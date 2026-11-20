using System.Security.Cryptography;
using Npgsql;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Infrastructure.Security;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Jobs;
using Outcome = Kalla.Pipelines.Jobs.RunItemOutcome;

namespace Kalla.Worker.Engine;

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

    /// <summary>The crawl frontier row's sitemap <c>lastmod</c>, stored on the document with a 200.</summary>
    public DateTimeOffset? SitemapLastMod { get; init; }

    /// <summary>
    /// The bytes the fetch is for when they are known before it (an upload slot's SHA-256): its terminal items carry them, not
    /// the known document's earlier content (<see cref="RunItemRow.ContentHash"/>).
    /// </summary>
    public byte[]? ContentHash { get; init; }
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
/// <c>EnsureCurrent</c> (<see cref="Reconciler"/>), which answers <c>unchanged</c> when the live set already has the wanted
/// key and otherwise re-runs the segment from the caches (a kept set with the wanted key is swapped back by the
/// <c>embed</c> continuation, which completes with the fetch's frontier row). The segment's outputs, the document facts, the continuation (<c>embed</c>, or
/// <c>parse</c> for parser-bound documents), the frontier row and the job's completion commit in one fenced transaction;
/// every other outcome is a run item committed with the job. Run items are written only at a terminal outcome.
/// </summary>
public sealed partial class DocumentIngest(
    JobDatabase database,
    IJobQueue queue,
    DocumentStore documents,
    ChunkSetSwap swaps,
    Reconciler reconciler,
    ProcessingSegment segment,
    IQuarantineStore quarantine,
    ScanWait scanWait,
    WorkerIdentity identity,
    ILoggerFactory loggers,
    TimeProvider time,
    ILogger<DocumentIngest> logger)
{
    private const string ForceExecutionSql = """
        UPDATE content."Documents" SET "CurrentExecutionId" = $2 WHERE "Id" = $1 AND "ContentHash" = $3 AND "DeletedAt" IS NULL
        """;

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
                    fetch.DisplayName, content.Facts.CanonicalHint)
                {
                    HttpLastModified = content.Validators.LastModifiedDate,
                    SitemapLastMod = fetch.SitemapLastMod,
                    FetchMode = content.Facts.FetchMode,
                };
                var upsert = await documents.UpsertFetchedAsync(fetched, fetch.Run.Id, now, cancellationToken);
                var frontier = Frontier(fetch, 200, content.Validators, fetched.ContentHash);
                return await ProcessAsync(fetch, upsert.Document, content.Raw.Key, content.Raw.MediaType, upsert.ContentChanged, frontier, cancellationToken);

            case FetchOutcome.NotModified notModified when known is { DeletedAt: null, ContentHash: not null }:
                await documents.SeenAsync(known.Id, fetch.Run.Id, now, notModified.FinalUrl?.AbsoluteUri, cancellationToken);
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

            case FetchOutcome.Transient transient when (transient.CountsAttempt && fetch.Job.Attempts >= fetch.Job.MaxAttempts) ||
                                                       scanWait.Expired(transient.Reason, fetch.Job):
                // The last attempt, or 24 h without a ClamAV verdict: the document fails for the run instead of dying
                // without a run item or waiting forever.
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
        var reconciliation = await reconciler.ReconcileAsync(fetch.Source, document, fetch.Pipeline.VersionId, cancellationToken);
        if (!changed && reconciliation.Action == ReconcileAction.Current)
        {
            return await TerminalAsync(fetch, document, Outcome.Unchanged, fetch.UnchangedReason, frontier, cancellationToken);
        }

        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("A fetched document has an execution id.");
        var input = new SegmentInput(document, fetch.Pipeline, fetch.Source.CollectionClassification, rawKey)
        {
            DeclaredMediaType = declaredMediaType,
            SiteProfile = reconciliation.SiteProfile,
        };
        var result = await segment.RunAsync(input, JobClasses.Fetch, null, cancellationToken);
        return await CommitSegmentAsync(fetch.Source, fetch.Run.Id, fetch.Pipeline.VersionId, fetch.Job, document, executionId, result, frontier, cancellationToken);
    }

    /// <summary>
    /// <c>EnsureCurrent</c> of a <c>reprocess</c> run (engine design §2.3, §9.2, §12.2, §12.3): one live document against
    /// <paramref name="pipeline"/> and its host's current site-profile version, without a fetch, decided by
    /// <see cref="Reconciler"/>. A current live set is left alone; a <c>superseded</c> set with the wanted key (kept 14 days:
    /// a rollback of the pipeline or of the host's site profile) is swapped back here (<see cref="SwapBackAsync"/>), with no
    /// <c>parse</c> or <c>embed</c> job; otherwise the segment runs from the
    /// first stage without cached output and its continuation (<c>parse</c> or <c>embed</c>, where a set differing only by
    /// label is re-keyed) commits fenced. Neither completes the <c>sync</c> job that iterates the documents.
    /// </summary>
    public async Task<EnsureOutcome> EnsureCurrentAsync(SourceContext source, Guid runId, PipelineRun pipeline, DocumentRow document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(document);
        var reconciliation = await reconciler.ReconcileAsync(source, document, pipeline.VersionId, cancellationToken);
        switch (reconciliation.Action)
        {
            case ReconcileAction.Current:
                return EnsureOutcome.Current;
            case ReconcileAction.SwapBack:
                await SwapBackAsync(source, runId, pipeline.VersionId, document, reconciliation, cancellationToken);
                return EnsureOutcome.Committed;
        }

        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("A fetched document has an execution id.");
        var input = new SegmentInput(document, pipeline, source.CollectionClassification, RawContentKeys.Of(document))
        {
            DeclaredMediaType = document.MediaType,
            SiteProfile = reconciliation.SiteProfile,
        };
        var result = await segment.RunAsync(input, JobClasses.Fetch, null, cancellationToken);
        await CommitSegmentAsync(source, runId, pipeline.VersionId, null, document, executionId, result, null, cancellationToken);
        return EnsureOutcome.Committed;
    }

    /// <summary>
    /// A forced <c>reprocess</c> of one live document (engine design §7.5, §8): a new execution id supersedes any work in
    /// flight for it, and the segment runs with the output cache bypassed from <paramref name="bypassFrom"/> on
    /// (<see cref="StageBypass"/>), the stages before it served from the cache, and commits fenced with its continuation
    /// (<c>parse</c> carrying the bypass, or <c>embed</c>). The wanted set is not consulted: a current document is
    /// re-processed too. The execution id is derived from the run and the document (<see cref="ForcedExecutionId"/>), so a
    /// retried iteration keeps it and finds what it already stored. A document whose content changed or that was deleted
    /// since it was listed is left to its own execution. Never completes the <c>sync</c> job.
    /// </summary>
    public async Task ForceAsync(SourceContext source, Guid runId, PipelineRun pipeline, DocumentRow document, string bypassFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(document);
        var contentHash = document.ContentHash ?? throw new InvalidOperationException("The document has no content yet.");
        var executionId = ForcedExecutionId(runId, document.Id);
        if (document.CurrentExecutionId != executionId)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(ForceExecutionSql, connection)
            {
                Parameters = { Db.P(document.Id), Db.P(executionId), Db.P(contentHash) },
            };
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return;
            }
        }

        var forced = document with { CurrentExecutionId = executionId };
        var reconciliation = await reconciler.ReconcileAsync(source, forced, pipeline.VersionId, cancellationToken);
        var input = new SegmentInput(forced, pipeline, source.CollectionClassification, RawContentKeys.Of(forced))
        {
            DeclaredMediaType = forced.MediaType,
            SiteProfile = reconciliation.SiteProfile,
            BypassFrom = bypassFrom,
        };
        var result = await segment.RunAsync(input, JobClasses.Fetch, null, cancellationToken);
        await CommitSegmentAsync(source, runId, pipeline.VersionId, null, forced, executionId, result, null, cancellationToken);
    }

    /// <summary>The execution id of a forced reprocess of <paramref name="documentId"/> in run <paramref name="runId"/>: a version-8 UUID from SHA-256 of both ids.</summary>
    public static Guid ForcedExecutionId(Guid runId, Guid documentId)
    {
        Span<byte> ids = stackalloc byte[32];
        runId.TryWriteBytes(ids, bigEndian: true, out _);
        documentId.TryWriteBytes(ids[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(ids, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    /// <summary>
    /// Swaps <paramref name="reconciliation"/>'s kept set back (engine design §2.3, §12.3, §14 rule 1) in run
    /// <paramref name="runId"/>, with no <c>parse</c> or <c>embed</c> job and no job to complete: an <c>updated</c> item
    /// (<see cref="ChunkSetSwap.SwappedBackReason"/>) on success, a <c>superseded</c> one when the execution or the published
    /// version moved on meanwhile.
    /// </summary>
    public async Task<SwapResult> SwapBackAsync(SourceContext source, Guid runId, Guid pipelineVersionId, DocumentRow document, Reconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reconciliation);
        var kept = reconciliation.Kept ?? throw new InvalidOperationException("Only a kept set is swapped back.");
        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("A fetched document has an execution id.");
        var swap = new SwapRequest(source, document, executionId, reconciliation.Wanted.ContentHash, pipelineVersionId, runId, kept, reconciliation.Live,
            SwappedBack: true, Job: null);
        var result = await swaps.SwapAsync(swap, cancellationToken);
        var reason = result switch
        {
            SwapResult.Fenced => "execution.superseded",
            SwapResult.Republished => EmbedJobHandler.Republished,
            _ => null,
        };
        if (reason is not null)
        {
            await TerminalAsync(source, runId, null, document, document.CanonicalIdentity, Outcome.Superseded, reason, pipelineVersionId, null, null, cancellationToken);
        }

        return result;
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
                        await commit.EnqueueAsync(queue, [SyncJobs.Embed(source, document, executionId, pipelineVersionId, runId, embed.PersonalDataKey,
                            embed.ParseRuleKey)], cancellationToken);
                        break;
                    case SegmentResult.Parse parse:
                        await commit.SetFactsAsync(document.Id, parse.Facts, cancellationToken);
                        await commit.EnqueueAsync(queue, [SyncJobs.Parse(source, document, executionId, pipelineVersionId, runId, parse.DetectKey,
                            bypassFrom: parse.BypassFrom)], cancellationToken);
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
            frontier, null, cancellationToken);
    }

    /// <summary>A terminal outcome without a segment: the run item, the frontier row and the job's completion (when given) in one transaction.</summary>
    public Task<JobResult> TerminalAsync(FetchCase fetch, DocumentRow? document, Outcome outcome, string? reason, FrontierCompletion? frontier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var item = TerminalItem(fetch.Source, fetch.Run.Id, document, document?.CanonicalIdentity ?? fetch.Identity, outcome, reason, fetch.Pipeline.VersionId);
        return CommitTerminalAsync(fetch.Job, fetch.ContentHash is null ? item : item with { ContentHash = fetch.ContentHash }, frontier, null, cancellationToken);
    }

    /// <summary>
    /// A terminal outcome of a document outside a segment; with <paramref name="usage"/> (an <c>embed</c> job's calls) the item's
    /// <c>CostUsd</c> is their cost and they are added to <c>CollectionDailyUsage</c> in the same transaction (engine design §10.6).
    /// </summary>
    public Task<JobResult> TerminalAsync(SourceContext source, Guid runId, LeasedJob? job, DocumentRow? document, string identityText, Outcome outcome,
        string? reason, Guid? pipelineVersionId, FrontierCompletion? frontier, JobUsage? usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CommitTerminalAsync(job, TerminalItem(source, runId, document, identityText, outcome, reason, pipelineVersionId), frontier, usage, cancellationToken);
    }

    private static RunItemRow TerminalItem(SourceContext source, Guid runId, DocumentRow? document, string identityText, Outcome outcome, string? reason,
        Guid? pipelineVersionId) =>
        new(runId, source.Classification, document?.Id, identityText, outcome)
        {
            Reason = reason,
            Strategy = document?.ParseStrategy,
            PipelineVersionId = pipelineVersionId,
            ExecutionId = document?.CurrentExecutionId,
            ContentHash = document?.ContentHash,
        };

    private async Task<JobResult> CommitTerminalAsync(LeasedJob? job, RunItemRow item, FrontierCompletion? frontier, JobUsage? usage,
        CancellationToken cancellationToken)
    {
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        await commit.AddRunItemAsync(item with { CostUsd = usage?.PendingCostUsd }, cancellationToken);
        if (frontier is not null)
        {
            await commit.CompleteFrontierAsync(frontier, cancellationToken);
        }

        if (job is not null && !await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, null, cancellationToken))
        {
            // The lease is gone: nothing is written; the dispatcher records the loss.
            return new JobResult.Success();
        }

        if (usage is not null)
        {
            await commit.AddUsageAsync(usage, cancellationToken);
        }

        await commit.CommitAsync(cancellationToken);
        return new JobResult.Committed();
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
            ContentHash = document.ContentHash,
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
