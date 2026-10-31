using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Connectors.UrlList;
using Ume.Rag.Worker.Jobs;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// <c>sync</c> jobs (engine design §9.2, §10; connectors design §2.2, §13). The first job of a run (no <c>runId</c>) waits
/// while another run of the source is running, resolves the source's published pipeline version (fail closed) and opens
/// the run under its own job id; continuations carry the run. Phase <c>list</c> runs the connector's listing, <c>fetch</c>
/// tops the run's fetches up again, <c>reconcile</c> (enqueued by the <see cref="RunFinalizer"/>) applies deletions
/// through the guard. A paused or deleted source cancels the run. P1a runs <c>url-list</c> and <c>upload</c> sources;
/// <c>crawl</c> and the <c>reprocess</c>/<c>retry-failed</c> kinds are refused with <c>sync.unsupported</c> until wired.
/// </summary>
public sealed partial class SyncJobHandler(
    SourceContexts sources,
    RunStore runs,
    UrlListSource urlLists,
    UploadSource uploads,
    UrlListConnector urlListConnector,
    UploadConnector uploadConnector,
    DocumentStore documents,
    IndexWriter index,
    IDeletionProposalStore proposals,
    EngineSettings settings,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    ConfigVersionState config,
    TimeProvider time,
    ILogger<SyncJobHandler> logger) : IJobHandler
{
    public const string InvalidPayload = "sync.invalid_payload";
    public const string Unsupported = "sync.unsupported";
    public const string RunInProgress = "sync.run_in_progress";

    private static readonly TimeSpan RunInProgressDelay = TimeSpan.FromMinutes(1);

    private const string ExplicitSql = """
        SELECT DISTINCT ON ("DocumentId") "DocumentId", "Reason" FROM content."RunItems"
        WHERE "RunId" = $1 AND "DocumentId" IS NOT NULL
        ORDER BY "DocumentId", "CreatedAt" DESC, "Id" DESC
        """;

    private const string OtherRunSql = """
        SELECT EXISTS (SELECT 1 FROM content."Runs" WHERE "SourceId" = $1 AND "State" = 'running' AND "Id" <> $2)
        """;

    private const string DegradeSql = """
        UPDATE content."Runs" SET "State" = 'degraded' WHERE "Id" = $1 AND "State" = 'awaiting-deletion-confirmation'
        """;

    public string JobClass => JobClasses.Sync;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        var job = context.Job;
        if (SyncJobs.Read<SyncPayload>(job) is not { } payload)
        {
            return new JobResult.FatalError(InvalidPayload);
        }

        if (await sources.GetAsync(payload.SourceId, cancellationToken) is not { } source)
        {
            return new JobResult.FatalError("source.unknown");
        }

        var runId = payload.RunId ?? job.Id;
        if (!source.Active)
        {
            await runs.CancelAsync(runId, cancellationToken);
            return new JobResult.Success("""{"cancelled":"source.paused"}""");
        }

        if (payload.Kind != SyncKind.Sync || source.Type is not (SourceTypes.UrlList or SourceTypes.Upload))
        {
            LogUnsupported(logger, source.SourceId, source.Type, RunStore.Text(payload.Kind));
            return new JobResult.FatalError(Unsupported);
        }

        RunRow? run;
        if (payload.RunId is null)
        {
            if (await OtherRunAsync(source.SourceId, runId, cancellationToken))
            {
                return new JobResult.TransientError(RunInProgress, RunInProgressDelay, CountsAttempt: false);
            }

            if (source.PublishedVersionId is not { } versionId)
            {
                return new JobResult.FatalError("pipeline.unpublished");
            }

            try
            {
                await sources.VersionAsync(versionId, source.Type, cancellationToken);
            }
            catch (PipelineRefusedException refused)
            {
                return new JobResult.FatalError(refused.Reason);
            }

            run = await runs.OpenAsync(runId, source, payload, versionId, cancellationToken);
        }
        else
        {
            run = await runs.GetAsync(runId, cancellationToken);
        }

        if (run is not { State: RunStates.Running })
        {
            return new JobResult.Success();
        }

        return payload.Phase switch
        {
            SyncPhase.List when source.Type == SourceTypes.UrlList => await urlLists.ListAsync(source, run, payload, cancellationToken),
            SyncPhase.List => await uploads.ListAsync(source, run, payload, cancellationToken),
            SyncPhase.Fetch when source.Type == SourceTypes.UrlList => await TopUpAsync(source, run, cancellationToken),
            SyncPhase.Fetch => new JobResult.Success(),
            _ => await ReconcileAsync(source, run, job, cancellationToken),
        };
    }

    private async Task<JobResult> TopUpAsync(SourceContext source, RunRow run, CancellationToken cancellationToken)
    {
        await urlLists.TopUpAsync(source, run.Id, null, cancellationToken);
        return new JobResult.Success();
    }

    /// <summary>
    /// Connectors design §13: candidates from this run's explicit answers (<c>http.404</c>, …) and repeated failures, plus
    /// the identities the complete listing no longer has (<c>removed-from-list</c>, <c>upload.deleted</c>, exempt); the
    /// guard decides; an older waiting proposal of the source is superseded; applied deletions are soft deletes with a
    /// <c>deleted</c> run item; the run's phase ends <c>done</c> in the job's own commit.
    /// </summary>
    private async Task<JobResult> ReconcileAsync(SourceContext source, RunRow run, LeasedJob job, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var values = await settings.GetAsync(cancellationToken);
        var all = await documents.SourceDocumentsAsync(source.SourceId, cancellationToken);
        var context = new ListingContext(source.SourceId, source.Classification, run.Id, source.Settings);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var identities = source.Type == SourceTypes.UrlList
            ? urlListConnector.ListIdentitiesAsync(context, cancellationToken)
            : uploadConnector.ListIdentitiesAsync(context, cancellationToken);
        await foreach (var listedIdentity in identities)
        {
            listed.Add(listedIdentity);
        }

        var explicitGone = await ExplicitAsync(run.Id, cancellationToken);
        var candidates = DeletionCandidates.FromTraversal(run.Id, all, new TraversalVerdict(false, [], null, null), explicitGone, new HashSet<Guid>(), now).ToList();
        var reason = source.Type == SourceTypes.UrlList ? DeletionReasons.RemovedFromList : DeletionReasons.UploadDeleted;
        candidates.AddRange(DeletionCandidates.FromListing(all, listed, listingComplete: true, reason).Where(c => candidates.All(x => x.DocumentId != c.DocumentId)));
        var decision = DeletionGuard.Evaluate(candidates, DeletionCandidates.Live(all), emptyListing: listed.Count == 0, values.Guard);

        if (await proposals.WaitingRunAsync(source.SourceId, cancellationToken) is { } waiting && waiting != run.Id)
        {
            var superseded = DeletionProposalLifecycle.Supersede(await proposals.ListAsync(waiting, cancellationToken), now);
            await proposals.WriteAsync(superseded.Proposals, cancellationToken);
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var degrade = new NpgsqlCommand(DegradeSql, connection) { Parameters = { Db.P(waiting) } };
            await degrade.ExecuteNonQueryAsync(cancellationToken);
        }

        var outcome = DeletionProposalLifecycle.Reconcile(run.Id, source.SourceId, source.Classification, decision, now);
        await proposals.WriteAsync(outcome.Proposals, cancellationToken);
        await index.SoftDeleteDocumentsAsync(outcome.Delete, now, cancellationToken);
        if (decision.Tripped)
        {
            LogGuardTripped(logger, run.Id, decision.Rule.ToString(), decision.NonExempt, decision.Live);
        }

        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        var byId = all.ToDictionary(d => d.Id);
        var reasons = candidates.ToDictionary(c => c.DocumentId, c => c.Reason);
        foreach (var deleted in outcome.Delete)
        {
            await commit.AddRunItemAsync(new RunItemRow(run.Id, source.Classification, deleted, byId[deleted].CanonicalIdentity, Outcome.Deleted)
            {
                Reason = reasons.GetValueOrDefault(deleted),
            }, cancellationToken);
        }

        await runs.SetPhaseAsync(run.Id, RunPhases.Done, commit.Transaction, cancellationToken);
        if (!await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, null, cancellationToken))
        {
            return new JobResult.Success();
        }

        await commit.CommitAsync(cancellationToken);
        return new JobResult.Committed();
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ExplicitAsync(Guid runId, CancellationToken cancellationToken)
    {
        var reasons = new Dictionary<Guid, string>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ExplicitSql, connection) { Parameters = { Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Db.String(reader, 1) is { } reason)
            {
                reasons[reader.GetGuid(0)] = reason;
            }
        }

        return reasons;
    }

    private async Task<bool> OtherRunAsync(Guid sourceId, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(OtherRunSql, connection) { Parameters = { Db.P(sourceId), Db.P(runId) } };
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    [LoggerMessage(LogLevel.Warning, "Source {SourceId} ({SourceType}): sync kind {Kind} is not run by this build")]
    private static partial void LogUnsupported(ILogger logger, Guid sourceId, string sourceType, string kind);

    [LoggerMessage(LogLevel.Warning, "Run {RunId}: the deletion guard tripped ({Rule}: {Candidates} of {Live} live documents); proposals wait for a decision")]
    private static partial void LogGuardTripped(ILogger logger, Guid runId, string rule, int candidates, int live);
}
