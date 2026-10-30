using System.Collections.Concurrent;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>
/// Wakes this process's dispatcher for a job class (data-model §4.3). Signalled by local enqueues and, as the
/// <c>rag_jobs</c> <see cref="INotificationListener"/> on the config monitor's listening connection (data-model §1.1,
/// issue #21), by every other process's enqueue, whose notification payload is the job class. The dispatcher also polls
/// every <c>Worker:PollSeconds</c> (2 s), so a missed notification costs at most one poll. Signals collapse: one pending
/// wake-up per class at most.
/// </summary>
public sealed class JobWakeUps(TimeProvider time) : INotificationListener
{
    /// <summary>The channel <c>PostgresJobQueue.EnqueueAsync</c> notifies at commit.</summary>
    public const string ChannelName = "rag_jobs";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.Ordinal);

    public string Channel => ChannelName;

    public void Signal(string jobClass)
    {
        var signal = Get(jobClass);
        if (signal.CurrentCount > 0)
        {
            return;
        }

        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Signalled concurrently: one pending wake-up is enough.
        }
    }

    /// <summary>
    /// A <c>rag_jobs</c> notification. Only the classes of <see cref="JobClasses.All"/> (what <c>CK_Jobs_Class</c> lets
    /// into the queue) are signalled: any role may <c>pg_notify</c>, and every new payload would keep a semaphore for good.
    /// </summary>
    public void OnNotification(string payload)
    {
        if (JobClasses.All.Contains(payload, StringComparer.Ordinal))
        {
            Signal(payload);
        }
    }

    /// <summary>Waits for a signal of <paramref name="jobClass"/> or until <paramref name="timeout"/>; true when signalled.</summary>
    public async Task<bool> WaitAsync(string jobClass, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var poll = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(poll.Token, cancellationToken);
        try
        {
            await Get(jobClass).WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private SemaphoreSlim Get(string jobClass) => _signals.GetOrAdd(jobClass, static _ => new SemaphoreSlim(0, 1));
}
