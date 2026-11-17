using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Connectors.UrlList;
using Ume.Rag.Worker.Jobs;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>Where a <c>reprocess</c> run's iteration stands: the last document handled, in id order.</summary>
public sealed record ReprocessCheckpoint : Connectors.ConnectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public Guid? After { get; init; }
}

/// <summary>
/// The <c>reprocess</c> and <c>retry-failed</c> kinds (engine design §2.3, §9.2, §12.2; connectors design §2.2), for URL
/// lists, uploads and crawls. <c>reprocess</c> iterates the source's live documents in id order and calls
/// <c>EnsureCurrent</c> against the run's pinned version (<see cref="DocumentIngest.EnsureCurrentAsync"/>): never a fetch,
/// the stages from the first one without cached output, a set differing only by label re-keyed by its <c>embed</c> job;
/// current documents get an <c>unchanged</c> item; the iteration resumes from the run's checkpoint and yields after 1,000
/// documents or 2 minutes. A forced <c>reprocess</c> (<c>force</c> with <c>fromStage</c>, engine design §8) re-processes
/// every live document instead, under a new execution id each, with the output cache bypassed from that stage on
/// (<see cref="DocumentIngest.ForceAsync"/>); its continuations carry the bypass. <c>retry-failed</c> takes the
/// identities whose latest item of an earlier run of the source failed, rebuilds their work through the connector (<see cref="IRetryFailedConnector"/>) and fetches only those: URL-list
/// and crawl rows are admitted to the source's frontier for this run (a crawl's as leaves, so a retried page adds no
/// links), uploads get their slot's <c>fetch</c> job. Neither kind reconciles deletions; both end through the finalizer.
/// </summary>
public sealed class SourceReruns(
    SourceContexts sources,
    DocumentStore documents,
    DocumentIngest ingest,
    UrlListConnector urlListConnector,
    UrlListSource urlLists,
    UploadConnector uploadConnector,
    CrawlConnector crawlConnector,
    CrawlSourceRunner crawls,
    ICrawlFrontierStore frontier,
    ICrawlRunStore crawlRuns,
    IConnectorCheckpointStore checkpoints,
    RunStore runs,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    TimeProvider time)
{
    // What a continue run takes over from the run it continues (#804): that run's rows still open, and its rows whose latest
    // item failed (a failed fetch completes its row idle). Rows a later run admitted carry that run's id and are not taken.
    private const string CarriedSql = """
        SELECT f."Url", f."FetchUrl", f."Host", f."Depth", f."Leaf", f."RemovalCheck", f."SitemapLastMod", f."State" <> 'idle'
        FROM content."CrawlFrontier" f
        WHERE f."Classification" = $1 AND f."SourceId" = $2 AND f."RunId" = $3
          AND (f."State" <> 'idle' OR (
              SELECT i."Outcome" FROM content."RunItems" i
              WHERE i."RunId" = $3 AND i."Identity" = f."Url" AND i."Outcome" <> 'superseded'
              ORDER BY i."CreatedAt" DESC, i."Id" DESC LIMIT 1) = 'failed')
        ORDER BY f."Depth", f."Url"
        """;

    /// <summary>A reprocess iteration yields after this many documents (engine design §9.2).</summary>
    public const int ReprocessItems = 1000;

    private const int Page = 100;

    // The latest item of each identity in the source's earlier runs; those that failed, unless their document is deleted. With
    // the URL it was found or fetched under: its frontier row's FetchUrl (UrlHash = SHA-256 of the identity), else Documents.Url.
    private const string FailedSql = """
        SELECT last."Identity", last."DocumentId", last."Reason", coalesce(f."FetchUrl", d."Url") FROM (
            SELECT DISTINCT ON (i."Identity") i."Identity", i."DocumentId", i."Reason", i."Outcome"
            FROM content."RunItems" i JOIN content."Runs" r ON r."Id" = i."RunId"
            WHERE r."SourceId" = $1 AND i."RunId" <> $2 AND i."Outcome" <> 'superseded'
            ORDER BY i."Identity", i."CreatedAt" DESC, i."Id" DESC) last
        LEFT JOIN content."Documents" d ON d."Id" = last."DocumentId"
        LEFT JOIN content."CrawlFrontier" f ON f."Classification" = $3 AND f."SourceId" = $1 AND f."UrlHash" = sha256(convert_to(last."Identity", 'UTF8'))
        WHERE last."Outcome" = 'failed' AND d."DeletedAt" IS NULL
        ORDER BY last."Identity"
        """;

    /// <summary>Phase <c>list</c> of a crawl's <c>continue</c> run (#804).</summary>
    /// <remarks>
    /// Takes over what <paramref name="continuesRunId"/> left: its frontier rows still <c>pending</c> or <c>released</c>,
    /// and the rows whose latest item in that run failed, each at its depth and with its leaf and removal-check flags, so
    /// the traversal goes on with link discovery instead of starting again from the seeds. Each row is checked against the
    /// current scope on the form it was found under (<see cref="CrawlScope.CheckStored"/>) and the current
    /// <c>maxDepth</c>; the ones dropped are counted. The run lists no seeds, fetches in the current fetch mode without
    /// revalidating, and is never a complete traversal (<see cref="Connectors.Deletion.TraversalCompleteness"/>), so its reconcile proposes no
    /// <c>not-seen</c> deletions. The report's <c>continued</c> section says what was carried over.
    /// </remarks>
    public async Task<JobResult> ContinueAsync(SourceContext source, RunRow run, Guid continuesRunId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        var context = await crawls.StartAsync(source, run, incremental: false, force: false, cancellationToken);
        var crawl = await crawlConnector.SourceAsync(source.SourceId, source.Classification, source.Settings, cancellationToken);
        var admissions = new List<FrontierAdmission>();
        int open = 0, failed = 0, dropped = 0;
        foreach (var row in await CarriedAsync(source, continuesRunId, cancellationToken))
        {
            if (row.Depth > crawl.Settings.MaxDepth || !crawl.Scope.CheckStored(row.Url, row.FetchUrl, crawl.Canonicalizer).Admitted)
            {
                dropped++;
                continue;
            }

            if (row.Open)
            {
                open++;
            }
            else
            {
                failed++;
            }

            admissions.Add(new FrontierAdmission(row.Url, row.Host, row.Depth, row.Leaf, row.SitemapLastMod, row.RemovalCheck, row.FetchUrl));
        }

        var admitted = await frontier.AdmitAsync(context.Run, admissions, cancellationToken);
        var report = ContractJson.Serialize(new { continued = new ContinuedSection(continuesRunId, open, failed, dropped, admitted.Admitted.Count) });
        await using (var commit = await EngineCommit.BeginAsync(database, cancellationToken))
        {
            await RunStore.AddReportAsync(run.Id, report, commit.Transaction, cancellationToken);
            await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, commit.Transaction, cancellationToken);
            await commit.CommitAsync(cancellationToken);
        }

        await crawls.TopUpAsync(context, cancellationToken);
        return new JobResult.Success();
    }

    /// <summary>Phase <c>list</c> of a <c>reprocess</c> run; <paramref name="bypassFrom"/> is a forced run's first bypassed stage.</summary>
    public async Task<JobResult> ReprocessAsync(SourceContext source, RunRow run, LeasedJob job, string? bypassFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(job);
        var pipeline = await sources.VersionAsync(run.PipelineVersionId ?? throw new InvalidOperationException("A reprocess run has a pipeline version."),
            source.Type, cancellationToken);
        // The run's own checkpoint, also for a first job retried after a crash: a run never resumes another run's iteration.
        var after = (await checkpoints.GetAsync<ReprocessCheckpoint>(source.SourceId, CheckpointKind.Run, run.Id, cancellationToken))?.After;
        var started = time.GetTimestamp();
        var handled = 0;
        while (true)
        {
            var page = await documents.LiveDocumentsAsync(source.SourceId, after, Page, cancellationToken);
            var unchanged = new List<RunItemRow>();
            foreach (var document in page)
            {
                if (document.ContentHash is not null && bypassFrom is not null)
                {
                    await ingest.ForceAsync(source, run.Id, pipeline, document, bypassFrom, cancellationToken);
                }
                else if (document.ContentHash is not null && await ingest.EnsureCurrentAsync(source, run.Id, pipeline, document, cancellationToken) == EnsureOutcome.Current)
                {
                    unchanged.Add(new RunItemRow(run.Id, document.Classification, document.Id, document.CanonicalIdentity, Outcome.Unchanged)
                    {
                        Strategy = document.ParseStrategy,
                        PipelineVersionId = pipeline.VersionId,
                        ExecutionId = document.CurrentExecutionId,
                    });
                }

                after = document.Id;
                handled++;
            }

            await AddItemsAsync(unchanged, cancellationToken);
            if (page.Count < Page)
            {
                await runs.SetPhaseAsync(run.Id, RunPhases.Done, null, cancellationToken);
                return new JobResult.Success();
            }

            await checkpoints.SaveAsync(source.SourceId, source.Classification, CheckpointKind.Run, run.Id,
                new ReprocessCheckpoint { SchemaVersion = ReprocessCheckpoint.CurrentSchemaVersion, After = after }, cancellationToken);
            if (handled >= ReprocessItems || time.GetElapsedTime(started) > UrlListSource.ListingSlice)
            {
                return await runs.ContinueAsync(queue, job, identity.InstanceId, SyncJobs.Continuation(source.SourceId, run.Id, source.DepartmentId,
                    source.Classification, run.Kind, run.Trigger, SyncPhase.List, resumeFromCheckpoint: true, bypassFrom), run.Id, null, cancellationToken);
            }
        }
    }

    /// <summary>Phase <c>list</c> of a <c>retry-failed</c> run.</summary>
    public async Task<JobResult> RetryFailedAsync(SourceContext source, RunRow run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        IRetryFailedConnector connector = source.Type switch
        {
            SourceTypes.UrlList => urlListConnector,
            SourceTypes.Crawl => crawlConnector,
            _ => uploadConnector,
        };
        var work = new List<WorkItem>();
        foreach (var (identityText, last) in await FailedAsync(source, run.Id, cancellationToken))
        {
            if (connector.FromIdentity(identityText, last) is { } item)
            {
                work.Add(item);
            }
        }

        switch (source.Type)
        {
            case SourceTypes.UrlList:
                await RetryUrlListAsync(source, run, work, cancellationToken);
                break;
            case SourceTypes.Crawl:
                await RetryCrawlAsync(source, run, work, cancellationToken);
                break;
            default:
                var jobs = work.Select(w => w.Hints.Upload!.UploadId).Distinct().Select(id => SyncJobs.FetchUpload(source, run.Id, id)).ToList();
                if (jobs.Count > 0)
                {
                    await queue.EnqueueAsync(jobs, null, cancellationToken);
                }

                await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, null, cancellationToken);
                break;
        }

        return new JobResult.Success();
    }

    /// <summary>Only identities the list still has: a removed one is the reconcile's business, not a retry's.</summary>
    private async Task RetryUrlListAsync(SourceContext source, RunRow run, List<WorkItem> work, CancellationToken cancellationToken)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var listedIdentity in urlListConnector.ListIdentitiesAsync(new ListingContext(source.SourceId, source.Classification, run.Id, source.Settings),
            cancellationToken))
        {
            listed.Add(listedIdentity);
        }

        var key = new CrawlRunKey(source.SourceId, source.Classification, run.Id);
        await crawlRuns.StartAsync(new CrawlRunRecord
        {
            RunId = run.Id,
            Classification = source.Classification,
            SourceId = source.SourceId,
            MaxPages = UrlListSource.MaxUrls,
            MaxDepth = 0,
        }, cancellationToken);
        await frontier.AdmitAsync(key, [.. work.Where(w => listed.Contains(w.CanonicalIdentity)).Select(w => new FrontierAdmission(w.CanonicalIdentity, w.Url!.Authority, 0, false))],
            cancellationToken);
        await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, null, cancellationToken);
        await urlLists.TopUpAsync(source, run.Id, null, cancellationToken);
    }

    /// <summary>
    /// Only URLs still in scope, admitted as leaves: the scope is checked on the path form the URL was found or fetched
    /// under, and on its identity where none is kept (<see cref="CrawlScope.CheckStored"/>); the path form stays the row's fetch URL.
    /// </summary>
    private async Task RetryCrawlAsync(SourceContext source, RunRow run, List<WorkItem> work, CancellationToken cancellationToken)
    {
        var context = await crawls.StartAsync(source, run, incremental: false, force: false, cancellationToken);
        var crawl = await crawlConnector.SourceAsync(source.SourceId, source.Classification, source.Settings, cancellationToken);
        var admissions = new List<FrontierAdmission>();
        foreach (var item in work)
        {
            var path = CrawlScope.PathFormOf(item.CanonicalIdentity, item.Url!.OriginalString, crawl.Canonicalizer);
            if ((path is null ? crawl.Scope.CheckIdentity(item.CanonicalIdentity, crawl.Canonicalizer) : crawl.Scope.Check(path)).Admitted)
            {
                admissions.Add(new FrontierAdmission(item.CanonicalIdentity, item.Url.Authority, 0, Leaf: true, FetchUrl: path));
            }
        }

        await frontier.AdmitAsync(context.Run, admissions, cancellationToken);
        await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, null, cancellationToken);
        await crawls.TopUpAsync(context, cancellationToken);
    }

    private async Task<IReadOnlyList<CarriedRow>> CarriedAsync(SourceContext source, Guid continuesRunId, CancellationToken cancellationToken)
    {
        var rows = new List<CarriedRow>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CarriedSql, connection)
        {
            Parameters = { Db.P(source.Classification), Db.P(source.SourceId), Db.P(continuesRunId) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CarriedRow(reader.GetString(0), Db.String(reader, 1), reader.GetString(2), reader.GetInt16(3), reader.GetBoolean(4),
                reader.GetBoolean(5), Db.Nullable<DateTimeOffset>(reader, 6), reader.GetBoolean(7)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<(string Identity, FailedItem Last)>> FailedAsync(SourceContext source, Guid runId, CancellationToken cancellationToken)
    {
        var failed = new List<(string, FailedItem)>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FailedSql, connection) { Parameters = { Db.P(source.SourceId), Db.P(runId), Db.P(source.Classification) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            failed.Add((reader.GetString(0), new FailedItem(Db.Nullable<Guid>(reader, 1), Db.String(reader, 2), Db.String(reader, 3))));
        }

        return failed;
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

/// <summary>A frontier row a <c>continue</c> run takes over; <see cref="Open"/> it was still pending or released, else its fetch failed.</summary>
internal sealed record CarriedRow(string Url, string? FetchUrl, string Host, short Depth, bool Leaf, bool RemovalCheck, DateTimeOffset? SitemapLastMod, bool Open);

/// <summary>
/// The <c>continued</c> section of a continue run's report (#804): the run it went on from, the rows taken over that were still
/// open and those whose fetch had failed, the rows the current scope or <c>maxDepth</c> dropped, and the rows admitted (fewer
/// than taken over when <c>maxPages</c> was reached).
/// </summary>
public sealed record ContinuedSection(Guid From, int Open, int Failed, int Dropped, int Admitted);
