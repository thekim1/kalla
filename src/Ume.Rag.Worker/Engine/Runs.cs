using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Engine;

/// <summary>A <c>content."Runs"</c> row as the engine reads it.</summary>
public sealed record RunRow(Guid Id, Guid SourceId, string Classification, string Kind, string Trigger, string? Phase, string State, Guid? PipelineVersionId);

/// <summary>
/// How a crawl run fetches (connectors design §5.4), fixed when its listing starts and kept in <c>Runs.Report</c> under
/// <c>mode</c>, so every job of the run, and the next runs' <c>revalidateEvery</c> count, read the same answer.
/// </summary>
/// <param name="Incremental">An incremental run (kept only on hosts whose profile marks <c>lastmod</c> reliable).</param>
/// <param name="Revalidate">No validators and no <c>lastmod</c> skips in this run.</param>
public sealed record CrawlRunMode(bool Incremental, bool Revalidate);

/// <summary>An earlier crawl run's completeness, newest first, for the incomplete-traversal count.</summary>
public sealed record CrawlRunHistory(bool Complete, bool Incremental);

/// <summary>
/// <c>content."Runs"</c> (engine design §10): a run is opened by the first <c>sync</c> job of a source with the job's own id,
/// so a retried first job finds its run instead of opening a second one. Counters are never incremented by jobs: the
/// <see cref="RunFinalizer"/> derives them from the run items.
/// </summary>
public sealed class RunStore(JobDatabase database)
{
    private const string OpenSql = """
        INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "PipelineVersionId", "ScheduledFor", "Phase", "State",
                                   "Added", "Updated", "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd")
        VALUES ($1, $2, $3, $4, $5, $6, $7, 'list', 'running', 0, 0, 0, 0, 0, 0, 0)
        ON CONFLICT ("Id") DO NOTHING
        """;

    private const string GetSql = """
        SELECT "Id", "SourceId", "Classification", "Kind", "Trigger", "Phase", "State", "PipelineVersionId" FROM content."Runs" WHERE "Id" = $1
        """;

    private const string PhaseSql = """UPDATE content."Runs" SET "Phase" = $2 WHERE "Id" = $1 AND "State" = 'running'""";

    private const string CancelSql = """
        UPDATE content."Runs" SET "State" = 'cancelled', "Phase" = 'done', "FinishedAt" = now() WHERE "Id" = $1 AND "State" = 'running'
        """;

    private const string ModeSql = """SELECT ("Report" -> 'mode')::text FROM content."Runs" WHERE "Id" = $1""";

    private const string SetModeSql = """
        UPDATE content."Runs" SET "Report" = coalesce("Report", '{}'::jsonb) || jsonb_build_object('mode', $2::jsonb)
         WHERE "Id" = $1 AND ("Report" -> 'mode') IS NULL
        """;

    // Earlier full traversals of the source (incremental runs are no traversals), for revalidateEvery.
    private const string TraversalsSql = """
        SELECT count(*) FROM content."Runs"
        WHERE "SourceId" = $1 AND "Kind" = 'sync' AND "Id" <> $2 AND ("Report" -> 'mode' ->> 'incremental') = 'false'
          AND "StartedAt" < (SELECT "StartedAt" FROM content."Runs" WHERE "Id" = $2)
        """;

    private const string HistorySql = """
        SELECT ("Report" -> 'completeness' ->> 'complete')::boolean, coalesce(("Report" -> 'mode' ->> 'incremental')::boolean, false)
        FROM content."Runs"
        WHERE "SourceId" = $1 AND "Kind" = 'sync' AND "Id" <> $2 AND ("Report" -> 'completeness') IS NOT NULL
          AND "StartedAt" < (SELECT "StartedAt" FROM content."Runs" WHERE "Id" = $2)
        ORDER BY "StartedAt" DESC
        LIMIT 100
        """;

    private const string ReportSql = """UPDATE content."Runs" SET "Report" = coalesce("Report", '{}'::jsonb) || $2::jsonb WHERE "Id" = $1""";

    public async Task<RunRow> OpenAsync(Guid runId, SourceContext source, SyncPayload payload, Guid? pipelineVersionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload);
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(OpenSql, connection)
        {
            Parameters =
            {
                Db.P(runId), Db.P(source.SourceId), Db.P(source.Classification), Db.P(Text(payload.Kind)), Db.P(Text(payload.Trigger)),
                Db.P(pipelineVersionId), Db.P(Db.Utc(payload.ScheduledFor)),
            },
        })
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await GetAsync(runId, cancellationToken) ?? throw new InvalidOperationException("The run was not opened.");
    }

    public async Task<RunRow?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(GetSql, connection) { Parameters = { Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RunRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Db.String(reader, 5),
            reader.GetString(6), Db.Nullable<Guid>(reader, 7));
    }

    /// <summary>Moves a running run to <paramref name="phase"/>; inside the caller's transaction when given.</summary>
    public async Task SetPhaseAsync(Guid runId, string phase, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await using var inTransaction = new NpgsqlCommand(PhaseSql, transaction.Connection, transaction) { Parameters = { Db.P(runId), Db.P(phase) } };
            await inTransaction.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PhaseSql, connection) { Parameters = { Db.P(runId), Db.P(phase) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary><c>cancel-run</c>, a paused or deleted source: the run ends <c>cancelled</c> (engine design §10.1, §15).</summary>
    public async Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CancelSql, connection) { Parameters = { Db.P(runId) } };
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>The crawl run's mode, or null before its listing fixed one.</summary>
    public async Task<CrawlRunMode?> CrawlModeAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ModeSql, connection) { Parameters = { Db.P(runId) } };
        return await command.ExecuteScalarAsync(cancellationToken) is string json ? JsonSerializer.Deserialize<CrawlRunMode>(json, JsonSerializerOptions.Web) : null;
    }

    /// <summary>Fixes the crawl run's mode once; a resumed listing keeps the first one, which it returns.</summary>
    public async Task<CrawlRunMode> SetCrawlModeAsync(Guid runId, CrawlRunMode mode, CancellationToken cancellationToken)
    {
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(SetModeSql, connection)
        {
            Parameters = { Db.P(runId), Db.P(JsonSerializer.Serialize(mode, JsonSerializerOptions.Web)) },
        })
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await CrawlModeAsync(runId, cancellationToken) ?? mode;
    }

    /// <summary>The source's full traversals started before <paramref name="runId"/> (<c>revalidateEvery</c>, connectors design §5.4).</summary>
    public async Task<int> TraversalsBeforeAsync(Guid sourceId, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TraversalsSql, connection) { Parameters = { Db.P(sourceId), Db.P(runId) } };
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The source's earlier crawl runs with a completeness section, newest first.</summary>
    public async Task<IReadOnlyList<CrawlRunHistory>> CrawlHistoryAsync(Guid sourceId, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(HistorySql, connection) { Parameters = { Db.P(sourceId), Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new List<CrawlRunHistory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new CrawlRunHistory(!reader.IsDBNull(0) && reader.GetBoolean(0), reader.GetBoolean(1)));
        }

        return history;
    }

    /// <summary>Merges <paramref name="sections"/> (a JSON object) into <c>Runs.Report</c>, in the caller's transaction.</summary>
    public static async Task AddReportAsync(Guid runId, string sections, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await using var command = new NpgsqlCommand(ReportSql, transaction.Connection, transaction) { Parameters = { Db.P(runId), Db.P(sections) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// A <c>sync</c> job hands its run on (engine design §7.4, §9.2): in one transaction the phase (when given) is set, the
    /// job completes and <paramref name="continuation"/> is enqueued. Completing first frees the run's continuation dedup
    /// key, which a continuation that yields again holds itself. When the lease was lost nothing is written.
    /// </summary>
    public async Task<JobResult> ContinueAsync(IJobQueue queue, LeasedJob job, Guid owner, JobRequest continuation, Guid runId, string? phase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(job);
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (phase is not null)
        {
            await SetPhaseAsync(runId, phase, commit.Transaction, cancellationToken);
        }

        if (!await commit.CompleteJobAsync(queue, job.Id, owner, null, cancellationToken))
        {
            return new JobResult.Success();
        }

        await commit.EnqueueAsync(queue, [continuation], cancellationToken);
        await commit.CommitAsync(cancellationToken);
        return new JobResult.Committed();
    }

    /// <summary><c>Runs.Kind</c> and <c>Runs.Trigger</c> as the CHECK constraints list them (kebab case).</summary>
    public static string Text<TEnum>(TEnum value) where TEnum : struct, Enum => ContractJson.Serialize(value).Trim('"');
}

/// <summary>
/// The run finalizer (engine design §10.3–§10.4): every <c>Worker:FinalizerSeconds</c> (30 s) the instance holding the
/// advisory lock looks at <c>running</c> runs that no queued or running job serves. A <c>sync</c> run whose fetches are done
/// first gets its <c>reconcile</c> job (deletions and the guard); a URL-list or crawl run (<c>sync</c> or
/// <c>retry-failed</c>) with frontier rows still open gets a fetch top-up first, so a job lost between a release and its
/// enqueue, or a worker that died before its top-up, is not lost work. <c>retry-failed</c> and <c>reprocess</c> runs have
/// no reconcile. Then the counters are derived from the run items (the latest item per document or identity;
/// <c>superseded</c> items are not counted), jobs that died without an item count as failed, and the state is set:
/// <c>failed</c> when the sync job failed, every processed item failed or the crawl reached no seed; <c>awaiting-deletion-confirmation</c>
/// while the guard holds proposals; <c>degraded</c> above <c>Settings.DegradedFailureRatio</c>, on any <c>malware</c>, or
/// when the crawl hit its page limit or a host was paused (the report's <c>verdict</c>); else <c>succeeded</c>.
/// </summary>
public sealed partial class RunFinalizer(
    JobDatabase database,
    IJobQueue queue,
    EngineSettings settings,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<RunFinalizer> logger) : BackgroundService
{
    private const long LockKey = 0x5241_4731_0001;

    private const string IdleRunsSql = """
        SELECT r."Id", r."SourceId", r."Classification", r."Kind", r."Trigger", r."Phase", s."Type", c."DepartmentId",
               EXISTS (SELECT 1 FROM content."CrawlFrontier" f
                        WHERE f."Classification" = r."Classification" AND f."SourceId" = r."SourceId" AND f."RunId" = r."Id" AND f."State" <> 'idle'),
               r."Report" -> 'verdict' ->> 'runFailure', r."Report" -> 'verdict' ->> 'runDegradation'
        FROM content."Runs" r
        JOIN config."Sources" s ON s."Id" = r."SourceId"
        JOIN config."Collections" c ON c."Id" = s."CollectionId"
        WHERE r."State" = 'running'
          AND NOT EXISTS (SELECT 1 FROM content."Jobs" j WHERE (j."RunId" = r."Id" OR j."Id" = r."Id") AND j."State" IN ('queued', 'running'))
        ORDER BY r."StartedAt"
        LIMIT 100
        """;

    private const string CountersSql = """
        WITH latest AS (
            SELECT DISTINCT ON (coalesce("DocumentId"::text, "Identity")) "Outcome"
            FROM content."RunItems"
            WHERE "RunId" = $1 AND "Outcome" <> 'superseded'
            ORDER BY coalesce("DocumentId"::text, "Identity"), "CreatedAt" DESC, "Id" DESC)
        SELECT count(*) FILTER (WHERE "Outcome" = 'added'),
               count(*) FILTER (WHERE "Outcome" IN ('updated', 'degraded')),
               count(*) FILTER (WHERE "Outcome" IN ('unchanged', 'skipped')),
               count(*) FILTER (WHERE "Outcome" = 'deleted'),
               count(*) FILTER (WHERE "Outcome" IN ('unsupported', 'too-large', 'encrypted', 'malware', 'blocked', 'failed')),
               count(*) FILTER (WHERE "Outcome" = 'malware'),
               (SELECT coalesce(sum("CostUsd"), 0) FROM content."RunItems" WHERE "RunId" = $1),
               (SELECT count(*) FROM content."Jobs" WHERE "RunId" = $1 AND "Class" IN ('fetch', 'parse', 'embed') AND "State" IN ('failed', 'dead')),
               EXISTS (SELECT 1 FROM content."Jobs" WHERE ("RunId" = $1 OR "Id" = $1) AND "Class" = 'sync' AND "State" IN ('failed', 'dead')),
               (SELECT count(*) FROM content."DeletionProposals" WHERE "RunId" = $1 AND "Decision" = 'pending')
        FROM latest
        """;

    private const string FinishSql = """
        UPDATE content."Runs"
           SET "Added" = $2, "Updated" = $3, "Unchanged" = $4, "Deleted" = $5, "Failed" = $6, "ProposedDeletions" = $7, "CostUsd" = $8,
               "State" = $9, "Phase" = 'done', "FinishedAt" = now()
         WHERE "Id" = $1 AND "State" = 'running'
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.FinalizerSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await FinalizeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>One pass; returns the runs finished.</summary>
    public async Task<int> FinalizeAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } })
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return 0;
            }
        }

        var idle = new List<IdleRun>();
        await using (var command = new NpgsqlCommand(IdleRunsSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                idle.Add(new IdleRun(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    Db.String(reader, 5), reader.GetString(6), reader.GetGuid(7), reader.GetBoolean(8), Db.String(reader, 9), Db.String(reader, 10)));
            }
        }

        var finished = 0;
        foreach (var run in idle)
        {
            // Rows still open with no job are released again by a fetch-phase continuation (URL lists and crawls).
            var topUp = run.OpenFrontier && run.SourceType is SourceTypes.UrlList or SourceTypes.Crawl;
            if (run.Phase is RunPhases.List or RunPhases.Fetch && (run.Kind == RunKinds.Sync || (run.Kind == RunKinds.RetryFailed && topUp)))
            {
                var phase = topUp ? SyncPhase.Fetch : SyncPhase.Reconcile;
                if (phase == SyncPhase.Reconcile)
                {
                    await using var setPhase = new NpgsqlCommand("""UPDATE content."Runs" SET "Phase" = 'reconcile' WHERE "Id" = $1""", connection, transaction)
                    {
                        Parameters = { Db.P(run.Id) },
                    };
                    await setPhase.ExecuteNonQueryAsync(cancellationToken);
                }

                await queue.EnqueueAsync([SyncJobs.Continuation(run.SourceId, run.Id, run.DepartmentId, run.Classification, run.Kind, run.Trigger, phase)],
                    transaction, cancellationToken);
                continue;
            }

            await using var counters = new NpgsqlCommand(CountersSql, connection, transaction) { Parameters = { Db.P(run.Id) } };
            Counters c;
            await using (var reader = await counters.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                c = new Counters(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4) + reader.GetInt64(7),
                    reader.GetInt64(5), reader.GetDecimal(6), reader.GetBoolean(8) || run.Phase == RunPhases.Reconcile, reader.GetInt64(9));
            }

            var state = StateOf(c, values.DegradedFailureRatio);
            if (run.CrawlFailure is not null)
            {
                state = RunStates.Failed;
            }
            else if (run.CrawlDegradation is not null && state == RunStates.Succeeded)
            {
                state = RunStates.Degraded;
            }

            await using var finish = new NpgsqlCommand(FinishSql, connection, transaction)
            {
                Parameters =
                {
                    Db.P(run.Id), Db.P((int)c.Added), Db.P((int)c.Updated), Db.P((int)c.Unchanged), Db.P((int)c.Deleted), Db.P((int)c.Failed),
                    Db.P((int)c.Proposed), Db.P(c.Cost), Db.P(state),
                },
            };
            finished += await finish.ExecuteNonQueryAsync(cancellationToken);
            LogFinished(logger, run.Id, state, c.Added, c.Updated, c.Unchanged, c.Deleted, c.Failed);
        }

        await transaction.CommitAsync(cancellationToken);
        return finished;
    }

    /// <summary>Engine design §10.3.</summary>
    internal static string StateOf(Counters c, decimal degradedRatio)
    {
        var processed = c.Added + c.Updated + c.Unchanged + c.Deleted + c.Failed;
        if (c.SyncFailed || (processed > 0 && c.Failed == processed))
        {
            return RunStates.Failed;
        }

        if (c.Proposed > 0)
        {
            return RunStates.AwaitingDeletionConfirmation;
        }

        var changed = processed - c.Unchanged;
        return c.Malware > 0 || (changed > 0 && (decimal)c.Failed / changed > degradedRatio) ? RunStates.Degraded : RunStates.Succeeded;
    }

    internal sealed record Counters(long Added, long Updated, long Unchanged, long Deleted, long Failed, long Malware, decimal Cost, bool SyncFailed, long Proposed);

    private sealed record IdleRun(Guid Id, Guid SourceId, string Classification, string Kind, string Trigger, string? Phase, string SourceType, Guid DepartmentId,
        bool OpenFrontier, string? CrawlFailure, string? CrawlDegradation);

    [LoggerMessage(LogLevel.Information, "Run {RunId} finished {State}: {Added} added, {Updated} updated, {Unchanged} unchanged, {Deleted} deleted, {Failed} failed")]
    private static partial void LogFinished(ILogger logger, Guid runId, string state, long added, long updated, long unchanged, long deleted, long failed);

    [LoggerMessage(LogLevel.Warning, "The run finalizer pass failed; it runs again at the next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
