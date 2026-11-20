using Microsoft.Extensions.Options;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Jobs;

namespace Kalla.Worker.Jobs;

/// <summary>
/// Runs the <see cref="JobReaper"/> every <c>Worker:ReaperSeconds</c> (30 s) on every instance; one pass at a time across
/// instances (engine design §9.4, performance pass §3.5). Wakes the local dispatcher for the classes it requeued.
/// </summary>
public sealed partial class JobReaperService(
    JobReaper reaper,
    JobWakeUps wakeUps,
    JobMetrics metrics,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<JobReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var instanceTimeout = TimeSpan.FromSeconds(options.Value.InstanceTimeoutSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ReaperSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var result = await reaper.ReapAsync(instanceTimeout, stoppingToken);
                foreach (var job in result.Reclaimed)
                {
                    metrics.Reclaimed(job.Class);
                    if (job.State == JobStates.Dead)
                    {
                        metrics.Dead(job.Class);
                        LogPoisonSuspect(logger, job.Id, job.Class, job.Reclaims);
                    }
                    else
                    {
                        LogReclaimed(logger, job.Id, job.Class, job.Reclaims);
                        wakeUps.Signal(job.Class);
                    }
                }

                if (result.InstancesRemoved > 0)
                {
                    LogInstancesRemoved(logger, result.InstancesRemoved);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogReapFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, "Job {JobId} ({JobClass}) reclaimed without an attempt (reclaim {Reclaims})")]
    private static partial void LogReclaimed(ILogger logger, Guid jobId, string jobClass, short reclaims);

    [LoggerMessage(LogLevel.Error, "Job {JobId} ({JobClass}) dead-lettered as a poison suspect after {Reclaims} reclaims")]
    private static partial void LogPoisonSuspect(ILogger logger, Guid jobId, string jobClass, short reclaims);

    [LoggerMessage(LogLevel.Information, "Removed {Count} worker instances silent for 5 minutes")]
    private static partial void LogInstancesRemoved(ILogger logger, int count);

    [LoggerMessage(LogLevel.Warning, "Reaper pass failed; retrying on the next tick")]
    private static partial void LogReapFailed(ILogger logger, Exception exception);
}
