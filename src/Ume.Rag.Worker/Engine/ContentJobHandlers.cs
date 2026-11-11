using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>Maps a stage's transient refusal to the queue (engine design §7.3).</summary>
internal static class StageResults
{
    public static JobResult From(StageTransientException transient) => new JobResult.TransientError(transient.Code, transient.RetryAfter, transient.CountsAttempt);
}

/// <summary>
/// <c>fetch</c> jobs (engine design §7.4, §9.1): one frontier row of a URL list or a crawl, or one upload slot, fetched within the run's
/// pinned pipeline version; the document path runs <c>detect</c> to <c>personal-data</c> inline and commits the
/// continuation. A job of a run that is no longer running, or of a paused source, ends without effect.
/// </summary>
public sealed class FetchJobHandler(SourceContexts sources, RunStore runs, UrlListSource urlLists, UploadSource uploads, CrawlSourceRunner crawls,
    ConfigVersionState config) : IJobHandler
{
    public string JobClass => JobClasses.Fetch;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        if (SyncJobs.Read<FetchPayload>(context.Job) is not { } payload)
        {
            return new JobResult.FatalError("fetch.invalid_payload");
        }

        var source = await sources.GetAsync(payload.SourceId, cancellationToken);
        var run = await runs.GetAsync(payload.RunId, cancellationToken);
        if (source is not { Active: true } || run is not { State: RunStates.Running, PipelineVersionId: { } versionId })
        {
            return new JobResult.Success();
        }

        try
        {
            var pipeline = await sources.VersionAsync(versionId, source.Type, cancellationToken);
            return source.Type switch
            {
                SourceTypes.UrlList when payload.UrlHash is not null => await urlLists.FetchAsync(source, run, pipeline, payload, context.Job, cancellationToken),
                SourceTypes.Crawl when payload.UrlHash is not null => await crawls.FetchAsync(source, run, pipeline, payload, context.Job, cancellationToken),
                SourceTypes.Upload when payload.UploadId is not null => await uploads.FetchAsync(source, run, pipeline, payload, context.Job, cancellationToken),
                _ => new JobResult.FatalError(SyncJobHandler.Unsupported),
            };
        }
        catch (PipelineRefusedException refused)
        {
            return new JobResult.FatalError(refused.Reason);
        }
        catch (StageTransientException transient)
        {
            return StageResults.From(transient);
        }
    }
}

/// <summary>
/// <c>parse</c> jobs (engine design §7.4, §9.5): the parser-bound segment (<c>parse</c> to <c>personal-data</c>) of one
/// document from its cached <c>detect</c> output, committed with the <c>embed</c> continuation. The route stores each
/// completed range, so a reclaimed job resumes at the next range; after <c>Worker:Parse:SliceSeconds</c> (10 minutes) the
/// job yields at a range boundary: in one fenced transaction it completes and re-enqueues itself with <c>slice + 1</c> (a
/// new job, so no attempt is used). A URL document gets its host's applied site profile, as in the fetch path, so the
/// set's key matches what <c>EnsureCurrent</c> wants. A forced reprocess's bypass (<see cref="ParsePayload.BypassFrom"/>)
/// goes to the segment and to a yielded job's continuation.
/// </summary>
public sealed partial class ParseJobHandler(
    SourceContexts sources,
    DocumentStore documents,
    ProcessingSegment segment,
    DocumentIngest ingest,
    SiteProfiles siteProfiles,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    JobMetrics metrics,
    IOptions<ParseOptions> parseOptions,
    ConfigVersionState config,
    TimeProvider time,
    ILogger<ParseJobHandler> logger) : IJobHandler
{
    /// <summary><c>rag.jobs.requeued</c> reason of a yielded job (engine design §17).</summary>
    public const string SliceReason = "slice";

    /// <summary>The payload's upper bound for <c>slice</c> (<see cref="ParsePayload.Slice"/>); a job there runs to the end.</summary>
    public const int MaxSlice = 10_000;

    public string JobClass => JobClasses.Parse;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        var job = context.Job;
        if (SyncJobs.Read<ParsePayload>(job) is not { } payload || job.RunId is not { } runId)
        {
            return new JobResult.FatalError("parse.invalid_payload");
        }

        var document = await documents.GetAsync(payload.DocumentId, cancellationToken);
        var source = document is null ? null : await sources.GetAsync(document.SourceId, cancellationToken);
        if (document is null || source is null)
        {
            return new JobResult.Success();
        }

        if (document.CurrentExecutionId != payload.ExecutionId)
        {
            return await ingest.TerminalAsync(source, runId, job, document, document.CanonicalIdentity, Outcome.Superseded, "execution.superseded",
                payload.PipelineVersionId, null, cancellationToken);
        }

        try
        {
            var pipeline = await sources.VersionAsync(payload.PipelineVersionId, source.Type, cancellationToken);
            var profile = document.IdentityKind == IdentityKinds.Url
                ? await siteProfiles.AppliedAsync(document.Classification, document.Url, cancellationToken)
                : null;
            var input = new SegmentInput(document, pipeline, source.CollectionClassification, RawContentKeys.Of(document))
            {
                DeclaredMediaType = document.MediaType,
                SiteProfile = profile,
                Slice = payload.Slice < MaxSlice ? new ParseSliceClock(time, TimeSpan.FromSeconds(parseOptions.Value.SliceSeconds)) : null,
                BypassFrom = payload.BypassFrom,
            };
            var inputKey = Convert.FromHexString(payload.InputKey);
            var result = await segment.RunAsync(input, JobClasses.Parse, inputKey, cancellationToken);
            return result is SegmentResult.Yielded yielded
                ? await YieldAsync(source, runId, payload, job, document, inputKey, yielded, cancellationToken)
                : await ingest.CommitSegmentAsync(source, runId, payload.PipelineVersionId, job, document, payload.ExecutionId, result, null, cancellationToken);
        }
        catch (PipelineRefusedException refused)
        {
            return new JobResult.FatalError(refused.Reason);
        }
        catch (StageTransientException transient)
        {
            return StageResults.From(transient);
        }
    }

    /// <summary>
    /// The slice's end (engine design §9.5): the outputs reached, the job's completion and its <c>slice + 1</c> continuation
    /// in one fenced transaction. The completion goes first, so the continuation can take the same dedup key.
    /// </summary>
    private async Task<JobResult> YieldAsync(SourceContext source, Guid runId, ParsePayload payload, LeasedJob job, DocumentRow document, byte[] inputKey,
        SegmentResult.Yielded yielded, CancellationToken cancellationToken)
    {
        await using (var commit = await EngineCommit.BeginAsync(database, cancellationToken))
        {
            if (await commit.FenceAsync(document.Id, document.Classification, payload.ExecutionId, document.ContentHash!, cancellationToken))
            {
                await commit.AddOutputsAsync(yielded.Outputs, cancellationToken);
                if (!await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, null, cancellationToken))
                {
                    return new JobResult.Success();
                }

                var next = SyncJobs.Parse(source, document, payload.ExecutionId, payload.PipelineVersionId, runId, inputKey, payload.Slice + 1, payload.BypassFrom) with
                {
                    Priority = job.Priority,
                };
                await commit.EnqueueAsync(queue, [next], cancellationToken);
                await commit.CommitAsync(cancellationToken);
                metrics.Requeued(JobClasses.Parse, SliceReason);
                LogYielded(logger, document.Id, payload.Slice + 1);
                return new JobResult.Committed();
            }
        }

        return await ingest.TerminalAsync(source, runId, job, document, document.CanonicalIdentity, Outcome.Superseded, "execution.superseded",
            payload.PipelineVersionId, null, cancellationToken);
    }

    [LoggerMessage(LogLevel.Information, "Document {DocumentId}: the parse slice is used up; slice {Slice} continues at the next range")]
    private static partial void LogYielded(ILogger logger, Guid documentId, int slice);
}

/// <summary>
/// <c>embed</c> jobs: the <c>embed</c> and <c>index</c> stages of one document (engine design §7.4, §14; processing design
/// §11). The wanted set (document, pipeline version, site-profile version, the collection's embedding profile, content
/// hash, and the salt the segment computed, <see cref="EmbedPayload.ParseRuleKey"/>) that the live one already meets
/// (<see cref="ChunkSetKey.IsMetBy"/>) is <c>unchanged</c>; a <c>superseded</c> one is swapped back; when the live set was
/// built from the same chunks (a noise-only change, a version change no stage noticed, or a parse-rule change that left the
/// chunks as they were) it is re-keyed in one row, except for a forced reprocess, which always writes its own set; else the
/// chunks are embedded (cache first), written invisible, and swapped in one fenced transaction with the run item and the
/// job's completion. The swap re-checks that the source's published version is still the one written, so a slow write of
/// an old version never overtakes a newer one; a set of a <c>public</c> collection that personal data flagged goes in
/// <c>held</c>.
/// </summary>
public sealed partial class EmbedJobHandler(
    SourceContexts sources,
    DocumentStore documents,
    IndexWriter index,
    INodeOutputStore nodeOutputs,
    StageOutputs outputs,
    EmbedStage embed,
    IEmbeddingProfileStore profiles,
    IChunkSetStore chunkSets,
    ChunkSetSwap swaps,
    EngineSettings settings,
    DocumentIngest ingest,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    ConfigVersionState config,
    TimeProvider time,
    ILogger<EmbedJobHandler> logger) : IJobHandler
{
    public const string Republished = "pipeline.republished";

    public string JobClass => JobClasses.Embed;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        var job = context.Job;
        if (SyncJobs.Read<EmbedPayload>(job) is not { } payload)
        {
            return new JobResult.FatalError("embed.invalid_payload");
        }

        var document = await documents.GetAsync(payload.DocumentId, cancellationToken);
        var source = document is null ? null : await sources.GetAsync(document.SourceId, cancellationToken);
        if (document is not { ContentHash: { } contentHash } || source is null)
        {
            return new JobResult.Success();
        }

        if (document.CurrentExecutionId != payload.ExecutionId)
        {
            return await Terminal(source, payload, job, document, Outcome.Superseded, "execution.superseded", cancellationToken);
        }

        try
        {
            return await EmbedAsync(source, payload, job, document, contentHash, cancellationToken);
        }
        catch (PipelineRefusedException refused)
        {
            return new JobResult.FatalError(refused.Reason);
        }
        catch (StageTransientException transient)
        {
            return StageResults.From(transient);
        }
        catch (StageFatalException fatal)
        {
            return await Terminal(source, payload, job, document, fatal.Outcome, fatal.Reason, cancellationToken);
        }
    }

    private async Task<JobResult> EmbedAsync(SourceContext source, EmbedPayload payload, LeasedJob job, DocumentRow document, byte[] contentHash,
        CancellationToken cancellationToken)
    {
        await sources.VersionAsync(payload.PipelineVersionId, source.Type, cancellationToken);
        var inputKey = Convert.FromHexString(payload.InputKey);
        if (await nodeOutputs.FindAsync(inputKey, cancellationToken) is not { } inputRow ||
            await outputs.GetAsync<ChunkSetDraft>(inputKey, cancellationToken) is not { } input)
        {
            return await Terminal(source, payload, job, document, Outcome.Failed, "embed.input_missing", cancellationToken);
        }

        var draft = input.Value;
        var key = new ChunkSetKey(document.Id, payload.PipelineVersionId, document.SiteProfileVersionId, source.EmbeddingProfileId, contentHash,
            payload.ParseRuleKey);
        var live = await index.LiveAsync(document.Id, cancellationToken);
        if (live is not null && key.IsMetBy(live.Key))
        {
            return await Terminal(source, payload, job, document, Outcome.Unchanged, null, cancellationToken);
        }

        var existing = await index.FindAsync(key, cancellationToken);

        if (existing is { State: ChunkSetStates.Superseded })
        {
            // Rollback within 14 days (engine design §12.3, §14 rule 1): swapped back, nothing written.
            var stored = await chunkSets.GetAsync(existing.Id, cancellationToken) ?? throw new InvalidOperationException("The set vanished.");
            return await SwapAsync(source, payload, job, document, contentHash, stored, live, swappedBack: true, cancellationToken);
        }

        if (existing is { State: ChunkSetStates.Writing })
        {
            // Left by an attempt that died between its batches and its swap: invisible, so it is replaced.
            await index.DeleteWritingAsync(existing.Id, cancellationToken);
        }

        if (existing is null && live is not null && !ChunkSetSalt.IsForced(key.ParseRuleKey) && live.Key.EmbeddingProfileId == key.EmbeddingProfileId &&
            live.InputHash.AsSpan().SequenceEqual(inputRow.InputHash))
        {
            return await ReKeyAsync(source, payload, job, document, contentHash, live, key, cancellationToken);
        }

        var profile = await profiles.GetAsync(source.EmbeddingProfileId, cancellationToken)
            ?? throw new StageFatalException(Outcome.Failed, "embed.profile_unknown");
        var embedded = await embed.RunAsync(draft, document.Classification, profile.Id, new EmbeddingCall(payload.RunId), cancellationToken);
        var values = await settings.GetAsync(cancellationToken);
        var counts = draft.Pii ?? new PiiCounts();
        var set = new ChunkSetRecord(Guid.CreateVersion7(), key, document.Classification, source.CollectionId, inputRow.InputHash)
        {
            StageKeys = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["personal-data"] = new { key = payload.InputKey, inputHash = Convert.ToHexStringLower(inputRow.InputHash) },
            }),
            PiiFlagged = PiiFlagPolicy.Fired(counts, values.PiiFlagRules).Count > 0,
            PiiCounts = counts,
            ChunkCount = draft.Chunks.Count,
        };
        var now = time.GetUtcNow();
        var rows = draft.Chunks.Select((c, i) => new ChunkRecord(Guid.CreateVersion7(), set.Id, document.Id, source.CollectionId, document.Classification, c.Ordinal, c,
            profile.Id, embedded.Vectors[i]) { DeletedAt = now }).ToList();
        // Guarded: an admin may have changed the profile while this document was embedded (B-30, #554 review).
        await index.WriteInvisibleAsync(set, rows, profile, cancellationToken);
        return await SwapAsync(source, payload, job, document, contentHash, set, live, swappedBack: false, cancellationToken);
    }

    private async Task<JobResult> SwapAsync(SourceContext source, EmbedPayload payload, LeasedJob job, DocumentRow document, byte[] contentHash,
        ChunkSetRecord set, ChunkSetRow? live, bool swappedBack, CancellationToken cancellationToken) =>
        await swaps.SwapAsync(new SwapRequest(source, document, payload.ExecutionId, contentHash, payload.PipelineVersionId, payload.RunId, set, live,
            swappedBack, job), cancellationToken) switch
        {
            SwapResult.Swapped => new JobResult.Committed(),
            SwapResult.LeaseLost => new JobResult.Success(),
            SwapResult.Fenced => await Terminal(source, payload, job, document, Outcome.Superseded, "execution.superseded", cancellationToken),
            _ => await Terminal(source, payload, job, document, Outcome.Superseded, Republished, cancellationToken),
        };

    private async Task<JobResult> ReKeyAsync(SourceContext source, EmbedPayload payload, LeasedJob job, DocumentRow document, byte[] contentHash, ChunkSetRow live,
        ChunkSetKey key, CancellationToken cancellationToken)
    {
        await using (var commit = await EngineCommit.BeginAsync(database, cancellationToken))
        {
            if (await commit.FenceAsync(document.Id, document.Classification, payload.ExecutionId, contentHash, cancellationToken) &&
                await ChunkSetSwap.IsPublishedAsync(commit, source.PipelineId, payload.PipelineVersionId, cancellationToken))
            {
                await IndexWriter.ReKeyAsync(commit.Connection, commit.Transaction, live.Id, key, cancellationToken);
                await commit.AddRunItemAsync(new RunItemRow(payload.RunId, document.Classification, document.Id, document.CanonicalIdentity, Outcome.Unchanged)
                {
                    Reason = ReKeyReason(live.Key, key),
                    Strategy = document.ParseStrategy,
                    PipelineVersionId = payload.PipelineVersionId,
                    ExecutionId = payload.ExecutionId,
                    ContentHash = contentHash,
                }, cancellationToken);
                if (!await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, null, cancellationToken))
                {
                    return new JobResult.Success();
                }

                await commit.CommitAsync(cancellationToken);
                LogSwapped(logger, document.Id, live.Id, "rekeyed", live.State);
                return new JobResult.Committed();
            }
        }

        return await Terminal(source, payload, job, document, Outcome.Superseded, Republished, cancellationToken);
    }

    private Task<JobResult> Terminal(SourceContext source, EmbedPayload payload, LeasedJob job, DocumentRow document, Outcome outcome, string? reason,
        CancellationToken cancellationToken) =>
        ingest.TerminalAsync(source, payload.RunId, job, document, document.CanonicalIdentity, outcome, reason, payload.PipelineVersionId, null, cancellationToken);

    /// <summary>A re-key's reason: another version (<c>version.unaffected</c>), only another rule part (<c>rule.unaffected</c>), else new content (<c>change.noise</c>).</summary>
    private static string ReKeyReason(ChunkSetKey live, ChunkSetKey key) =>
        live.PipelineVersionId != key.PipelineVersionId ? "version.unaffected"
        : live.ContentHash.AsSpan().SequenceEqual(key.ContentHash) && live.SiteProfileVersionId == key.SiteProfileVersionId &&
          !string.Equals(live.ParseRuleKey, key.ParseRuleKey, StringComparison.Ordinal) ? "rule.unaffected"
        : "change.noise";

    [LoggerMessage(LogLevel.Information, "Document {DocumentId}: set {ChunkSetId} {Kind}, now {State}")]
    private static partial void LogSwapped(ILogger logger, Guid documentId, Guid chunkSetId, string kind, string state);
}
