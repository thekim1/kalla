using Cronos;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Retrieval;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// When the index-health check (knowledge design §6.4) is enqueued: a <c>check-hnsw-index</c> command per embedding
/// profile, dedup <see cref="JobDedupKeys.HnswCheck"/>, attributed to the department and level of the profile's largest
/// collection (or the run's collection). Only a profile with more than <c>exactScanMaxChunks</c> live chunks in total
/// can have an HNSW-searched scope (one large collection or several smaller ones searched together), so no other profile
/// is checked; <see cref="HnswIndexHealth"/> decides which scopes are HNSW-searched. Chunk counts are the live chunk sets'
/// <c>ChunkCount</c>.
/// </summary>
public static class HnswCheckTriggers
{
    /// <summary>A finished run triggers a check when it made more than this share of the profile's live chunks live.</summary>
    public const int RunPercent = 10;

    private const string ExactScanMaxChunks = """
        coalesce((SELECT ("Retrieval" ->> 'exactScanMaxChunks')::bigint FROM config."Settings" ORDER BY "Id" LIMIT 1), $1)
        """;

    private const string NightlySql = $"""
        WITH live AS (
            SELECT cs."EmbeddingProfileId" AS profile, cs."CollectionId" AS collection, sum(cs."ChunkCount") AS chunks
            FROM content."ChunkSets" cs
            WHERE cs."State" = 'live'
            GROUP BY 1, 2),
        total AS (
            SELECT profile, sum(chunks) AS chunks FROM live GROUP BY 1)
        SELECT DISTINCT ON (l.profile) l.profile, c."DepartmentId", c."Classification"
        FROM live l JOIN total t ON t.profile = l.profile JOIN config."Collections" c ON c."Id" = l.collection
        WHERE t.chunks > {ExactScanMaxChunks}
          AND NOT EXISTS (SELECT 1 FROM content."Jobs" j
                          WHERE j."Class" = '{JobClasses.Command}' AND j."DedupKey" = 'hnsw-check:' || l.profile::text AND j."EnqueuedAt" >= $2)
        ORDER BY l.profile, l.chunks DESC, l.collection
        """;

    private static readonly string AfterRunSql = $"""
        WITH made AS (
            SELECT cs."EmbeddingProfileId" AS profile, sum(cs."ChunkCount") AS chunks
            FROM content."Documents" d JOIN content."ChunkSets" cs ON cs."Id" = d."LiveChunkSetId"
            WHERE d."SourceId" = $2 AND d."DeletedAt" IS NULL AND cs."State" = 'live'
              AND cs."LiveAt" >= (SELECT r."StartedAt" FROM content."Runs" r WHERE r."Id" = $3)
            GROUP BY 1)
        SELECT m.profile
        FROM made m
        WHERE 100 * m.chunks > {RunPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)} * (SELECT sum(t."ChunkCount") FROM content."ChunkSets" t
                                               WHERE t."EmbeddingProfileId" = m.profile AND t."State" = 'live')
          AND (SELECT sum(t."ChunkCount") FROM content."ChunkSets" t
               WHERE t."EmbeddingProfileId" = m.profile AND t."State" = 'live') > {ExactScanMaxChunks}
        ORDER BY m.profile
        """;

    /// <summary>The nightly checks not yet enqueued since <paramref name="since"/>, under the caller's transaction.</summary>
    public static async Task<IReadOnlyList<JobRequest>> NightlyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var jobs = new List<JobRequest>();
        await using var command = new NpgsqlCommand(NightlySql, connection, transaction)
        {
            Parameters = { Db.P((long)RetrievalPlatformSettings.Default.ExactScanMaxChunks), Db.P(since.ToUniversalTime()) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(Job(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), HnswCheckTrigger.Nightly));
        }

        return jobs;
    }

    /// <summary>
    /// The checks a finished run of <paramref name="sourceId"/> triggers: one per profile of which the run made more than
    /// <see cref="RunPercent"/> % of the live chunks live (sets swapped in since the run started), under the caller's
    /// transaction.
    /// </summary>
    public static async Task<IReadOnlyList<JobRequest>> AfterRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, Guid sourceId,
        Guid departmentId, string classification, CancellationToken cancellationToken)
    {
        var jobs = new List<JobRequest>();
        await using var command = new NpgsqlCommand(AfterRunSql, connection, transaction)
        {
            Parameters = { Db.P((long)RetrievalPlatformSettings.Default.ExactScanMaxChunks), Db.P(sourceId), Db.P(runId) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(Job(reader.GetGuid(0), departmentId, classification, HnswCheckTrigger.Sync));
        }

        return jobs;
    }

    private static JobRequest Job(Guid profileId, Guid departmentId, string classification, HnswCheckTrigger trigger) =>
        new(JobClasses.Command, departmentId, classification,
            ContractJson.Serialize(new CheckHnswIndexCommand { EmbeddingProfileId = profileId, Trigger = trigger }))
        {
            DedupKey = JobDedupKeys.HnswCheck(profileId),
        };
}

/// <summary>
/// The nightly index-health check (knowledge design §6.4): every <c>Worker:SchedulerSeconds</c> the instance holding the
/// advisory lock takes the latest occurrence of <see cref="Cron"/> (Europe/Stockholm) that has passed and enqueues the
/// <see cref="HnswCheckTriggers.NightlyAsync"/> checks not enqueued since it. Missed occurrences after an outage collapse
/// into one check.
/// </summary>
public sealed partial class HnswCheckScheduler(JobDatabase database, IJobQueue queue, IOptions<WorkerOptions> options, TimeProvider time,
    ILogger<HnswCheckScheduler> logger) : BackgroundService
{
    /// <summary>03:00 every night: after maintenance (02:00), before the nightly evaluation runs (03:30).</summary>
    public const string Cron = "0 3 * * *";

    private const long LockKey = 0x5241_4731_0006;

    private static readonly CronExpression Expression = CronExpression.Parse(Cron);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SchedulerSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>One pass under the advisory lock; returns the checks enqueued.</summary>
    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        if (LatestOccurrence(time.GetUtcNow()) is not { } night)
        {
            return 0;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } })
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return 0;
            }
        }

        var jobs = await HnswCheckTriggers.NightlyAsync(connection, transaction, night, cancellationToken);
        var enqueued = jobs.Count == 0 ? new HashSet<Guid>() : await queue.EnqueueAsync(jobs, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (enqueued.Count > 0)
        {
            LogEnqueued(logger, enqueued.Count, night);
        }

        return enqueued.Count;
    }

    /// <summary>The latest occurrence of <see cref="Cron"/> at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset? LatestOccurrence(DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        var cursor = now - TimeSpan.FromDays(2);
        while (Expression.GetNextOccurrence(cursor, StockholmTime.Zone) is { } next && next <= now)
        {
            latest = next;
            cursor = next;
        }

        return latest;
    }

    [LoggerMessage(LogLevel.Information, "Enqueued {Count} HNSW index checks for {ScheduledFor}")]
    private static partial void LogEnqueued(ILogger logger, int count, DateTimeOffset scheduledFor);

    [LoggerMessage(LogLevel.Warning, "The HNSW check scheduler pass failed; it runs again at the next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
