using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Engine;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// Registers this instance in <c>content."WorkerInstances"</c> with its stage manifest and support rows
/// (<see cref="StageManifestRegistrar"/>; a conflicting manifest stops the worker), then every <c>Worker:HeartbeatSeconds</c> (10 s) renews its
/// heartbeat and, in one statement, the leases of its running jobs (engine design §5.2, §9.4). A job whose lease the
/// database no longer holds for this instance is cancelled; its commit would fail anyway. A clean stop removes the row
/// after the dispatcher has drained (hosted services stop in reverse order).
/// </summary>
public sealed partial class WorkerHeartbeat(
    WorkerInstanceStore instances,
    StageManifestRegistrar manifest,
    IJobQueue queue,
    RunningJobs running,
    WorkerIdentity identity,
    JobMetrics metrics,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<WorkerHeartbeat> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var classes = options.Value.ClassList();
        await RegisterAsync(classes, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.HeartbeatSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await instances.HeartbeatAsync(identity.InstanceId, identity.BuildVersion, classes, stoppingToken);
                var leases = running.Leases();
                var renewed = await queue.HeartbeatAsync(identity.InstanceId, leases, stoppingToken);
                foreach (var lost in running.LoseLeases(leases, renewed))
                {
                    metrics.LeaseLost(lost.Class);
                    LogLeaseLost(logger, lost.Id, lost.Class);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The next tick tries again; the reaper reclaims this instance's jobs only after InstanceTimeoutSeconds.
                LogHeartbeatFailed(logger, ex, identity.InstanceId);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!identity.Registered.IsCompleted)
        {
            return;
        }

        try
        {
            await instances.DeleteAsync(identity.InstanceId, cancellationToken);
            LogStopped(logger, identity.InstanceId);
        }
        catch (Exception ex)
        {
            // The reaper removes the row after 5 silent minutes.
            LogDeleteFailed(logger, ex, identity.InstanceId);
        }
    }

    private async Task RegisterAsync(IReadOnlyList<string> classes, CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await instances.HeartbeatAsync(identity.InstanceId, identity.BuildVersion, classes, stoppingToken);
                await manifest.RegisterAsync(identity.InstanceId, stoppingToken);
                identity.MarkRegistered();
                LogRegistered(logger, identity.InstanceId, identity.BuildVersion, string.Join(',', classes));
                return;
            }
            catch (Exception ex) when (ex is not StageManifestConflictException && !stoppingToken.IsCancellationRequested)
            {
                LogRegistrationFailed(logger, ex, delay.TotalSeconds);
                await Task.Delay(delay, time, stoppingToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Worker instance {InstanceId} registered (build {BuildVersion}, classes {Classes})")]
    private static partial void LogRegistered(ILogger logger, Guid instanceId, string buildVersion, string classes);

    [LoggerMessage(LogLevel.Warning, "Worker registration failed; retrying in {DelaySeconds} s")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(LogLevel.Warning, "Heartbeat of worker instance {InstanceId} failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, Guid instanceId);

    [LoggerMessage(LogLevel.Warning, "Job {JobId} ({JobClass}) lost its lease; it is cancelled without committing")]
    private static partial void LogLeaseLost(ILogger logger, Guid jobId, string jobClass);

    [LoggerMessage(LogLevel.Information, "Worker instance {InstanceId} stopped and unregistered")]
    private static partial void LogStopped(ILogger logger, Guid instanceId);

    [LoggerMessage(LogLevel.Warning, "Worker instance {InstanceId} could not unregister; the reaper removes it later")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid instanceId);
}
