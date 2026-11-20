using System.Text.Json;
using Kalla.Domain;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Evaluation.Generation;

/// <summary>
/// <c>generate-set</c> and <c>top-up-set</c> (processing design §13.1–§13.3): the collection's <c>generated</c> layer, an
/// advisory regression signal (weakness review §2). A round samples chunks with the seed <c>SHA-256(setId ‖ round)</c>
/// (the round is <c>EvaluationSets."Round"</c>, advanced by every round that generated, even one that kept nothing), asks <c>Settings.EvaluationModel</c> for a question and answer per chunk, filters the candidates
/// (<see cref="QuestionFilters"/>; the judge is <c>Settings.EvaluationJudgeModel</c>, default the same model) and stores the
/// kept ones with their expected documents (the source document and every live document with a chunk of the same
/// <c>TextHash</c>, at most <see cref="MaxExpectedDocuments"/>) until the layer has its target size. <c>top-up-set</c>
/// first retires stale questions: an expected document is deleted, or the source document's content changed and the judge
/// no longer rates the question answerable from that document's best-matching chunk (only changed documents are
/// re-checked). Both kinds generate only what is missing, so a retried job adds nothing to a full layer. Every model call
/// runs at the collection's ceiling (<see cref="EvalModelClient.CeilingForAsync"/>); a model the gateway does not serve there
/// refuses the job with <see cref="EvalModelClient.ModelNotAtCeiling"/>. Result <see cref="EvalSetResult"/>. Every model call
/// (<c>eval-generate</c>, <c>eval-judge</c>, the near-duplicate embeddings <c>eval-dedup</c>) carries the job's id as
/// <c>x-fors-run-id</c>, runs as the collection's gateway key and is counted under the collection; the round's commit adds them
/// to <c>CollectionDailyUsage</c> (engine design §7.7, §10.6).
/// </summary>
public sealed partial class EvalSetGenerator(EvalSetStore store, EvalModelClient models, EmbeddingClient embeddings, IEmbeddingProfileStore profiles,
    QuestionFilters filters, ITokenCounter tokens, ILogger<EvalSetGenerator> logger) : IEvalSetHandler
{
    public const string InvalidPayload = "eval.invalid_payload";
    public const string UnknownKind = "eval.unknown_kind";
    public const string CollectionUnknown = "collection.unknown";
    public const string ModelUnset = "eval.model_unset";
    public const string ProfileUnknown = "eval.embedding_profile_unknown";

    /// <summary>Eligible chunks have a body of at least this many tokens (processing design §13.1).</summary>
    public const int MinBodyTokens = 50;

    public const int MaxExpectedDocuments = 10;
    public const int MaxAnswerChars = 1_000;
    public const string DedupStep = "eval-dedup";

    private const int EmbedBatch = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JobResult> ExecuteAsync(JobContext context, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var job = context.Job;
        EvalSetPayload? payload = kind switch
        {
            EvalKinds.GenerateSet => SyncJobs.Read<EvalGenerateSetPayload>(job),
            EvalKinds.TopUpSet => SyncJobs.Read<EvalTopUpSetPayload>(job),
            _ => null,
        };
        if (payload is null)
        {
            return new JobResult.FatalError(kind is EvalKinds.GenerateSet or EvalKinds.TopUpSet ? InvalidPayload : UnknownKind);
        }

        try
        {
            return await RoundAsync(job, payload.CollectionId, kind, context.Usage, cancellationToken);
        }
        catch (EvalStopException stop)
        {
            var code = stop.Result switch
            {
                JobResult.FatalError fatal => fatal.Code,
                JobResult.TransientError transient => transient.Code,
                _ => string.Empty,
            };
            LogStopped(logger, job.Id, code);
            return stop.Result;
        }
        catch (StageTransientException transient)
        {
            return StageResults.From(transient);
        }
        catch (StageFatalException fatal)
        {
            return new JobResult.FatalError(fatal.Reason);
        }
    }

    private async Task<JobResult> RoundAsync(LeasedJob job, Guid collectionId, string kind, JobUsage jobUsage, CancellationToken cancellationToken)
    {
        if (await store.CollectionAsync(collectionId, cancellationToken) is not { } collection)
        {
            return new JobResult.FatalError(CollectionUnknown);
        }

        var settings = await store.SettingsAsync(cancellationToken);
        if (settings.Model is not { } model)
        {
            return new JobResult.FatalError(ModelUnset);
        }

        var judgeModel = settings.JudgeModel ?? model;
        var ceiling = await models.CeilingForAsync(collection.Classification, cancellationToken);
        await models.RequireAtCeilingAsync([model, judgeModel], ceiling, cancellationToken);
        if (await profiles.GetAsync(collection.EmbeddingProfileId, cancellationToken) is not { } profile)
        {
            return new JobResult.FatalError(ProfileUnknown);
        }

        var setId = await store.EnsureSetAsync(collection, cancellationToken);
        var round = await store.RoundAsync(setId, cancellationToken);
        var seed = ChunkSampler.Seed(setId, round);
        var usage = new RoundCalls(job.Id, new UsageSink(jobUsage, collection.UsageOwner));
        var active = await store.ActiveAsync(setId, cancellationToken);
        var (stale, rechecked) = kind == EvalKinds.TopUpSet
            ? await CheckStaleAsync(job, active, judgeModel, ceiling, usage, cancellationToken)
            : ([], []);
        var remaining = active.Where(q => !stale.Contains(q.Id)).ToList();

        var liveDocuments = await store.LiveDocumentsAsync(collection.Id, cancellationToken);
        var eligible = await store.EligibleAsync(collection.Id, MinBodyTokens, cancellationToken);
        var target = ChunkSampler.Target(settings.MaxQuestions, liveDocuments, eligible.Count);
        var needed = target - remaining.Count;
        var origin = new RoundOrigin(model, judgeModel, ceiling, round, Convert.ToHexStringLower(seed), liveDocuments);
        var added = new List<NewQuestion>();
        var generated = 0;
        if (needed > 0)
        {
            var used = remaining.Select(q => q.SourceChunkId).OfType<Guid>().ToHashSet();
            var candidates = ChunkSampler.Candidates(needed);
            var ranking = ChunkSampler.Rank([.. eligible.Where(c => !used.Contains(c.ChunkId))], seed, target, liveDocuments, candidates);
            var sample = await SampleAsync(ranking, candidates, cancellationToken);
            var kept = new List<float[]>(await EmbedAsync(profile, [.. remaining.Select(q => q.Question)], usage, cancellationToken));
            var accepted = new List<(EvalChunk Chunk, GeneratedCandidate Candidate, JudgeScores Scores)>();
            foreach (var chunk in sample)
            {
                if (accepted.Count >= needed)
                {
                    break;
                }

                generated++;
                var (candidate, scores, reason) = await CandidateAsync(chunk, origin, usage, cancellationToken);
                if (reason is null)
                {
                    var vector = (await EmbedAsync(profile, [candidate!.Question], usage, cancellationToken))[0];
                    if (QuestionFilters.IsDuplicate(vector, kept))
                    {
                        reason = FilterReasons.Duplicate;
                    }
                    else
                    {
                        kept.Add(vector);
                        accepted.Add((chunk, candidate, scores!));
                    }
                }

                if (reason is not null)
                {
                    LogRejected(logger, job.Id, chunk.ChunkId, reason);
                }
            }

            var duplicates = await store.SameTextDocumentsAsync(collection.Id, [.. accepted.Select(a => a.Chunk.TextHash)], cancellationToken);
            added.AddRange(accepted.Select(a => Question(a.Chunk, a.Candidate, a.Scores, origin, duplicates[Db.Hex(a.Chunk.TextHash)])));
        }

        var result = new EvalSetResult
        {
            SetId = setId,
            Round = round,
            Generated = generated,
            Kept = added.Count,
            Retired = stale.Count,
            Tokens = usage.Tokens,
        };
        LogRound(logger, job.Id, collection.Id, kind, round, target, generated, added.Count, stale.Count);
        var write = new EvalSetWrite([.. stale], rechecked, added, NextRound: generated > 0 ? round + 1 : null);
        return await store.CommitAsync(job, setId, collection.Classification, "system:" + kind, write, result, jobUsage, cancellationToken);
    }

    /// <summary>The chunks to generate from: the ranking's first <paramref name="count"/> with a body of at least <see cref="MinBodyTokens"/>.</summary>
    private async Task<IReadOnlyList<EvalChunk>> SampleAsync(IReadOnlyList<SampleChunk> ranking, int count, CancellationToken cancellationToken)
    {
        var sample = new List<EvalChunk>();
        for (var offset = 0; offset < ranking.Count && sample.Count < count; offset += count)
        {
            var batch = await store.ChunksAsync([.. ranking.Skip(offset).Take(count).Select(c => c.ChunkId)], cancellationToken);
            sample.AddRange(batch.Where(c => tokens.Count(c.Text) >= MinBodyTokens).Take(count - sample.Count));
        }

        return sample;
    }

    /// <summary>Generation, then filters 1–3; the reason is null when the candidate passed them.</summary>
    private async Task<(GeneratedCandidate? Candidate, JudgeScores? Scores, string? Reason)> CandidateAsync(EvalChunk chunk, RoundOrigin round,
        RoundCalls usage, CancellationToken cancellationToken)
    {
        var reply = await models.CompleteAsync(round.Model, EvalModelClient.GenerateStep, round.Ceiling, EvalPrompts.GenerateSystem,
            EvalPrompts.GenerateUser(chunk.Title, chunk.HeadingPath, chunk.Text), usage.RunId, usage.Sink, cancellationToken);
        usage.Tokens += reply.Tokens;
        if (EvalPrompts.ReadCandidate(reply.Content) is not { } candidate)
        {
            return (null, null, FilterReasons.Format);
        }

        if ((filters.Rules(candidate.Question) ?? QuestionFilters.Leakage(candidate.Question, candidate.Answer, chunk.Text)) is { } reason)
        {
            return (candidate, null, reason);
        }

        var scores = await JudgeAsync(chunk, candidate.Question, candidate.Answer, round.JudgeModel, round.Ceiling, usage, cancellationToken);
        return scores is not null && QuestionFilters.Passes(scores) ? (candidate, scores, null) : (candidate, scores, FilterReasons.Judge);
    }

    private async Task<JudgeScores?> JudgeAsync(EvalChunk chunk, string question, string answer, string judgeModel, Residency ceiling, RoundCalls usage,
        CancellationToken cancellationToken)
    {
        var reply = await models.CompleteAsync(judgeModel, EvalModelClient.JudgeStep, ceiling, EvalPrompts.JudgeSystem,
            EvalPrompts.JudgeUser(chunk.Title, chunk.HeadingPath, chunk.Text, question, answer), usage.RunId, usage.Sink, cancellationToken);
        usage.Tokens += reply.Tokens;
        return EvalPrompts.ReadScores(reply.Content);
    }

    /// <summary>
    /// Processing design §13.3: stale when an expected document is gone (deleted or purged); when only the source
    /// document's content changed, stale unless the judge still rates the question answerable (≥ 4) from that document's
    /// best-matching live chunk (most of the question's content words), and then the question takes the new content hash.
    /// </summary>
    private async Task<(HashSet<Guid> Stale, List<(Guid, byte[])> Rechecked)> CheckStaleAsync(LeasedJob job, IReadOnlyList<ActiveQuestion> active,
        string judgeModel, Residency ceiling, RoundCalls usage, CancellationToken cancellationToken)
    {
        var stale = new HashSet<Guid>();
        var rechecked = new List<(Guid, byte[])>();
        var documents = await store.DocumentsAsync([.. active.SelectMany(q => q.ExpectedDocumentIds).Distinct()], cancellationToken);
        foreach (var question in active)
        {
            if (question.ExpectedDocumentIds.Any(id => !documents.TryGetValue(id, out var document) || document.Deleted))
            {
                stale.Add(question.Id);
                LogStale(logger, job.Id, question.Id, "document.gone");
                continue;
            }

            if (question.SourceDocumentId is not { } source || question.SourceContentHash is not { } hash || !documents.TryGetValue(source, out var now)
                || (now.LiveContentHash is { } live && live.AsSpan().SequenceEqual(hash)))
            {
                continue;
            }

            var best = (await store.LiveChunksAsync(source, cancellationToken)).MaxBy(c => QuestionFilters.CopiedShare(question.Question, c.Text));
            var scores = best is null ? null : await JudgeAsync(best, question.Question, question.ExpectedAnswer, judgeModel, ceiling, usage, cancellationToken);
            if (now.LiveContentHash is { } changed && scores is { Answerable: >= QuestionFilters.MinJudgeScore })
            {
                rechecked.Add((question.Id, changed));
                continue;
            }

            stale.Add(question.Id);
            LogStale(logger, job.Id, question.Id, "document.changed");
        }

        return (stale, rechecked);
    }

    private async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingProfileRecord profile, IReadOnlyList<string> texts, RoundCalls usage,
        CancellationToken cancellationToken)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(EmbedBatch))
        {
            vectors.AddRange(await embeddings.EmbedAsync(profile, batch, new EmbeddingCall(usage.RunId, usage.Sink, DedupStep), cancellationToken));
        }

        return vectors;
    }

    private static NewQuestion Question(EvalChunk chunk, GeneratedCandidate candidate, JudgeScores scores, RoundOrigin round, IEnumerable<Guid> sameText)
    {
        Guid[] expected = [chunk.DocumentId, .. sameText.Where(d => d != chunk.DocumentId).Order().Take(MaxExpectedDocuments - 1)];
        var origin = new QuestionOrigin(round.Model, round.JudgeModel, EvalPrompts.PromptVersion, round.Round, round.Seed, chunk.DocumentId, candidate.Type,
            scores, new QuestionStrata(chunk.DocumentType, chunk.ContentKind), round.Documents);
        var answer = candidate.Answer.Length > MaxAnswerChars ? candidate.Answer[..MaxAnswerChars] : candidate.Answer;
        return new NewQuestion(Guid.CreateVersion7(), candidate.Question, answer, expected, chunk.ContentHash, JsonSerializer.Serialize(origin, Json), chunk.ChunkId);
    }

    [LoggerMessage(LogLevel.Debug, "Eval job {JobId}: the candidate of chunk {ChunkId} was dropped ({Reason})")]
    private static partial void LogRejected(ILogger logger, Guid jobId, Guid chunkId, string reason);

    [LoggerMessage(LogLevel.Information, "Eval job {JobId}: question {QuestionId} is stale ({Reason})")]
    private static partial void LogStale(ILogger logger, Guid jobId, Guid questionId, string reason);

    [LoggerMessage(LogLevel.Information,
        "Eval job {JobId}: collection {CollectionId} {Kind} round {Round}: target {Target}, generated {Generated}, kept {Kept}, retired {Retired}")]
    private static partial void LogRound(ILogger logger, Guid jobId, Guid collectionId, string kind, int round, int target, int generated, int kept, int retired);

    [LoggerMessage(LogLevel.Warning, "Eval job {JobId} stopped: {Code}")]
    private static partial void LogStopped(ILogger logger, Guid jobId, string code);

    /// <summary>The round's model calls: the run id they carry (the job's), where their usage goes, and the tokens the result reports.</summary>
    private sealed class RoundCalls(Guid runId, UsageSink sink)
    {
        public Guid RunId => runId;

        public UsageSink Sink => sink;

        public int Tokens { get; set; }
    }

    private sealed record RoundOrigin(string Model, string JudgeModel, Residency Ceiling, int Round, string Seed, int Documents);

    /// <summary>
    /// <c>EvaluationQuestions.Origin</c> of a generated question (processing design §13.1: model, prompt version, seed, judge
    /// scores, strata), with <c>D</c> at the round (<see cref="EvalSetTriggers"/>: a top-up when <c>D</c> grew by 25 %).
    /// </summary>
    private sealed record QuestionOrigin(string Model, string JudgeModel, string PromptVersion, int Round, string Seed, Guid SourceDocumentId, string? Type,
        JudgeScores Judge, QuestionStrata Strata, int Documents);

    private sealed record QuestionStrata(string DocumentType, string ContentKind);
}
