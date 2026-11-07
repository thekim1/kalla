using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Jobs;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>What a crawl run's reconcile phase decides on (connectors design §5.4, §5.5, §13).</summary>
/// <param name="Verdict">The traversal's completeness.</param>
/// <param name="Candidates">The deletion candidates.</param>
/// <param name="EmptyListing">The traversal was complete and reached no document.</param>
/// <param name="Report">The crawl report's sections, a JSON object merged into <c>Runs.Report</c> in the reconcile commit.</param>
public sealed record CrawlReconcile(TraversalVerdict Verdict, IReadOnlyList<DeletionCandidate> Candidates, bool EmptyListing, string Report);

/// <summary>The crawl report's renderer counts (connectors design §8.6): pages rendered, render failures, and of those the pages kept from their last good content.</summary>
public sealed record RenderSection(int Rendered, int Failures, int KeptFromLastGood);

/// <summary>
/// The engine's part of a <c>crawl</c> source (connectors design §5, §6.0; engine design §9.2). Listing: the run's mode
/// (incremental only on hosts whose profile marks <c>lastmod</c> reliable, <c>revalidateEvery</c>) is fixed in
/// <c>Runs.Report</c>; seeds from start URLs and sitemaps are admitted per checkpoint and the listing yields after 1,000
/// items or 2 minutes; at its end the removal checks are admitted and every host topped up. Fetch: the traversal fetches
/// one released row and admits its links; the document, its run item and the row's completion commit together
/// (<see cref="DocumentIngest"/>), then the hosts are topped up, so a worker that dies between the fetch and the commit
/// leaves the row released for the reclaimed job. A <c>fetch</c>-phase continuation (the finalizer's, or the night
/// window's) releases again what lost its job; when nothing can move any more (the rows of hosts the run ended, §6.4) the
/// run goes on to its reconcile. Reconcile: completeness, deletion candidates and the crawl report's sections.
/// </summary>
public sealed class CrawlSourceRunner(
    CrawlConnector connector,
    CrawlTraversal traversal,
    FetchTopUp topUp,
    ICrawlFrontierStore frontier,
    ICrawlRunStore crawlRuns,
    IConnectorCheckpointStore checkpoints,
    ICrawlPolicySource policy,
    RunStore runs,
    DocumentStore documents,
    DocumentIngest ingest,
    ScanWait scanWait,
    ConnectorMetrics metrics,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    IOptions<CrawlOptions> options,
    TimeProvider time)
{
    /// <summary>A listing yields at the next checkpoint after this many items (engine design §9.2).</summary>
    public const int ListingItems = 1000;

    private static readonly HashSet<string> GoneReasons = new(StringComparer.Ordinal)
    {
        DeletionReasons.Http404, DeletionReasons.Http410, DeletionReasons.MovedOutOfScope,
    };

    private const string ReleasedSql = """
        SELECT "UrlHash" FROM content."CrawlFrontier"
        WHERE "Classification" = $1 AND "SourceId" = $2 AND "RunId" = $3 AND "State" = 'released'
        """;

    // URLs of the run whose document the run did not see because they failed: a seed (whatever its outcome), or a URL at
    // any depth whose latest item of the run is failed (401/403, TLS, 5xx after retries, http.soft_404). They are never
    // not-seen candidates; their failures count toward persistent-failure (§5.4, §13.1). A URL skipped by a trap or the
    // page limit has no failed item and stays a candidate.
    private const string FailedInRunSql = """
        SELECT d."Id" FROM content."CrawlFrontier" f
        JOIN content."Documents" d ON d."SourceId" = f."SourceId" AND d."IdentityKind" = 'url' AND d."IdentityHash" = f."UrlHash"
        WHERE f."Classification" = $1 AND f."SourceId" = $2 AND f."RunId" = $3 AND NOT f."RemovalCheck"
          AND d."DeletedAt" IS NULL AND d."LastRunId" IS DISTINCT FROM $3
          AND (f."Depth" = 0 OR (
              SELECT i."Outcome" FROM content."RunItems" i
              WHERE i."RunId" = $3 AND i."Identity" = f."Url" AND i."Outcome" <> 'superseded'
              ORDER BY i."CreatedAt" DESC, i."Id" DESC LIMIT 1) = 'failed')
        """;

    private const string MediaTypesSql = """
        SELECT "Id", "MediaType" FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL AND "MediaType" IS NOT NULL
        """;

    // Live documents last reached deeper than the current maxDepth (a lowered limit, connectors design §12.3).
    private const string BeyondDepthSql = """
        SELECT d."Id" FROM content."CrawlFrontier" f
        JOIN content."Documents" d ON d."SourceId" = f."SourceId" AND d."IdentityKind" = 'url' AND d."IdentityHash" = f."UrlHash"
        WHERE f."Classification" = $1 AND f."SourceId" = $2 AND f."Depth" > $3 AND d."DeletedAt" IS NULL
        """;

    // The run's removal checks and the latest item of each.
    private const string RemovalSql = """
        SELECT DISTINCT ON (i."Identity") i."Outcome", i."Reason"
        FROM content."CrawlFrontier" f JOIN content."RunItems" i ON i."RunId" = f."RunId" AND i."Identity" = f."Url"
        WHERE f."Classification" = $1 AND f."SourceId" = $2 AND f."RunId" = $3 AND f."RemovalCheck" AND i."Outcome" <> 'superseded'
        ORDER BY i."Identity", i."CreatedAt" DESC, i."Id" DESC
        """;

    // The run's rendered pages (a fetched item whose document's content is the rendered DOM; a lastmod skip renders nothing),
    // its render failures (attempts used up), and those of them whose document kept its last good content.
    private const string RenderSql = """
        SELECT count(*) FILTER (WHERE i."Outcome" IN ('added', 'updated', 'unchanged', 'degraded') AND d."FetchMode" = 'browser'
                                  AND i."Reason" IS DISTINCT FROM 'crawl.lastmod_skip')::int,
               count(*) FILTER (WHERE i."Outcome" = 'failed' AND i."Reason" LIKE 'render.%')::int,
               count(*) FILTER (WHERE i."Outcome" = 'failed' AND i."Reason" LIKE 'render.%' AND d."Id" IS NOT NULL AND d."ContentHash" IS NOT NULL)::int
        FROM content."RunItems" i LEFT JOIN content."Documents" d ON d."Id" = i."DocumentId"
        WHERE i."RunId" = $1
        """;

    // rag.crawl.incomplete_runs: the most consecutive incomplete traversals of any active crawl source (its latest report).
    private const string IncompleteRunsSql = """
        SELECT coalesce(max(c), 0)::int FROM (
            SELECT DISTINCT ON (r."SourceId") (r."Report" -> 'completeness' ->> 'consecutiveIncomplete')::int AS c
            FROM content."Runs" r JOIN config."Sources" s ON s."Id" = r."SourceId"
            WHERE s."Type" = 'crawl' AND s."State" = 'active' AND s."DeletedAt" IS NULL AND (r."Report" -> 'completeness') IS NOT NULL
            ORDER BY r."SourceId", r."StartedAt" DESC) latest
        """;

    /// <summary>The context the run's jobs share, from the mode its listing fixed.</summary>
    public async Task<CrawlRunContext> ContextAsync(SourceContext source, RunRow run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        return Context(source, run, await runs.CrawlModeAsync(run.Id, cancellationToken) ?? new CrawlRunMode(false, false));
    }

    /// <summary>
    /// Opens the run's <c>CrawlRuns</c> row and fixes its mode once: an incremental run stays incremental only when every
    /// scope host's profile marks <c>lastmod</c> reliable; every <c>revalidateEvery</c>-th full traversal and a forced run
    /// revalidate. A <c>retry-failed</c> run is a full, non-revalidating fetch of its rows. The fixed mode's incremental flag
    /// is written to <c>CrawlRuns.Incremental</c> on every start, so a listing resumed after a crash between the two writes
    /// still records it.
    /// </summary>
    public async Task<CrawlRunContext> StartAsync(SourceContext source, RunRow run, bool incremental, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        var mode = await runs.CrawlModeAsync(run.Id, cancellationToken) ?? await FixModeAsync(source, run, incremental, force, cancellationToken);
        await crawlRuns.SetIncrementalAsync(run.Id, mode.Incremental, cancellationToken);
        return Context(source, run, mode);
    }

    private async Task<CrawlRunMode> FixModeAsync(SourceContext source, RunRow run, bool incremental, bool force, CancellationToken cancellationToken)
    {
        // A site profile applied just before this run (a site-profile follow-up) is read now, not from this instance's cache.
        await connector.SourceAsync(source.SourceId, source.Classification, source.Settings, fresh: true, cancellationToken);
        var started = await traversal.StartAsync(Context(source, run, new CrawlRunMode(incremental && run.Kind == RunKinds.Sync, false)), cancellationToken);
        var revalidate = run.Kind == RunKinds.Sync && CrawlFreshness.Revalidates(await runs.TraversalsBeforeAsync(source.SourceId, run.Id, cancellationToken),
            ContractJson.Deserialize<CrawlSettings>(source.Settings).RevalidateEvery, force, started.Incremental);
        return await runs.SetCrawlModeAsync(run.Id, new CrawlRunMode(started.Incremental, revalidate), cancellationToken);
    }

    /// <summary>
    /// Phase <c>list</c> of a <c>sync</c> run. When the run's identity settings differ from the last applied ones
    /// (<see cref="SettingsEffects"/>), it is a full revalidating traversal: every URL re-canonicalised and fetched; so it is
    /// when <c>fetchMode</c>, <c>readySelector</c> or <c>scrollToLoad</c> changed (every page fetched again in the new mode,
    /// no validators, no <c>lastmod</c> skip; §12.3).
    /// </summary>
    public async Task<JobResult> ListAsync(SourceContext source, RunRow run, SyncPayload payload, LeasedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(payload);
        var change = await SettingsEffects.SinceAppliedAsync(database, source.SourceId, run.Id, cancellationToken);
        var full = change.Identity || change.Fetch;
        var context = await StartAsync(source, run, payload.Incremental && !full, payload.Force || full, cancellationToken);
        var from = payload.ResumeFromCheckpoint
            ? await checkpoints.GetAsync<CrawlCheckpoint>(source.SourceId, CheckpointKind.Run, run.Id, cancellationToken) ?? new CrawlCheckpoint()
            : new CrawlCheckpoint();
        var started = time.GetTimestamp();
        var items = 0;
        var batch = new List<ListingEvent<CrawlCheckpoint>>();
        var refused = new List<RunItemRow>();
        var listing = new ListingContext(source.SourceId, source.Classification, run.Id, source.Settings);
        await foreach (var listed in connector.ListAsync(listing, from, cancellationToken))
        {
            switch (listed)
            {
                case ListingEvent<CrawlCheckpoint>.Checkpoint checkpoint:
                    await traversal.AdmitSeedsAsync(context, batch, cancellationToken);
                    await AddItemsAsync(refused, cancellationToken);
                    await checkpoints.SaveAsync(source.SourceId, source.Classification, CheckpointKind.Run, run.Id, checkpoint.State, cancellationToken);
                    batch.Clear();
                    refused.Clear();
                    if (items >= ListingItems || time.GetElapsedTime(started) > UrlListSource.ListingSlice)
                    {
                        // Yield: the continuation resumes from the checkpoint just saved (no attempt, engine design §9.2).
                        return await runs.ContinueAsync(queue, job, identity.InstanceId, SyncJobs.Continuation(source.SourceId, run.Id, source.DepartmentId,
                            source.Classification, run.Kind, run.Trigger, SyncPhase.List, resumeFromCheckpoint: true), run.Id, null, cancellationToken);
                    }

                    break;
                case ListingEvent<CrawlCheckpoint>.Done done:
                    await traversal.AdmitSeedsAsync(context, batch, cancellationToken);
                    await traversal.ListingDoneAsync(context, done.Completeness, cancellationToken);
                    await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, null, cancellationToken);
                    await TopUpAsync(context, cancellationToken);
                    return new JobResult.Success();
                case ListingEvent<CrawlCheckpoint>.Item:
                    items++;
                    batch.Add(listed);
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure failure:
                    batch.Add(listed);
                    if (failure.Reason != CrawlConnector.RobotsUnreachable && !failure.Reason.StartsWith(CrawlConnector.SitemapReasonPrefix, StringComparison.Ordinal))
                    {
                        // An invalid or out-of-scope start URL: a failed seed with its item (sitemap and robots failures pause their host).
                        refused.Add(new RunItemRow(run.Id, source.Classification, null, failure.Identity, Outcome.Failed) { Reason = failure.Reason });
                    }

                    break;
            }
        }

        return new JobResult.Success();
    }

    /// <summary>
    /// Phase <c>fetch</c> (the night window opened, or the finalizer found open rows with no job): releases and enqueues
    /// again; when nothing can move, a <c>sync</c> run goes on to its reconcile and a <c>retry-failed</c> run ends. A
    /// released row is still moving even when its enqueue was dropped: its dedup key is held by a fetch already queued or
    /// running (the top-up that released it enqueued it after the finalizer looked).
    /// </summary>
    public async Task<JobResult> FetchPhaseAsync(SourceContext source, RunRow run, LeasedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        var context = await ContextAsync(source, run, cancellationToken);
        var result = await topUp.TopUpAsync(context, null, null, cancellationToken);
        if (result.WindowOpensAt is { } opens)
        {
            return await runs.ContinueAsync(queue, job, identity.InstanceId, topUp.Continuation(context, opens), run.Id, null, cancellationToken);
        }

        if (result.Enqueued + await RequeueReleasedAsync(source, run.Id, cancellationToken) > 0)
        {
            return new JobResult.Success();
        }

        if (run.Kind != RunKinds.Sync)
        {
            await runs.SetPhaseAsync(run.Id, RunPhases.Done, null, cancellationToken);
            return new JobResult.Success();
        }

        return await runs.ContinueAsync(queue, job, identity.InstanceId, SyncJobs.Continuation(source.SourceId, run.Id, source.DepartmentId,
            source.Classification, run.Kind, run.Trigger, SyncPhase.Reconcile), run.Id, RunPhases.Reconcile, cancellationToken);
    }

    /// <summary>
    /// A <c>fetch</c> job of a crawl row. Transient outcomes requeue (the last attempt, or 24 h without a ClamAV verdict
    /// (<see cref="ScanWait"/>), abandons the row with a <c>failed</c> item); a host that cannot be reached for the run
    /// leaves a <c>skipped</c> item; a fetched URL goes through <see cref="DocumentIngest"/> with the row's completion in the
    /// document's commit (a verified alias as <c>skipped</c> <c>identity.folded</c>), and the hosts are topped up after it.
    /// </summary>
    public async Task<JobResult> FetchAsync(SourceContext source, RunRow run, PipelineRun pipeline, FetchPayload payload, LeasedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(job);
        var urlHash = payload.UrlHash ?? throw new ArgumentException("A crawl fetch has a frontier row.", nameof(payload));
        var context = await ContextAsync(source, run, cancellationToken);
        var row = await frontier.GetAsync(source.SourceId, source.Classification, urlHash, cancellationToken);
        switch (await traversal.ProcessAsync(context, urlHash, cancellationToken))
        {
            case CrawlFetchResult.Requeue requeue when (requeue.CountsAttempt && job.Attempts >= job.MaxAttempts) || scanWait.Expired(requeue.Reason, job):
                await traversal.AbandonAsync(context, urlHash, cancellationToken);
                return await ingest.TerminalAsync(source, run.Id, job, await KnownAsync(source, row!.Url, cancellationToken), row.Url, Outcome.Failed,
                    requeue.Reason, run.PipelineVersionId, null, cancellationToken);
            case CrawlFetchResult.Requeue requeue:
                return new JobResult.TransientError(requeue.Reason, requeue.Delay, requeue.CountsAttempt);
            case CrawlFetchResult.NotReached notReached:
                return await ingest.TerminalAsync(source, run.Id, job, await KnownAsync(source, row!.Url, cancellationToken), row.Url, Outcome.Skipped,
                    notReached.Reason, run.PipelineVersionId, null, cancellationToken);
            case CrawlFetchResult.Done done:
                var outcome = done.Fold.Kind == CanonicalFoldKind.Folded ? new FetchOutcome.Skipped(CrawlTraversal.Folded) : done.Outcome;
                var fetched = done.Frontier.Row;
                var fetch = new FetchCase(source, run, pipeline, job, IdentityKinds.Url,
                    outcome is FetchOutcome.NotModified ? fetched.RedirectTo ?? fetched.Url : fetched.Url, Db.Bytes(fetched.UrlHash))
                {
                    Frontier = Completion(source, run, done.Frontier),
                    UnchangedReason = done.Facts.Contains(CrawlTraversal.LastmodSkip) ? CrawlTraversal.LastmodSkip : null,
                    SitemapLastMod = fetched.SitemapLastMod,
                };
                var result = await ingest.HandleAsync(fetch, outcome, cancellationToken);
                if (result is JobResult.Committed)
                {
                    await traversal.CommittedAsync(context, done, cancellationToken);
                }

                return result;
            default:
                return new JobResult.Success();
        }
    }

    /// <summary>
    /// Before the reconcile: the traversal's completeness (stored on <c>CrawlRuns</c>), the candidates (explicit answers
    /// in any run, <c>persistent-failure</c>, <c>not-seen</c> only after a complete traversal and never for a URL that
    /// failed in the run, and the settings-change effects of <see cref="SettingsCandidates"/>: <c>scope-narrowed</c>, <c>identity-changed</c>,
    /// nothing beyond a lowered <c>maxDepth</c>), and the report's sections: completeness with the incomplete-traversal
    /// count and alert, the run's verdict, the removal checks, the <c>demote</c> patterns with the live documents they match,
    /// the scope hosts crawled with a non-default politeness profile (connectors design §6.6) and, for a <c>browser</c>
    /// source, the renderer counts (§8.6).
    /// </summary>
    public async Task<CrawlReconcile> PrepareReconcileAsync(SourceContext source, RunRow run, IReadOnlyList<SourceDocument> all,
        IReadOnlyDictionary<Guid, string> explicitGone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(all);
        var context = await ContextAsync(source, run, cancellationToken);
        var verdict = await traversal.EvaluateAsync(context, cancelled: false, cancellationToken);
        var failedInRun = await FailedInRunAsync(source, run.Id, cancellationToken);
        var crawl = await connector.SourceAsync(source.SourceId, source.Classification, source.Settings, fresh: true, cancellationToken);
        var change = await SettingsEffects.SinceAppliedAsync(database, source.SourceId, run.Id, cancellationToken);
        var candidates = SettingsCandidates.Crawl(run.Id, all, verdict, explicitGone, failedInRun, time.GetUtcNow(), crawl,
            await MediaTypesAsync(source.SourceId, cancellationToken), await BeyondDepthAsync(source, crawl.Settings.MaxDepth, cancellationToken), change.Identity);
        var record = await crawlRuns.GetAsync(run.Id, cancellationToken) ?? throw new InvalidOperationException("The crawl run has no CrawlRuns row.");
        var history = (await runs.CrawlHistoryAsync(source.SourceId, run.Id, cancellationToken)).Select(h => new TraversalHistoryEntry(h.Complete, h.Incremental));
        var completeness = CrawlReportSections.Completeness(record, verdict, options.Value.CompletenessRule, context.Incremental, failedInRun.Count, history,
            policy.Current.IncompleteRunsAlert);
        var settings = crawl.Settings;
        // Demote counts over Documents.Url, the URL retrieval matches demote on (the path form on an identity-rule host, #228).
        var live = all.Where(d => d.DeletedAt is null && d.Url is not null).Select(d => d.Url!).ToList();
        var report = new Dictionary<string, object>
        {
            ["completeness"] = completeness,
            ["verdict"] = new { verdict.RunFailure, verdict.RunDegradation },
            ["removalChecks"] = RemovalSection.From(await RemovalOutcomesAsync(source, run.Id, cancellationToken)),
            ["demote"] = settings.Demote.Select(d => new { d.Pattern, d.Factor, Documents = live.Count(UrlPattern.Parse(d.Pattern).Matches) }).ToList(),
            ["politeness"] = PolitenessReportEntry.For(crawl.Scope.Hosts, policy.Current),
        };
        if (settings.FetchMode == FetchMode.Browser)
        {
            report["render"] = await RenderSectionAsync(run.Id, cancellationToken);
        }

        var empty = verdict.Complete && !all.Any(d => d.DeletedAt is null && d.LastRunId == run.Id);
        return new CrawlReconcile(verdict, candidates, empty, JsonSerializer.Serialize(report, JsonSerializerOptions.Web));
    }

    /// <summary>Records <c>rag.crawl.incomplete_runs</c> after a crawl report was written.</summary>
    public async Task RecordIncompleteRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(IncompleteRunsSql, connection);
        metrics.IncompleteRuns(await command.ExecuteScalarAsync(cancellationToken) is int count ? count : 0);
    }

    /// <summary>Tops every host of the run up; a closed night window enqueues the run's continuation for its opening.</summary>
    public async Task TopUpAsync(CrawlRunContext context, CancellationToken cancellationToken)
    {
        var result = await topUp.TopUpAsync(context, null, null, cancellationToken);
        if (result.WindowOpensAt is { } opens)
        {
            await queue.EnqueueAsync([topUp.Continuation(context, opens)], null, cancellationToken);
        }
    }

    private static CrawlRunContext Context(SourceContext source, RunRow run, CrawlRunMode mode) =>
        new(new CrawlRunKey(source.SourceId, source.Classification, run.Id), source.DepartmentId, SyncJobs.Parse<SyncKind>(run.Kind),
            SyncJobs.Parse<RunTrigger>(run.Trigger), source.Settings, mode.Revalidate, mode.Incremental);

    private FrontierCompletion Completion(SourceContext source, RunRow run, FrontierDone done) =>
        new(source.SourceId, source.Classification, Db.Bytes(done.Row.UrlHash), run.Id, done.Facts?.FetchedAt ?? time.GetUtcNow(), done.Facts?.LastStatus,
            done.Facts?.ETag, done.Facts?.LastModified, done.Facts?.ContentHash is { } hash ? Db.Bytes(hash) : null)
        {
            RedirectTo = done.Facts?.RedirectTo,
            KeepFacts = done.Facts is null,
            SeedFailed = done.SeedFailed,
        };

    private Task<DocumentRow?> KnownAsync(SourceContext source, string url, CancellationToken cancellationToken) =>
        documents.FindAsync(source.SourceId, IdentityKinds.Url, url, cancellationToken);

    /// <summary>
    /// Released rows of the run enqueued again; a row whose job is still open is dropped by its dedup key. Returns the
    /// released rows: each has a queued or running fetch now.
    /// </summary>
    private async Task<int> RequeueReleasedAsync(SourceContext source, Guid runId, CancellationToken cancellationToken)
    {
        var released = new List<string>();
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(ReleasedSql, connection) { Parameters = { Db.P(source.Classification), Db.P(source.SourceId), Db.P(runId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                released.Add(Db.Hex(reader.GetFieldValue<byte[]>(0)));
            }
        }

        if (released.Count > 0)
        {
            await queue.EnqueueAsync([.. released.Select(h => SyncJobs.FetchUrl(source, runId, h))], null, cancellationToken);
        }

        return released.Count;
    }

    private async Task<RenderSection> RenderSectionAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RenderSql, connection) { Parameters = { Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RenderSection(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private async Task<IReadOnlySet<Guid>> FailedInRunAsync(SourceContext source, Guid runId, CancellationToken cancellationToken)
    {
        var documents = new HashSet<Guid>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FailedInRunSql, connection) { Parameters = { Db.P(source.Classification), Db.P(source.SourceId), Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(reader.GetGuid(0));
        }

        return documents;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> MediaTypesAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var types = new Dictionary<Guid, string>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(MediaTypesSql, connection) { Parameters = { Db.P(sourceId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            types[reader.GetGuid(0)] = reader.GetString(1);
        }

        return types;
    }

    private async Task<IReadOnlySet<Guid>> BeyondDepthAsync(SourceContext source, int maxDepth, CancellationToken cancellationToken)
    {
        var documents = new HashSet<Guid>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(BeyondDepthSql, connection)
        {
            Parameters = { Db.P(source.Classification), Db.P(source.SourceId), Db.P((short)maxDepth) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(reader.GetGuid(0));
        }

        return documents;
    }

    /// <summary>The removal checks' items as the outcomes <see cref="RemovalSection"/> tallies: live (kept), gone, or another answer.</summary>
    private async Task<IReadOnlyList<FetchOutcome>> RemovalOutcomesAsync(SourceContext source, Guid runId, CancellationToken cancellationToken)
    {
        var outcomes = new List<FetchOutcome>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RemovalSql, connection) { Parameters = { Db.P(source.Classification), Db.P(source.SourceId), Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var outcome = reader.GetString(0);
            var reason = Db.String(reader, 1);
            outcomes.Add(outcome switch
            {
                "added" or "updated" or "unchanged" or "degraded" => new FetchOutcome.NotModified(new Validators(null, null)),
                _ when reason is not null && GoneReasons.Contains(reason) => new FetchOutcome.Gone(reason),
                _ => new FetchOutcome.Failed(reason ?? outcome),
            });
        }

        return outcomes;
    }

    private async Task AddItemsAsync(List<RunItemRow> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        foreach (var item in items)
        {
            await commit.AddRunItemAsync(item, cancellationToken);
        }

        await commit.CommitAsync(cancellationToken);
    }
}
