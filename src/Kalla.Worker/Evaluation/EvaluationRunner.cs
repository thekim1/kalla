using System.Text.Json;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Retrieval;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing;

namespace Kalla.Worker.Evaluation;

/// <summary>
/// The retrieval runs of the <c>eval</c> class (processing design §13.4–§13.6, §14): <c>on-demand</c> (1–3 variants,
/// each once more under <c>candidateSettings</c>), <c>publish-report</c> (compared with the last run before the publish)
/// and <c>nightly</c> (compared with the previous run, <c>rag.evaluation.recall_at_10</c> and the recall alert). An
/// <c>on-demand</c> run also stores its <c>Candidate</c> flag and its coverage threshold proposal (<c>ThresholdProposal</c>,
/// #129); a <c>hybrid-rerank</c> or <c>hybrid-rerank-gateway</c> run sets <c>Reranked</c>. Rerank is optional (#414): a
/// reranked variant whose reranker has no platform model is skipped (<c>no_rerank_model</c>), not run as all fallbacks. Every run reports its search latency, and a reranked run its
/// rerank latency, cost per question and <c>rerank_unavailable</c> count (<see cref="EvaluationSearchStats"/>, #399). Each job
/// searches the set as it was at the start, then writes its runs, their per-question results and its completion in one
/// transaction. A question whose expected document is gone is not searched and counts as <c>documentGone</c>, not as a miss;
/// the run makes such a <c>human</c> question <c>stale</c>, and a <c>stale</c> one whose documents are back active again, in
/// the same transaction (processing design §13.3). A question whose embedding fails fails the run (knowledge design §5.5):
/// the job is retried as the gateway error says, and nothing of it is kept. Each run's model calls (query embeddings that missed the cache, rerank
/// calls) carry its id as <c>x-fors-run-id</c> and are counted under the collection; the job's commit adds them to
/// <c>CollectionDailyUsage</c> (processing design §14.7; engine design §10.6).
/// </summary>
public sealed partial class EvaluationRunner(
    EvaluationStore store,
    RetrievalEvaluator evaluator,
    IJobQueue queue,
    WorkerIdentity identity,
    EvaluationMetrics metrics,
    TimeProvider time,
    ILogger<EvaluationRunner> logger)
{
    public const string CollectionUnknown = "collection.unknown";
    public const string NoQuestions = "eval.no_questions";
    public const string InvalidSettings = "eval.invalid_settings";
    public const string InvalidCandidateSettings = "eval.invalid_candidate_settings";
    public const string SearchUnavailable = "eval.search_unavailable";
    public const string EmbeddingResidency = "eval.embedding_residency";

    public Task<JobResult> OnDemandAsync(LeasedJob job, EvalOnDemandPayload payload, JobUsage usage, CancellationToken cancellationToken) =>
        GuardedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (await store.LoadAsync(payload.CollectionId, cancellationToken) is not { } target)
            {
                return new JobResult.FatalError(CollectionUnknown);
            }

            if (target.SetId is null || !target.HasQuestions)
            {
                return new JobResult.FatalError(NoQuestions);
            }

            RetrievalSettings? candidate = null;
            if (payload.CandidateSettings is { ValueKind: JsonValueKind.Object } json)
            {
                try
                {
                    candidate = RetrievalSettingsResolver.Collection(target.Platform, json.GetRawText());
                }
                catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
                {
                    return new JobResult.FatalError(InvalidCandidateSettings);
                }
            }

            var passes = new List<(RetrievalSettings Settings, bool Candidate)> { (target.Settings, false) };
            if (candidate is not null)
            {
                passes.Add((candidate, true));
            }

            using var session = evaluator.Open(target);
            var calls = Calls(target, usage);
            var records = new List<EvaluationRunRecord>();
            var refs = new List<EvaluationRunRef>();
            var skipped = new List<SkippedVariant>();
            foreach (var variant in payload.Variants)
            {
                if (EvaluationSession.Reranker(variant) is { } kind && target.Platform.RerankModelFor(kind) is null)
                {
                    skipped.Add(new SkippedVariant { Variant = variant, Reason = SkippedVariant.NoRerankModel });
                    continue;
                }

                foreach (var (settings, isCandidate) in passes)
                {
                    var (record, outcomes, search) = await RunAsync(session, EvaluationRunKinds.OnDemand, variant, settings, calls, cancellationToken);
                    var coverage = EvaluationScoring.Propose(outcomes);
                    record.Run.Candidate = isCandidate;
                    record.Run.ThresholdProposal = coverage is null ? null : ContractJson.Serialize(coverage);
                    records.Add(record);
                    refs.Add(new EvaluationRunRef
                    {
                        Variant = variant,
                        EvaluationRunId = record.Run.Id,
                        QuestionCount = record.Run.QuestionCount ?? 0,
                        Found = record.Run.Found ?? 0,
                        DocumentGone = target.Gone.Count,
                        Candidate = isCandidate,
                        Layers = EvaluationScoring.Layers(outcomes, target.Gone),
                        Coverage = coverage,
                        RecallAtK = (double?)record.Run.RecallAtK,
                        Mrr = (double?)record.Run.Mrr,
                        Search = search,
                    });
                }
            }

            metrics.Runs(EvaluationRunKinds.OnDemand, records.Count);
            return await store.CompleteAsync(job, queue, identity, target, records, new EvalOnDemandResult { Runs = refs, Skipped = skipped.Count > 0 ? skipped : null }, usage,
                cancellationToken);
        });

    /// <summary>The full set after a publish or rollback; nothing to write without questions (no report is shown).</summary>
    public Task<JobResult> PublishReportAsync(LeasedJob job, EvalPublishReportPayload payload, JobUsage usage, CancellationToken cancellationToken) =>
        GuardedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (await store.LoadAsync(payload.CollectionId, cancellationToken) is not { } target)
            {
                return new JobResult.FatalError(CollectionUnknown);
            }

            if (target.SetId is not { } setId || !target.HasQuestions)
            {
                return new JobResult.Success();
            }

            var publishedAt = await store.RunStartedAtAsync(payload.RunId, cancellationToken) ?? time.GetUtcNow();
            var compared = await store.LastComparableAsync(setId, publishedAt, cancellationToken);
            var version = await store.VersionNumberAsync(payload.PipelineVersionId, cancellationToken);
            using var session = evaluator.Open(target);
            var (record, outcomes, search) = await RunAsync(session, EvaluationRunKinds.PublishReport, EvaluationVariant.Hybrid, target.Settings, Calls(target, usage),
                cancellationToken, compared, payload.PipelineVersionId, version);
            metrics.Runs(EvaluationRunKinds.PublishReport, 1);
            return await store.CompleteAsync(job, queue, identity, target, [record], RunResult(target, record, outcomes, search, recallAlert: false), usage,
                cancellationToken);
        });

    /// <summary>
    /// The nightly run of a published collection (processing design §14.8); a collection unpublished, deleted or emptied
    /// since it was scheduled completes without a run.
    /// </summary>
    public Task<JobResult> NightlyAsync(LeasedJob job, EvalNightlyPayload payload, JobUsage usage, CancellationToken cancellationToken) =>
        GuardedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (await store.LoadAsync(payload.CollectionId, cancellationToken) is not { PublishedAt: not null, SetId: { } setId } target || !target.HasQuestions)
            {
                return new JobResult.Success();
            }

            var now = time.GetUtcNow();
            var compared = await store.LastComparableAsync(setId, now, cancellationToken);
            var history = await store.NightlyRecallsAsync(setId, now - EvaluationScoring.AlertWindow, now, cancellationToken);
            using var session = evaluator.Open(target);
            var (record, outcomes, search) = await RunAsync(session, EvaluationRunKinds.Nightly, EvaluationVariant.Hybrid, target.Settings, Calls(target, usage),
                cancellationToken, compared);
            var alert = EvaluationScoring.RecallAlert(record.Run.RecallAtK, history);
            if (record.Run.RecallAtK is { } recall)
            {
                metrics.Recall(target.CollectionId, recall);
                if (alert)
                {
                    LogRecallAlert(logger, target.CollectionId, recall, EvaluationScoring.Median(history));
                }
            }

            metrics.Runs(EvaluationRunKinds.Nightly, 1);
            return await store.CompleteAsync(job, queue, identity, target, [record], RunResult(target, record, outcomes, search, alert), usage, cancellationToken);
        });

    /// <summary>The <c>EvaluationRuns</c> text of a variant; both reranked variants are <c>hybrid</c> with <c>Reranked</c> set.</summary>
    public static string VariantText(EvaluationVariant variant) => variant switch
    {
        EvaluationVariant.Vector => EvaluationVariants.Vector,
        EvaluationVariant.HybridNoDecompounder => EvaluationVariants.HybridNoDecompounder,
        _ => EvaluationVariants.Hybrid,
    };

    private async Task<(EvaluationRunRecord Record, IReadOnlyList<QuestionOutcome> Outcomes, EvaluationSearchStats Search)> RunAsync(
        EvaluationSession session, string kind, EvaluationVariant variant, RetrievalSettings settings, UsageSink calls, CancellationToken cancellationToken,
        ComparedRun? compared = null, Guid? subjectId = null, int? subjectVersion = null)
    {
        var target = session.Target;
        var started = time.GetUtcNow();
        var runId = Guid.CreateVersion7();
        var reranker = EvaluationSession.Reranker(variant);
        var reranked = reranker is not null;
        var outcomes = await session.RunAsync(variant, settings, runId, calls, cancellationToken);
        var model = EvaluationScoring.RerankModel(outcomes, reranked, reranker is { } rerankerKind ? target.Platform.RerankModelFor(rerankerKind) : null);
        var price = model is null ? null : await store.PriceAsync(model, cancellationToken);
        var search = EvaluationScoring.Search(outcomes, reranked, model, price);
        var totals = EvaluationScoring.Totals(outcomes);
        var (wins, losses) = compared is null ? (0, 0) : EvaluationScoring.Compare(outcomes, compared);
        var run = new EvaluationRun
        {
            Id = runId,
            SetId = target.SetId!.Value,
            Classification = target.Classification,
            Kind = kind,
            SubjectId = subjectId,
            SubjectVersion = subjectVersion,
            ComparedRunId = compared?.Id,
            QuestionCount = totals.Questions,
            Found = totals.Found,
            Wins = compared is null ? null : wins,
            Losses = compared is null ? null : losses,
            RecallAtK = totals.RecallAtK,
            Mrr = totals.Mrr,
            Reranked = reranked,
            Variant = VariantText(variant),
            StartedAt = started,
            FinishedAt = time.GetUtcNow(),
        };
        return (new EvaluationRunRecord(run, [.. outcomes.Select(o => o.ToResult(run.Id, target.Classification))]), outcomes, search);
    }

    /// <summary>
    /// The collection's knowledge base as the owner of the job's model calls, in the attempt's <paramref name="usage"/>; the
    /// calls run as its gateway key.
    /// </summary>
    private static UsageSink Calls(EvaluationTarget target, JobUsage usage) =>
        new(usage, new UsageOwner(target.CollectionId, target.Classification, target.DepartmentId, target.GatewayKeyId));

    private static EvalRunResult RunResult(EvaluationTarget target, EvaluationRunRecord record, IReadOnlyList<QuestionOutcome> outcomes, EvaluationSearchStats search,
        bool recallAlert) => new()
    {
        EvaluationRunId = record.Run.Id,
        ComparedRunId = record.Run.ComparedRunId,
        QuestionCount = record.Run.QuestionCount ?? 0,
        Found = record.Run.Found ?? 0,
        DocumentGone = target.Gone.Count,
        Wins = record.Run.Wins ?? 0,
        Losses = record.Run.Losses ?? 0,
        RecallAlert = recallAlert,
        Layers = EvaluationScoring.Layers(outcomes, target.Gone),
        RecallAtK = (double?)record.Run.RecallAtK,
        Mrr = (double?)record.Run.Mrr,
        Search = search,
    };

    /// <summary>
    /// Maps the run's failures to the job's outcome: a failed query embedding as the gateway error says (a rate limit
    /// requeues without an attempt; the residency check is final), a refused or timed-out statement and a failed canary
    /// as transient, settings that do not resolve as final.
    /// </summary>
    private static async Task<JobResult> GuardedAsync(Func<Task<JobResult>> run)
    {
        try
        {
            return await run();
        }
        catch (QueryEmbeddingUnavailableException e) when (e.InnerException is StageTransientException transient)
        {
            return StageResults.From(transient);
        }
        catch (QueryEmbeddingUnavailableException e) when (e.Reason == Domain.Retrieval.RetrievalDegradation.EmbeddingResidency)
        {
            return new JobResult.FatalError(EmbeddingResidency);
        }
        catch (QueryEmbeddingUnavailableException e)
        {
            return new JobResult.FatalError(e.InnerException is StageFatalException fatal ? fatal.Reason : "eval.embedding_unavailable");
        }
        catch (StageTransientException e)
        {
            return StageResults.From(e);
        }
        catch (StageFatalException e)
        {
            return new JobResult.FatalError(e.Reason);
        }
        catch (SearchUnavailableException)
        {
            return new JobResult.TransientError(SearchUnavailable);
        }
        catch (FormatException)
        {
            return new JobResult.FatalError(InvalidSettings);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Collection {CollectionId}: nightly recall@10 {Recall} is 5 points or more below the 4-week median {Median}")]
    private static partial void LogRecallAlert(ILogger logger, Guid collectionId, decimal recall, decimal median);
}
