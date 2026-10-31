using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.UrlList;
using Ume.Rag.Worker.Jobs;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The engine's part of a <c>url-list</c> source (connectors design §4, §6.0; engine design §9.2). Listing: the listed URLs
/// are admitted to the source's own frontier at depth 0 (a <c>CrawlRuns</c> row holds the run's admission count, capped at
/// the list's 5,000), committed per checkpoint with the connector checkpoint and the refused URLs' run items; a listing that
/// runs past 2 minutes yields to a continuation. Release: each host keeps at most <c>Crawl:FetchBatchPerHost</c> fetches
/// queued or running, topped up after the listing and whenever one of its fetches ends; a released row whose job is gone is
/// enqueued again (dedup <c>fetch:{sourceId}:{urlHash}</c>). Fetch: the row's host slot (§6.3), the connector's conditional
/// GET, then <see cref="DocumentIngest"/>, which completes the frontier row in the document's commit.
/// </summary>
public sealed class UrlListSource(
    UrlListConnector connector,
    ICrawlFrontierStore frontier,
    ICrawlRunStore crawlRuns,
    IConnectorCheckpointStore checkpoints,
    HostBudget budget,
    RunStore runs,
    DocumentIngest ingest,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    IOptions<CrawlOptions> options,
    TimeProvider time)
{
    /// <summary>A listing yields after this long (engine design §9.2).</summary>
    public static readonly TimeSpan ListingSlice = TimeSpan.FromMinutes(2);

    /// <summary>The URL list's cap (connectors design §4): the run's admission limit.</summary>
    public const int MaxUrls = 5000;

    private const string ReleasedSql = """
        SELECT "UrlHash" FROM content."CrawlFrontier"
        WHERE "Classification" = $1 AND "SourceId" = $2 AND "RunId" = $3 AND "State" = 'released'
        """;

    public async Task<JobResult> ListAsync(SourceContext source, RunRow run, SyncPayload payload, LeasedJob job, CancellationToken cancellationToken)
    {
        var key = new CrawlRunKey(source.SourceId, source.Classification, run.Id);
        await crawlRuns.StartAsync(new CrawlRunRecord
        {
            RunId = run.Id,
            Classification = source.Classification,
            SourceId = source.SourceId,
            MaxPages = MaxUrls,
            MaxDepth = 0,
        }, cancellationToken);
        var from = payload.ResumeFromCheckpoint
            ? await checkpoints.GetAsync<UrlListCheckpoint>(source.SourceId, CheckpointKind.Run, run.Id, cancellationToken) ?? new UrlListCheckpoint()
            : new UrlListCheckpoint();
        var started = time.GetTimestamp();
        var admissions = new List<FrontierAdmission>();
        var refused = new List<RunItemRow>();
        var context = new ListingContext(source.SourceId, source.Classification, run.Id, source.Settings);
        await foreach (var listed in connector.ListAsync(context, from, cancellationToken))
        {
            switch (listed)
            {
                case ListingEvent<UrlListCheckpoint>.Item { Work: var work }:
                    admissions.Add(new FrontierAdmission(work.CanonicalIdentity, work.Url!.Authority, 0, false));
                    break;
                case ListingEvent<UrlListCheckpoint>.Failure failure:
                    refused.Add(new RunItemRow(run.Id, source.Classification, null, failure.Identity, Outcome.Failed) { Reason = failure.Reason });
                    break;
                case ListingEvent<UrlListCheckpoint>.Checkpoint checkpoint:
                    await frontier.AdmitAsync(key, admissions, cancellationToken);
                    await AddItemsAsync(refused, cancellationToken);
                    await checkpoints.SaveAsync(source.SourceId, source.Classification, CheckpointKind.Run, run.Id, checkpoint.State, cancellationToken);
                    admissions.Clear();
                    refused.Clear();
                    if (time.GetElapsedTime(started) > ListingSlice)
                    {
                        // Yield: the continuation resumes from the checkpoint just saved (no attempt, engine design §9.2); completed with
                        // this job, so a continuation that yields again does not lose its successor to its own dedup key.
                        return await runs.ContinueAsync(queue, job, identity.InstanceId, SyncJobs.Continuation(source.SourceId, run.Id, source.DepartmentId,
                            source.Classification, run.Kind, run.Trigger, SyncPhase.List, resumeFromCheckpoint: true), run.Id, null, cancellationToken);
                    }

                    break;
                case ListingEvent<UrlListCheckpoint>.Done:
                    await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, null, cancellationToken);
                    await TopUpAsync(source, run.Id, null, cancellationToken);
                    return new JobResult.Success();
            }
        }

        return new JobResult.Success();
    }

    /// <summary>Tops up one host (after its fetch ended) or every host of the run; re-enqueues released rows without a job.</summary>
    public async Task TopUpAsync(SourceContext source, Guid runId, string? host, CancellationToken cancellationToken)
    {
        var key = new CrawlRunKey(source.SourceId, source.Classification, runId);
        foreach (var load in await frontier.HostLoadAsync(key, cancellationToken))
        {
            var want = options.Value.FetchBatchPerHost - load.Released;
            if ((host is null || string.Equals(load.Host, host, StringComparison.Ordinal)) && load.Pending > 0 && want > 0)
            {
                await frontier.ReleaseAsync(key, load.Host, want, cancellationToken);
            }
        }

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
    }

    public async Task<JobResult> FetchAsync(SourceContext source, RunRow run, PipelineRun pipeline, FetchPayload payload, LeasedJob job, CancellationToken cancellationToken)
    {
        var row = await frontier.GetAsync(source.SourceId, source.Classification, payload.UrlHash!, cancellationToken);
        if (row is null || row.RunId != run.Id || row.State != FrontierState.Released)
        {
            return new JobResult.Success();
        }

        var fetch = new FetchCase(source, run, pipeline, job, IdentityKinds.Url, row.Url, Db.Bytes(row.UrlHash));
        JobResult result;
        switch (await budget.AcquireAsync(row.Host, cancellationToken))
        {
            case HostSlot.Later later:
                return new JobResult.TransientError(later.Reason, later.Delay, CountsAttempt: false);
            case HostSlot.Refused { Until: { } until } paused when until > time.GetUtcNow():
                return new JobResult.TransientError(paused.Reason, until - time.GetUtcNow(), CountsAttempt: false);
            case HostSlot.Refused refused:
                result = await ingest.HandleAsync(fetch, new FetchOutcome.Failed(refused.Reason), cancellationToken);
                break;
            case HostSlot.Granted granted:
                using (granted.Lease)
                {
                    var item = new WorkItem(IdentityKind.Url, row.Url, new Uri(row.Url), 0,
                        new WorkHints(row.ETag is null && row.LastModified is null ? null : new Validators(row.ETag, row.LastModified), row.ContentHash));
                    var started = time.GetTimestamp();
                    var outcome = await connector.FetchAsync(new FetchContext(source.SourceId, source.Classification, run.Id, source.Settings, Revalidate: false),
                        item, cancellationToken);
                    await granted.Lease.CompleteAsync(Response(outcome, time.GetElapsedTime(started)), cancellationToken);
                    result = await ingest.HandleAsync(fetch, outcome, cancellationToken);
                }

                break;
            default:
                throw new InvalidOperationException("Unexpected host slot.");
        }

        if (result is JobResult.Committed)
        {
            await TopUpAsync(source, run.Id, row.Host, cancellationToken);
        }

        return result;
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

    private static HostResponse Response(FetchOutcome outcome, TimeSpan latency) => outcome switch
    {
        FetchOutcome.Content or FetchOutcome.NotModified => new HostResponse(HostResponseKind.Success, latency),
        FetchOutcome.Transient { Reason: "rate_limited", RetryAfter: var retryAfter } => new HostResponse(HostResponseKind.RateLimited, latency, retryAfter),
        FetchOutcome.Transient { Reason: "http.5xx" or "net.timeout" } or FetchOutcome.Gone { Reason: "http.404" } => new HostResponse(HostResponseKind.Failure, latency),
        _ => new HostResponse(HostResponseKind.Neutral, latency),
    };
}
