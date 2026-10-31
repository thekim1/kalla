using System.Security.Cryptography;
using Cronos;
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

/// <summary>A source as the scheduler sees it.</summary>
public sealed record ScheduledSource(Guid SourceId, string Type, string Classification, Guid DepartmentId, string? Schedule, DateTimeOffset CreatedAt,
    DateTimeOffset? LastScheduledFor, bool Running);

/// <summary>
/// The scheduler of engine design §9.6: every <c>Worker:SchedulerSeconds</c> (60 s) the instance holding the advisory lock
/// evaluates each active source's cron (<c>Sources.Schedule</c>, else <c>Settings.DefaultSchedules</c> by type; uploads
/// never) in Europe/Stockholm with Cronos. The next occurrence after the last scheduled run (else after the source's
/// <c>CreatedAt</c>, never its <c>UpdatedAt</c>, which every edit moves) plus a deterministic per-source jitter
/// (<c>hash(sourceId) mod Settings.ScheduleJitterMinutes</c>) is due once it has passed and no run of the source is running; it enqueues one <c>sync</c> with <c>ScheduledFor</c> = the occurrence
/// (dedup <c>sync:{sourceId}</c>), so missed occurrences after an outage collapse into one run. DST gaps run once at the
/// next valid time, overlaps once (Cronos). In the same pass an idle source whose settings changed in scope or identity
/// since its newest <c>sync</c> run gets a <c>config-change</c> sync now (connectors design §12.4, <see cref="SettingsEffects"/>).
/// </summary>
public sealed partial class SyncScheduler(
    JobDatabase database,
    IJobQueue queue,
    EngineSettings settings,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    public static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private const long LockKey = 0x5241_4731_0002;

    private const string SourcesSql = """
        SELECT s."Id", s."Type", s."Classification", c."DepartmentId", s."Schedule", s."CreatedAt",
               (SELECT max(r."ScheduledFor") FROM content."Runs" r WHERE r."SourceId" = s."Id" AND r."Trigger" = 'schedule'),
               EXISTS (SELECT 1 FROM content."Runs" r WHERE r."SourceId" = s."Id" AND r."State" = 'running')
        FROM config."Sources" s JOIN config."Collections" c ON c."Id" = s."CollectionId"
        WHERE s."State" = 'active' AND s."DeletedAt" IS NULL AND c."DeletedAt" IS NULL
        """;

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

    /// <summary>One pass under the advisory lock; returns the syncs enqueued.</summary>
    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } })
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return 0;
            }
        }

        var sources = new List<ScheduledSource>();
        await using (var command = new NpgsqlCommand(SourcesSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sources.Add(new ScheduledSource(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), Db.String(reader, 4),
                    reader.GetFieldValue<DateTimeOffset>(5), Db.Nullable<DateTimeOffset>(reader, 6), reader.GetBoolean(7)));
            }
        }

        var jobs = new List<JobRequest>();
        var scheduled = new HashSet<Guid>();
        foreach (var source in sources)
        {
            if (Due(source, values, now) is not { } due)
            {
                continue;
            }

            var payload = new SyncPayload { SourceId = source.SourceId, Kind = SyncKind.Sync, Trigger = RunTrigger.Schedule, ScheduledFor = due.Occurrence };
            jobs.Add(new JobRequest(JobClasses.Sync, source.DepartmentId, source.Classification, ContractJson.Serialize(payload))
            {
                DedupKey = JobDedupKeys.Sync(source.SourceId),
            });
            scheduled.Add(source.SourceId);
            LogDue(logger, source.SourceId, due.Occurrence, (now - due.At).TotalSeconds);
        }

        jobs.AddRange(await SettingsEffects.ChangedAsync(transaction, scheduled, logger, cancellationToken));
        var enqueued = jobs.Count == 0 ? 0 : (await queue.EnqueueAsync(jobs, transaction, cancellationToken)).Count;
        await transaction.CommitAsync(cancellationToken);
        return enqueued;
    }

    /// <summary>The occurrence due now and when it became due (occurrence + jitter), or null.</summary>
    public static (DateTimeOffset Occurrence, DateTimeOffset At)? Due(ScheduledSource source, EngineSettingsValues values, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(values);
        var cron = source.Schedule ?? (source.Type == SourceTypes.Upload ? null : values.DefaultSchedules.GetValueOrDefault(source.Type));
        if (source.Running || cron is null || !CronExpression.TryParse(cron, CronFormat.Standard, out var expression))
        {
            return null;
        }

        if (expression.GetNextOccurrence(source.LastScheduledFor ?? source.CreatedAt, Stockholm) is not { } next)
        {
            return null;
        }

        // Missed occurrences collapse into the latest one that has passed.
        while (expression.GetNextOccurrence(next, Stockholm) is { } later && later + Jitter(source.SourceId, values.ScheduleJitterMinutes) <= now)
        {
            next = later;
        }

        var at = next + Jitter(source.SourceId, values.ScheduleJitterMinutes);
        return at <= now ? (next, at) : null;
    }

    /// <summary>Deterministic per source: the first eight bytes of SHA-256 of the id, modulo the jitter minutes.</summary>
    public static TimeSpan Jitter(Guid sourceId, int jitterMinutes)
    {
        if (jitterMinutes <= 0)
        {
            return TimeSpan.Zero;
        }

        var hash = SHA256.HashData(sourceId.ToByteArray());
        return TimeSpan.FromMinutes((double)(BitConverter.ToUInt64(hash, 0) % (ulong)jitterMinutes));
    }

    [LoggerMessage(LogLevel.Information, "Source {SourceId}: scheduled sync for {ScheduledFor} enqueued ({LagSeconds:F0} s after due)")]
    private static partial void LogDue(ILogger logger, Guid sourceId, DateTimeOffset scheduledFor, double lagSeconds);

    [LoggerMessage(LogLevel.Warning, "The scheduler pass failed; it runs again at the next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
