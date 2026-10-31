using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Engine;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// The nightly content maintenance at 02:00 Europe/Stockholm on the instance with <c>Worker:Maintenance=true</c>
/// (repo-and-deployment §3, data-model §4.4, §8): run-item partitions, then terminal jobs older than 7 days in batches of
/// 1,000. Rows still in <c>RunItems_default</c> afterwards mean maintenance lagged: a warning and the
/// <c>rag.partitions.default_rows</c> gauge. Then the engine's sweeps: chunk sets never swapped or past the rollback window
/// (<see cref="IndexWriter.SweepOrphansAsync"/>) and the blob-tombstone purge (<see cref="BlobPurgeSweep"/>). Content
/// retention of cache rows and the nightly drift sweep are not built yet.
/// </summary>
public sealed partial class MaintenanceService(
    ContentMaintenance maintenance,
    IndexWriter index,
    BlobPurgeSweep purge,
    JobMetrics metrics,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    public static readonly TimeOnly RunAt = new(2, 0);
    public static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Maintenance)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            await Task.Delay(NextRunAt(now) - now, time, stoppingToken);
            await RunAsync(stoppingToken);
        }
    }

    /// <summary>One maintenance run; each step logs its own failure, so one failing step does not skip the next.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var partitions = await maintenance.MaintainPartitionsAsync(cancellationToken);
            metrics.DefaultRows(partitions.DefaultRows);
            if (partitions.DefaultRows > 0)
            {
                LogDefaultRows(logger, partitions.DefaultRows);
            }

            LogPartitions(logger, partitions.Created, partitions.Dropped);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogStepFailed(logger, ex, "partitions");
        }

        try
        {
            var deleted = await maintenance.DeleteTerminalJobsAsync(ContentMaintenance.TerminalJobRetention, cancellationToken);
            metrics.RetentionDeleted("Jobs", deleted);
            LogJobsDeleted(logger, deleted);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogStepFailed(logger, ex, "terminal jobs");
        }

        try
        {
            LogOrphansSwept(logger, await index.SweepOrphansAsync(cancellationToken));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogStepFailed(logger, ex, "orphan chunk sets");
        }

        try
        {
            var result = await purge.RunAsync(cancellationToken);
            LogPurged(logger, result.Purged, result.Kept);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogStepFailed(logger, ex, "blob purge");
        }
    }

    /// <summary>
    /// The next 02:00 in Stockholm after <paramref name="now"/>. On the spring DST day 02:00 does not exist and the run is at
    /// 03:00; on the autumn day it exists twice and the run is once, at the standard-time 02:00.
    /// </summary>
    public static DateTimeOffset NextRunAt(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Stockholm);
        for (var day = 0; day <= 2; day++)
        {
            var candidate = DateOnly.FromDateTime(local.DateTime).AddDays(day).ToDateTime(RunAt);
            if (Stockholm.IsInvalidTime(candidate))
            {
                candidate = candidate.AddHours(1);
            }

            var at = new DateTimeOffset(candidate, Stockholm.GetUtcOffset(candidate));
            if (at > now)
            {
                return at.ToUniversalTime();
            }
        }

        return now.AddDays(1);
    }

    [LoggerMessage(LogLevel.Information, "Run-item partitions: {Created} created, {Dropped} dropped")]
    private static partial void LogPartitions(ILogger logger, int created, int dropped);

    [LoggerMessage(LogLevel.Warning, "{Rows} run items are in RunItems_default: partition maintenance lagged")]
    private static partial void LogDefaultRows(ILogger logger, long rows);

    [LoggerMessage(LogLevel.Information, "Deleted {Count} terminal jobs older than 7 days")]
    private static partial void LogJobsDeleted(ILogger logger, long count);

    [LoggerMessage(LogLevel.Information, "Removed {Count} chunk sets never swapped or past the rollback window")]
    private static partial void LogOrphansSwept(ILogger logger, long count);

    [LoggerMessage(LogLevel.Information, "Blob purge: {Purged} blobs purged, {Kept} tombstones dropped for keys referenced again")]
    private static partial void LogPurged(ILogger logger, long purged, long kept);

    [LoggerMessage(LogLevel.Error, "Maintenance step {Step} failed; it runs again tomorrow")]
    private static partial void LogStepFailed(ILogger logger, Exception exception, string step);
}
