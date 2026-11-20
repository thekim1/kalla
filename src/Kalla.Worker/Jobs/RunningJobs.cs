using System.Collections.Concurrent;
using System.Reflection;
using Kalla.Domain.Ports;

namespace Kalla.Worker.Jobs;

/// <summary>This process's registration in <c>content."WorkerInstances"</c>: a new v7 id per start (engine design §5.2).</summary>
public sealed class WorkerIdentity
{
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid InstanceId { get; } = Guid.CreateVersion7();

    public string BuildVersion { get; } =
        typeof(WorkerIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Completes once the instance row exists; the dispatcher dequeues nothing before.</summary>
    public Task Registered => _registered.Task;

    internal void MarkRegistered() => _registered.TrySetResult();
}

/// <summary>A job this instance runs: its lease, and the token that stops it when the lease is lost or the worker stops.</summary>
public sealed class RunningJob(LeasedJob job, TimeSpan lease, CancellationTokenSource cancellation) : IDisposable
{
    private volatile bool _leaseLost;

    public LeasedJob Job => job;
    public TimeSpan Lease => lease;
    public CancellationToken Token => cancellation.Token;
    public bool LeaseLost => _leaseLost;

    internal void LoseLease()
    {
        _leaseLost = true;
        cancellation.Cancel();
    }

    public void Dispose() => cancellation.Dispose();
}

/// <summary>
/// The jobs this instance runs: the lease heartbeat renews them, the dispatcher counts them per class and department for
/// fairness (data-model §4.2, in memory per instance).
/// </summary>
public sealed class RunningJobs
{
    private readonly ConcurrentDictionary<Guid, RunningJob> _jobs = new();

    public int Count => _jobs.Count;

    public RunningJob Add(LeasedJob job, TimeSpan lease, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(job);
        var running = new RunningJob(job, lease, cancellation);
        _jobs[job.Id] = running;
        return running;
    }

    public void Remove(Guid jobId) => _jobs.TryRemove(jobId, out _);

    /// <summary>Running jobs of <paramref name="jobClass"/> for <paramref name="departmentId"/>.</summary>
    public int CountFor(string jobClass, Guid departmentId) =>
        _jobs.Values.Count(r => r.Job.DepartmentId == departmentId && string.Equals(r.Job.Class, jobClass, StringComparison.Ordinal));

    public IReadOnlyList<JobLease> Leases() => [.. _jobs.Values.Select(r => new JobLease(r.Job.Id, r.Lease))];

    /// <summary>Cancels every job of <paramref name="renewed"/>'s request that the database no longer holds for this instance.</summary>
    public IReadOnlyList<LeasedJob> LoseLeases(IReadOnlyList<JobLease> requested, IReadOnlySet<Guid> renewed)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(renewed);
        var lost = new List<LeasedJob>();
        foreach (var lease in requested)
        {
            if (!renewed.Contains(lease.JobId) && _jobs.TryGetValue(lease.JobId, out var running))
            {
                running.LoseLease();
                lost.Add(running.Job);
            }
        }

        return lost;
    }
}
