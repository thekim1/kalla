using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>One <c>fetch</c> job's document: the source, run and pinned version, the job, and where the outcome goes.</summary>
/// <param name="Source">The source.</param>
/// <param name="Run">The run the job serves.</param>
/// <param name="Pipeline">The run's pipeline version (engine design §15: carried, not re-resolved).</param>
/// <param name="Job">The leased job (completed in the commit).</param>
/// <param name="IdentityKind"><c>url</c> or <c>upload</c>.</param>
/// <param name="Identity">The listed identity (the frontier URL or <c>upload:&lt;slot&gt;</c>); a fetched page's final URL replaces it.</param>
/// <param name="FrontierUrlHash">The source's frontier row the fetch completes in its commit (URL lists, crawls), or null.</param>
public sealed record FetchCase(SourceContext Source, RunRow Run, PipelineRun Pipeline, LeasedJob Job, string IdentityKind, string Identity, byte[]? FrontierUrlHash)
{
    public string? DisplayName { get; init; }

    /// <summary>The frontier row's completion as the crawl decided it (facts, redirect, failed seed); null: from the outcome.</summary>
    public FrontierCompletion? Frontier { get; init; }

    /// <summary>The reason of an <c>unchanged</c> item (<c>crawl.lastmod_skip</c>), when the fetch says why.</summary>
    public string? UnchangedReason { get; init; }
}

/// <summary>What <see cref="DocumentIngest.EnsureCurrentAsync"/> did with a document.</summary>
public enum EnsureOutcome
{
    /// <summary>The live set has the wanted key: nothing written (the caller records <c>unchanged</c>).</summary>
    Current,

    /// <summary>The segment ran from the caches and its continuation, or its end, was committed.</summary>
    Committed,
}

/// <summary>
/// The engine side of a fetch (connectors design §2.2; engine design §2.3, §7.4–§7.6): new or changed content commits the
/// document with a new execution id and runs its processing segment; the same content (or a 304) runs
/// <c>EnsureCurrent</c>, which answers <c>unchanged</c> when the live set already has the wanted key and otherwise
/// re-runs the segment from the caches. The segment's outputs, the document facts, the continuation (<c>embed</c>, or
/// <c>parse</c> for parser-bound documents), the frontier row and the job's completion commit in one fenced transaction;
/// every other outcome is a run item committed with the job. Run items are written only at a terminal outcome.
/// </summary>
public sealed partial class DocumentIngest(
    JobDatabase database,
    IJobQueue queue,
    DocumentStore documents,
    IndexWriter index,
    ChunkSetSwap swaps,
    IChunkSetStore chunkSets,
    ProcessingSegment segment,
    SiteProfiles siteProfiles,
    IQuarantineStore quarantine,
    WorkerIdentity identity,
    ILoggerFactory loggers,
    TimeProvider time,
    ILogger<DocumentIngest> logger)
{
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public async Task<JobResult> HandleAsync(FetchCase fetch, FetchOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var now = time.GetUtcNow();
        var known = await documents.FindAsync(fetch.Source.SourceId, fetch.IdentityKind, fetch.Identity, cancellationToken);
        switch (outcome)
        {
            case FetchOutcome.Content content:
                var fetched = new FetchedDocument(fetch.Source.SourceId, fetch.Source.Classification, fetch.IdentityKind, content.Facts.CanonicalIdentity,
                    Convert.FromHexString(content.Raw.ContentHash), (short)content.Raw.ContentHashVersion, content.Facts.FinalUrl?.AbsoluteUri,
                    fetch.DisplayName, content.Facts.CanonicalHint);
                var upsert = await documents.UpsertFetchedAsync(fetched, fetch.Run.Id, now, cancellationToken);
                var frontier = Frontier(fetch, 200, content.Validators, fetched.ContentHash);
                return await ProcessAsync(fetch, upsert.Document, content.Raw.Key, content.Raw.MediaType, upsert.ContentChanged, frontier, cancellationToken);

            case FetchOutcome.NotModified notModified when known is { DeletedAt: null, ContentHash: not null }:
                await documents.SeenAsync(known.Id, fetch.Run.Id, now, cancellationToken);
                return await ProcessAsync(fetch, known, RawContentKeys.Of(known), known.MediaType, changed: false,
                    Frontier(fetch, 304, notModified.Validators, null), cancellationToken);

            case FetchOutcome.NotModified notModified:
                // A 304 for a URL with no live document (validators outlived it): fetched again in full at the next run.
                return await TerminalAsync(fetch, known, Outcome.Unchanged, "http.304_without_document", Frontier(fetch, 304, notModified.Validators, null), cancellationToken);

            case FetchOutcome.Gone gone:
                return await TerminalAsync(fetch, known, Outcome.Failed, gone.Reason, Frontier(fetch, StatusOf(gone.Reason), null, null), cancellationToken);

            case FetchOutcome.Skipped skipped:
                return await TerminalAsync(fetch, known, Outcome.Skipped, skipped.Reason, Frontier(fetch, null, null, null), cancellationToken);

            case FetchOutcome.Refused refused:
                return await TerminalAsync(fetch, known, refused.Outcome switch
                {
                    RunItemOutcome.TooLarge => Outcome.TooLarge,
                    RunItemOutcome.Blocked => Outcome.Blocked,
                    _ => Outcome.Unsupported,
                }, refused.Reason, Frontier(fetch, null, null, null), cancellationToken);

            case FetchOutcome.Quarantined finding:
                var context = new FetchContext(fetch.Source.SourceId, fetch.Source.Classification, fetch.Run.Id, fetch.Source.Settings, false);
                var record = QuarantineRecord.For(finding, context, fetch.DisplayName, fetch.IdentityKind == IdentityKinds.Url ? new Uri(fetch.Identity) : null, now);
                await quarantine.AddAsync(record, cancellationToken);
                SecurityEvents.MalwareDetected(_security, finding.Signature, finding.Sha256, record.Origin, fetch.Source.SourceId, fetch.Run.Id, known?.Id,
                    fetch.Source.Classification);
                return await TerminalAsync(fetch, known, Outcome.Malware, finding.Reason, Frontier(fetch, 200, null, null), cancellationToken);

            case FetchOutcome.Transient transient when transient.CountsAttempt && fetch.Job.Attempts >= fetch.Job.MaxAttempts:
                // The last attempt: the document fails for the run instead of dying without a run item.
                await FailedAsync(known, cancellationToken);
                return await TerminalAsync(fetch, known, Outcome.Failed, transient.Reason, Frontier(fetch, null, null, null), cancellationToken);

            case FetchOutcome.Transient transient:
                return new JobResult.TransientError(transient.Reason, transient.RetryAfter, transient.CountsAttempt);

            case FetchOutcome.Failed failed:
                await FailedAsync(known, cancellationToken);
                return await TerminalAsync(fetch, known, Outcome.Failed, failed.Reason, Frontier(fetch, StatusOf(failed.Reason), null, null), cancellationToken);

            default:
                throw new InvalidOperationException($"Unexpected fetch outcome {outcome.GetType().Name}.");
        }
    }

    /// <summary>
    /// <c>EnsureCurrent</c> and the processing segment of one document (engine design §2.3, §7.4): unchanged when the live set
    /// already has the wanted key, else the stages from the caches, committed with the continuation.
    /// </summary>
    public async Task<JobResult> ProcessAsync(FetchCase fetch, DocumentRow document, BlobKey rawKey, string? declaredMediaType, bool changed,
        FrontierCompletion? frontier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(document);
        var profile = document.IdentityKind == IdentityKinds.Url
            ? await siteProfiles.AppliedAsync(document.Classification, document.Url, cancellationToken)
            : null;
        if (!changed && await IsCurrentAsync(fetch.Source, fetch.Pipeline.VersionId, document, profile?.Id, cancellationToken))
        {
            return await TerminalAsync(fetch, document, Outcome.Unchanged, fetch.UnchangedReason, frontier, cancellationToken);
        }

        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("A fetched document has an execution id.");
        var input = new SegmentInput(document, fetch.Pipeline, fetch.Source.CollectionClassification, rawKey)
        {
            DeclaredMediaType = declaredMediaType,
            SiteProfile = profile,
        };
        var result = await segment.RunAsync(input, JobClasses.Fetch, null, cancellationToken);
        return await CommitSegmentAsync(fetch.Source, fetch.Run.Id, fetch.Pipeline.VersionId, fetch.Job, document, executionId, result, frontier, cancellationToken);
    }

    /// <summary>
    /// <c>EnsureCurrent</c> of a <c>reprocess</c> run (engine design §2.3, §9.2, §12.2, §12.3): one live document against
    /// <paramref name="pipeline"/> and its host's current site-profile version, without a fetch. A current live set is
    /// left alone; a <c>superseded</c> set with the wanted key (kept 14 days: a rollback of the pipeline or of the host's
    /// site profile) is swapped back here, with no <c>parse</c> or <c>embed</c> job; otherwise the segment runs from the
    /// first stage without cached output and its continuation (<c>parse</c> or <c>embed</c>, where a set differing only by
    /// label is re-keyed) commits fenced. Neither completes the <c>sync</c> job that iterates the documents.
    /// </summary>
    public async Task<EnsureOutcome> EnsureCurrentAsync(SourceContext source, Guid runId, PipelineRun pipeline, DocumentRow document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(document);
        var profile = document.IdentityKind == IdentityKinds.Url
            ? await siteProfiles.AppliedAsync(document.Classification, document.Url, cancellationToken)
            : null;
        if (await IsCurrentAsync(source, pipeline.VersionId, document, profile?.Id, cancellationToken))
        {
            return EnsureOutcome.Current;
        }

        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("A fetched document has an execution id.");
        var contentHash = document.ContentHash ?? throw new InvalidOperationException("A fetched document has content.");
        var key = new ChunkSetKey(document.Id, pipeline.VersionId, profile?.Id, source.EmbeddingProfileId, contentHash);
        if (await index.FindAsync(key, cancellationToken) is { State: ChunkSetStates.Superseded } kept &&
            await chunkSets.GetAsync(kept.Id, cancellationToken) is { } stored)
        {
            var swap = new SwapRequest(source, document, executionId, contentHash, pipeline.VersionId, runId, stored,
                await index.LiveAsync(document.Id, cancellationToken), SwappedBack: true, Job: null);
            switch (await swaps.SwapAsync(swap, cancellationToken))
            {
                case SwapResult.Fenced:
                    await TerminalAsync(source, runId, null, document, document.CanonicalIdentity, Outcome.Superseded, "execution.superseded", pipeline.VersionId,
                        null, cancellationToken);
                    break;
                case SwapResult.Republished:
                    await TerminalAsync(source, runId, null, document, document.CanonicalIdentity, Outcome.Superseded, EmbedJobHandler.Republished,
                        pipeline.VersionId, null, cancellationToken);
                    break;
            }

            return EnsureOutcome.Committed;
        }

        var input = new SegmentInput(document, pipeline, source.CollectionClassification, RawContentKeys.Of(document))
        {
            DeclaredMediaType = document.MediaType,
            SiteProfile = profile,
        };
        var result = await segment.RunAsync(input, JobClasses.Fetch, null, cancellationToken);
        await CommitSegmentAsync(source, runId, pipeline.VersionId, null, document, executionId, result, null, cancellationToken);
        return EnsureOutcome.Committed;
    }

    /// <summary>
    /// The segment commit (engine design §7.4–§7.6), shared by the <c>fetch</c> and <c>parse</c> jobs and a
    /// <c>reprocess</c> run's iteration (<paramref name="job"/> null: nothing completes): fenced on the execution, else a
    /// <c>superseded</c> item (the newer execution records the real outcome).
    /// </summary>
    public async Task<JobResult> CommitSegmentAsync(SourceContext source, Guid runId, Guid pipelineVersionId, LeasedJob? job, DocumentRow document,
        Guid executionId, SegmentResult result, FrontierCompletion? frontier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(result);
        await using (var commit = await EngineCommit.BeginAsync(database, cancellationToken))
        {
            if (await commit.FenceAsync(document.Id, document.Classification, executionId, document.ContentHash!, cancellationToken))
            {
                await commit.AddOutputsAsync(result.Outputs, cancellationToken);
                switch (result)
                {
                    case SegmentResult.Embed embed:
                        await commit.SetFactsAsync(document.Id, embed.Facts, cancellationToken);
                        await commit.EnqueueAsync(queue, [SyncJobs.Embed(source, document, executionId, pipelineVersionId, runId, embed.PersonalDataKey)], cancellationToken);
                        break;
                    case SegmentResult.Parse parse:
                        await commit.SetFactsAsync(document.Id, parse.Facts, cancellationToken);
                        await commit.EnqueueAsync(queue, [SyncJobs.Parse(source, document, executionId, pipelineVersionId, runId, parse.DetectKey)], cancellationToken);
                        break;
                    case SegmentResult.Ended ended:
                        await commit.SetParseOutcomeAsync(document.Id, EngineText.Outcome(ended.Outcome), cancellationToken);
                        await commit.AddRunItemAsync(Item(runId, document, document.CanonicalIdentity, ended.Outcome, ended.Reason, pipelineVersionId), cancellationToken);
                        break;
                }

                if (frontier is not null)
                {
                    await commit.CompleteFrontierAsync(frontier, cancellationToken);
                }

                if (job is null || await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, null, cancellationToken))
                {
                    await commit.CommitAsync(cancellationToken);
                    return new JobResult.Committed();
                }

                return new JobResult.Success();
            }
        }

        LogSuperseded(logger, document.Id, executionId);
        return await TerminalAsync(source, runId, job, document, document.CanonicalIdentity, Outcome.Superseded, "execution.superseded", pipelineVersionId,
            frontier, cancellationToken);
    }

    /// <summary>A terminal outcome without a segment: the run item, the frontier row and the job's completion (when given) in one transaction.</summary>
    public Task<JobResult> TerminalAsync(FetchCase fetch, DocumentRow? document, Outcome outcome, string? reason, FrontierCompletion? frontier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        return TerminalAsync(fetch.Source, fetch.Run.Id, fetch.Job, document, document?.CanonicalIdentity ?? fetch.Identity, outcome, reason,
            fetch.Pipeline.VersionId, frontier, cancellationToken);
    }

    public async Task<JobResult> TerminalAsync(SourceContext source, Guid runId, LeasedJob? job, DocumentRow? document, string identityText, Outcome outcome,
        string? reason, Guid? pipelineVersionId, FrontierCompletion? frontier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        await commit.AddRunItemAsync(new RunItemRow(runId, source.Classification, document?.Id, identityText, outcome)
        {
            Reason = reason,
            Strategy = document?.ParseStrategy,
            PipelineVersionId = pipelineVersionId,
            ExecutionId = document?.CurrentExecutionId,
        }, cancellationToken);
        if (frontier is not null)
        {
            await commit.CompleteFrontierAsync(frontier, cancellationToken);
        }

        if (job is not null && !await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, null, cancellationToken))
        {
            // The lease is gone: nothing is written; the dispatcher records the loss.
            return new JobResult.Success();
        }

        await commit.CommitAsync(cancellationToken);
        return new JobResult.Committed();
    }

    private async Task<bool> IsCurrentAsync(SourceContext source, Guid pipelineVersionId, DocumentRow document, Guid? siteProfileVersionId,
        CancellationToken cancellationToken)
    {
        var live = await index.LiveAsync(document.Id, cancellationToken);
        return live is { State: ChunkSetStates.Live or ChunkSetStates.Held } &&
               live.Key.PipelineVersionId == pipelineVersionId &&
               live.Key.SiteProfileVersionId == siteProfileVersionId &&
               live.Key.EmbeddingProfileId == source.EmbeddingProfileId &&
               document.ContentHash is { } hash && live.Key.ContentHash.AsSpan().SequenceEqual(hash);
    }

    private async Task FailedAsync(DocumentRow? known, CancellationToken cancellationToken)
    {
        if (known is not null)
        {
            await documents.FailedAsync(known.Id, cancellationToken);
        }
    }

    private static RunItemRow Item(Guid runId, DocumentRow document, string identityText, Outcome outcome, string? reason, Guid pipelineVersionId) =>
        new(runId, document.Classification, document.Id, identityText, outcome)
        {
            Reason = reason,
            Strategy = document.ParseStrategy,
            PipelineVersionId = pipelineVersionId,
            ExecutionId = document.CurrentExecutionId,
        };

    private FrontierCompletion? Frontier(FetchCase fetch, short? status, Validators? validators, byte[]? contentHash) =>
        fetch.Frontier ?? (fetch.FrontierUrlHash is { } urlHash
            ? new FrontierCompletion(fetch.Source.SourceId, fetch.Source.Classification, urlHash, fetch.Run.Id, time.GetUtcNow(), status,
                validators?.ETag, validators?.LastModified, contentHash)
            : null);

    private static short? StatusOf(string reason) =>
        reason.StartsWith("http.", StringComparison.Ordinal) &&
        short.TryParse(reason.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
            ? code
            : null;

    [LoggerMessage(LogLevel.Information, "Document {DocumentId}: execution {ExecutionId} was superseded before its commit")]
    private static partial void LogSuperseded(ILogger logger, Guid documentId, Guid executionId);
}
