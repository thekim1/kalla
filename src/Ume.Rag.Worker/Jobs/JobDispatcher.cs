using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.ServiceDefaults;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// Fills this instance's job slots (engine design §9.3, data-model §4.2). One loop per class it runs, each with
/// <c>Worker:Concurrency:&lt;class&gt;</c> slots; a free slot probes the departments with ready jobs in ascending order of
/// the jobs this instance already runs for them (ties: higher priority, then older), one dequeue statement per probe, the
/// first hit wins. Loops wake on a local signal or every <c>Worker:PollSeconds</c>.
/// </summary>
/// <remarks>
/// Each job runs in its own scope under the <c>ConfigVersion</c> log scope. Capacity refusals (an unconfirmed config, a
/// transient result with <c>CountsAttempt = false</c>) requeue without an attempt. A clean stop stops dequeuing, gives
/// running jobs <c>Worker:StopGraceSeconds</c>, then cancels the rest and releases them without an attempt. A process that
/// dies writes nothing: the reaper reclaims its jobs.
/// </remarks>
public sealed partial class JobDispatcher(
    IJobQueue queue,
    RunningJobs running,
    WorkerIdentity identity,
    JobWakeUps wakeUps,
    IEnumerable<IJobHandler> handlers,
    IServiceScopeFactory scopes,
    ConfigVersionState config,
    JobMetrics metrics,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<JobDispatcher> logger) : BackgroundService
{
    /// <summary>Requeue reason of jobs released by a clean stop.</summary>
    public const string ShutdownReason = "shutdown";

    /// <summary>Requeue reason and <c>LastError</c> of a job refused for an unconfirmed config (engine design §7.3).</summary>
    public const string ConfigUnconfirmedReason = ConfigVersionState.UnconfirmedCode;

    /// <summary><c>LastError</c> of an unhandled exception in a handler (type name in the log only).</summary>
    public const string ExceptionCode = "job.exception";

    private static readonly TimeSpan DefaultCapacityDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDatabaseBackoff = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _dispatching = new();
    private readonly CancellationTokenSource _cancelJobs = new();
    private readonly ConcurrentDictionary<Guid, Task> _jobs = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var byClass = handlers.ToDictionary(h => h.JobClass, StringComparer.Ordinal);
        var lanes = new List<Lane>();
        foreach (var jobClass in options.Value.ClassList())
        {
            if (byClass.TryGetValue(jobClass, out var handler))
            {
                lanes.Add(new Lane(JobClassPolicy.For(jobClass), handler, options.Value.ConcurrencyOf(jobClass)));
            }
            else
            {
                LogClassWithoutHandler(logger, jobClass);
            }
        }

        await identity.Registered.WaitAsync(stoppingToken);
        using var dispatching = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _dispatching.Token);
        LogStarted(logger, string.Join(", ", lanes.Select(l => $"{l.Policy.Class}×{l.Concurrency}")));
        await Task.WhenAll(lanes.Select(lane => RunLaneAsync(lane, dispatching.Token, stoppingToken)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _dispatching.CancelAsync();
        var all = Task.WhenAll(_jobs.Values);
        if (!all.IsCompleted)
        {
            LogDraining(logger, _jobs.Count, options.Value.StopGraceSeconds);
            try
            {
                await all.WaitAsync(TimeSpan.FromSeconds(options.Value.StopGraceSeconds), time, cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Out of grace: cancel what is left and release it without an attempt.
                await _cancelJobs.CancelAsync();
                await Task.WhenAll(_jobs.Values).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        _dispatching.Dispose();
        _cancelJobs.Dispose();
    }

    private async Task RunLaneAsync(Lane lane, CancellationToken dispatching, CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollSeconds);
        var failures = 0;
        try
        {
            while (true)
            {
                await lane.Slots.WaitAsync(dispatching);
                LeasedJob? job;
                try
                {
                    job = await TryDequeueAsync(lane.Policy, dispatching);
                    failures = 0;
                }
                catch (Exception ex) when (!dispatching.IsCancellationRequested)
                {
                    // Postgres unavailable: back off 1 s → 30 s (engine design §16).
                    lane.Slots.Release();
                    var delay = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1 << Math.Min(failures++, 5)).Ticks, MaxDatabaseBackoff.Ticks));
                    LogDequeueFailed(logger, ex, lane.Policy.Class, delay.TotalSeconds);
                    await Task.Delay(delay, time, dispatching);
                    continue;
                }

                if (job is null)
                {
                    lane.Slots.Release();
                    await wakeUps.WaitAsync(lane.Policy.Class, poll, dispatching);
                    continue;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cancelJobs.Token);
                var entry = running.Add(job, lane.Policy.Lease, cancellation);
                var task = RunJobAsync(entry, lane, stoppingToken);
                _jobs[job.Id] = task;
                _ = task.ContinueWith(t => _jobs.TryRemove(KeyValuePair.Create(entry.Job.Id, t)), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (dispatching.IsCancellationRequested)
        {
            // Stopping: no more dequeues.
        }
    }

    /// <summary>Per-department fairness (data-model §4.2): fewest running here first, then priority, then age.</summary>
    private async Task<LeasedJob?> TryDequeueAsync(JobClassPolicy policy, CancellationToken cancellationToken)
    {
        var ready = await queue.ReadyDepartmentsAsync(policy.Class, cancellationToken);
        foreach (var department in ready
                     .OrderBy(d => running.CountFor(policy.Class, d.DepartmentId))
                     .ThenByDescending(d => d.MaxPriority)
                     .ThenBy(d => d.OldestAvailableAt)
                     .ThenBy(d => d.DepartmentId))
        {
            // Not cancellable: a dequeue cancelled after its commit would strand a leased job until the reaper.
            var job = await queue.DequeueAsync(new DequeueRequest(policy.Class, department.DepartmentId, identity.InstanceId, policy.Lease), CancellationToken.None);
            if (job is not null)
            {
                return job;
            }
        }

        return null;
    }

    private async Task RunJobAsync(RunningJob entry, Lane lane, CancellationToken stoppingToken)
    {
        await Task.Yield();
        var job = entry.Job;
        var started = time.GetTimestamp();
        var outcome = "unrecorded";
        try
        {
            using (ConfigVersionLogScope.Begin(logger, config))
            using (logger.BeginScope(JobScope(job)))
            {
                var result = await ExecuteAsync(lane.Handler, entry);
                outcome = result is null ? await CancelledAsync(entry, stoppingToken) : await ApplyAsync(job, result);
            }
        }
        catch (Exception ex)
        {
            // The outcome could not be written: the lease runs out and the reaper returns the job.
            LogOutcomeFailed(logger, ex, job.Id, job.Class);
        }
        finally
        {
            metrics.Duration(job.Class, outcome, time.GetElapsedTime(started));
            running.Remove(job.Id);
            entry.Dispose();
            lane.Slots.Release();
        }
    }

    /// <summary>Runs the handler; null when it was cancelled (lease lost or stopping).</summary>
    private async Task<JobResult?> ExecuteAsync(IJobHandler handler, RunningJob entry)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await handler.ExecuteAsync(new JobContext(entry.Job, scope.ServiceProvider), entry.Token);
        }
        catch (Exception) when (entry.Token.IsCancellationRequested)
        {
            // Cancelled, or failing because it was: the caller decides from why the token fired.
            return null;
        }
        catch (ApiFaultException ex) when (IsConfigUnconfirmed(ex))
        {
            // EnsureConfirmed outside the grace window: a capacity refusal, 2 s then 5 s (engine design §7.3, §15).
            var delay = entry.Job.LastError == ConfigUnconfirmedReason ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
            return new JobResult.TransientError(ConfigUnconfirmedReason, delay, CountsAttempt: false);
        }
        catch (Exception ex)
        {
            // Types and throw site only: an exception message may carry content (engine design §7.3, repo-and-deployment §9).
            LogJobException(logger, entry.Job.Id, entry.Job.Class, ExceptionSummary.Types(ex), ExceptionSummary.Site(ex));
            return new JobResult.TransientError(ExceptionCode);
        }
    }

    private async Task<string> ApplyAsync(LeasedJob job, JobResult result)
    {
        var owner = identity.InstanceId;
        switch (result)
        {
            case JobResult.Success success:
                if (await queue.CompleteAsync(job.Id, owner, success.ResultJson, null, CancellationToken.None))
                {
                    LogSucceeded(logger, job.Id, job.Class);
                    return JobStatesOutcome.Succeeded;
                }

                return LeaseLost(job);

            case JobResult.Committed:
                LogSucceeded(logger, job.Id, job.Class);
                return JobStatesOutcome.Succeeded;

            case JobResult.TransientError { CountsAttempt: false } refusal:
                if (!await queue.ReleaseAsync(job.Id, owner, refusal.Code, refusal.RetryAfter ?? DefaultCapacityDelay, CancellationToken.None))
                {
                    return LeaseLost(job);
                }

                metrics.Requeued(job.Class, refusal.Code);
                LogRequeued(logger, job.Id, job.Class, refusal.Code);
                return JobStatesOutcome.Requeued;

            case JobResult.TransientError transient:
                return await FailAsync(job, transient.Code, fatal: false, transient.RetryAfter);

            case JobResult.FatalError fatal:
                return await FailAsync(job, fatal.Code, fatal: true, retryAfter: null);

            default:
                throw new InvalidOperationException($"Unknown job result {result.GetType().Name}.");
        }
    }

    private async Task<string> FailAsync(LeasedJob job, string code, bool fatal, TimeSpan? retryAfter)
    {
        var state = await queue.FailAsync(job.Id, identity.InstanceId, code, fatal, retryAfter, CancellationToken.None);
        if (state is null)
        {
            return LeaseLost(job);
        }

        if (state == Domain.Entities.JobStates.Dead)
        {
            metrics.Dead(job.Class);
        }

        LogFailed(logger, job.Id, job.Class, code, state);
        return state;
    }

    private async Task<string> CancelledAsync(RunningJob entry, CancellationToken stoppingToken)
    {
        var job = entry.Job;
        if (entry.LeaseLost)
        {
            return JobStatesOutcome.LeaseLost;
        }

        if (!_cancelJobs.IsCancellationRequested && stoppingToken.IsCancellationRequested)
        {
            // Torn down without a clean stop: behave as a crash and leave the job to the reaper.
            return JobStatesOutcome.Abandoned;
        }

        if (await queue.ReleaseAsync(job.Id, identity.InstanceId, ShutdownReason, TimeSpan.Zero, CancellationToken.None))
        {
            metrics.Requeued(job.Class, ShutdownReason);
            LogRequeued(logger, job.Id, job.Class, ShutdownReason);
            return JobStatesOutcome.Requeued;
        }

        return LeaseLost(job);
    }

    private string LeaseLost(LeasedJob job)
    {
        metrics.LeaseLost(job.Class);
        LogLeaseLost(logger, job.Id, job.Class);
        return JobStatesOutcome.LeaseLost;
    }

    private static bool IsConfigUnconfirmed(ApiFaultException ex) =>
        ex.Status == StatusCodes.Status503ServiceUnavailable &&
        ex.Extensions?.TryGetValue("code", out var code) == true && code as string == ConfigVersionState.UnconfirmedCode;

    private static Dictionary<string, object> JobScope(LeasedJob job)
    {
        var scope = new Dictionary<string, object>
        {
            ["JobId"] = job.Id,
            ["JobClass"] = job.Class,
            ["Classification"] = job.Classification,
        };
        if (job.RunId is { } runId)
        {
            scope["RunId"] = runId;
        }

        return scope;
    }

    private sealed class Lane(JobClassPolicy policy, IJobHandler handler, int concurrency)
    {
        public JobClassPolicy Policy => policy;
        public IJobHandler Handler => handler;
        public int Concurrency => concurrency;

        /// <summary>Free slots. Never disposed: running jobs release theirs after the loop has ended.</summary>
        public SemaphoreSlim Slots { get; } = new(concurrency, concurrency);
    }

    /// <summary>The <c>outcome</c> dimension of <c>rag.jobs.duration</c>: a job state, or what happened instead.</summary>
    private static class JobStatesOutcome
    {
        public const string Succeeded = "succeeded";
        public const string Requeued = "requeued";
        public const string LeaseLost = "lease_lost";
        public const string Abandoned = "abandoned";
    }

    [LoggerMessage(LogLevel.Information, "Dispatcher started: {Lanes}")]
    private static partial void LogStarted(ILogger logger, string lanes);

    [LoggerMessage(LogLevel.Information, "Job class {JobClass} has no handler in this build and is not dispatched")]
    private static partial void LogClassWithoutHandler(ILogger logger, string jobClass);

    [LoggerMessage(LogLevel.Warning, "Dequeue of {JobClass} failed; retrying in {DelaySeconds} s")]
    private static partial void LogDequeueFailed(ILogger logger, Exception exception, string jobClass, double delaySeconds);

    [LoggerMessage(LogLevel.Information, "Stopping: waiting up to {GraceSeconds} s for {Count} running jobs")]
    private static partial void LogDraining(ILogger logger, int count, int graceSeconds);

    [LoggerMessage(LogLevel.Error, "Job {JobId} ({JobClass}) threw {ExceptionType} at {ThrowSite}")]
    private static partial void LogJobException(ILogger logger, Guid jobId, string jobClass, string exceptionType, string throwSite);

    [LoggerMessage(LogLevel.Debug, "Job {JobId} ({JobClass}) succeeded")]
    private static partial void LogSucceeded(ILogger logger, Guid jobId, string jobClass);

    [LoggerMessage(LogLevel.Information, "Job {JobId} ({JobClass}) requeued without an attempt: {Reason}")]
    private static partial void LogRequeued(ILogger logger, Guid jobId, string jobClass, string reason);

    [LoggerMessage(LogLevel.Warning, "Job {JobId} ({JobClass}) failed with {Code}; now {State}")]
    private static partial void LogFailed(ILogger logger, Guid jobId, string jobClass, string code, string state);

    [LoggerMessage(LogLevel.Warning, "Job {JobId} ({JobClass}) lost its lease before its outcome was written")]
    private static partial void LogLeaseLost(ILogger logger, Guid jobId, string jobClass);

    [LoggerMessage(LogLevel.Error, "The outcome of job {JobId} ({JobClass}) could not be written; the reaper returns it when its lease ends")]
    private static partial void LogOutcomeFailed(ILogger logger, Exception exception, Guid jobId, string jobClass);
}
