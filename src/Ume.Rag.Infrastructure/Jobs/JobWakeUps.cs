using System.Collections.Concurrent;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>
/// Wakes this process's dispatcher for a job class (data-model §4.3). Signalled by local enqueues; the dispatcher also
/// polls every <c>Worker:PollSeconds</c> (2 s), so jobs enqueued by another process are seen within the poll interval.
/// <c>rag_jobs</c> notifications are not received yet: the worker's one listening connection (data-model §1.1) belongs to
/// the config monitor, and routing that channel through it to <see cref="Signal"/> is pending. Signals collapse: one
/// pending wake-up per class at most.
/// </summary>
public sealed class JobWakeUps(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.Ordinal);

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
