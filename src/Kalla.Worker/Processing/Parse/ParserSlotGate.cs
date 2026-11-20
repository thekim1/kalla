using Kalla.Worker.Parsing;

namespace Kalla.Worker.Processing.Parse;

/// <summary>Who waits for a parser conversion slot; a waiting preview goes before any waiting <c>parse</c> range.</summary>
public enum ParserSlotPriority
{
    Parse,
    Preview,
}

/// <summary>
/// The in-process priority gate in front of the parser's conversion slots (engine design §9.5 "Preview first"): a caller
/// holds a slot for one <c>/v1/convert</c> request (one range) and disposes the lease after it. A freed slot goes to the
/// oldest waiting preview, else to the oldest waiting <c>parse</c> range, so with one slot a preview waits at most one range.
/// <c>/v1/inspect</c> and <c>/v1/ner</c> use their own parser lanes and are not gated.
/// </summary>
public interface IParserSlotGate
{
    ValueTask<IDisposable> EnterAsync(ParserSlotPriority priority, CancellationToken cancellationToken);
}

/// <summary>
/// The gate over a fixed number of conversion slots (P1: <c>Worker:Concurrency:parse</c>, the parser's
/// <c>capacity.convert_slots</c> in the pilot). A slot is handed straight to the next waiter on release, so a later caller
/// never overtakes a waiting one.
/// </summary>
public sealed class ParserSlotGate : IParserSlotGate
{
    private readonly Lock _lock = new();
    private readonly Queue<TaskCompletionSource<IDisposable>> _previews = new();
    private readonly Queue<TaskCompletionSource<IDisposable>> _parses = new();
    private int _free;

    public ParserSlotGate(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        _free = slots;
    }

    /// <summary>Callers waiting now (for tests and diagnostics).</summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _previews.Count(w => !w.Task.IsCompleted) + _parses.Count(w => !w.Task.IsCompleted);
            }
        }
    }

    public ValueTask<IDisposable> EnterAsync(ParserSlotPriority priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<IDisposable> waiter;
        lock (_lock)
        {
            if (_free > 0)
            {
                _free--;
                return ValueTask.FromResult<IDisposable>(new Lease(this));
            }

            waiter = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            (priority == ParserSlotPriority.Preview ? _previews : _parses).Enqueue(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            // A cancelled waiter stays queued until a release skips it.
            var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return new ValueTask<IDisposable>(waiter.Task);
    }

    private void Release()
    {
        lock (_lock)
        {
            while (_previews.Count > 0 || _parses.Count > 0)
            {
                var next = _previews.Count > 0 ? _previews.Dequeue() : _parses.Dequeue();
                if (next.TrySetResult(new Lease(this)))
                {
                    return;
                }
            }

            _free++;
        }
    }

    private sealed class Lease(ParserSlotGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// The ambient priority of the parser calls in the current flow: <c>parse</c> unless a preview job runs its stages inside
/// <see cref="AsPreview"/>.
/// </summary>
public static class ParserSlots
{
    private static readonly AsyncLocal<ParserSlotPriority?> Current = new();

    public static ParserSlotPriority Priority => Current.Value ?? ParserSlotPriority.Parse;

    /// <summary>Parser calls in this flow wait as a preview until the scope is disposed.</summary>
    public static IDisposable AsPreview() => new Scope(ParserSlotPriority.Preview);

    private sealed class Scope : IDisposable
    {
        private readonly ParserSlotPriority? _previous = Current.Value;

        public Scope(ParserSlotPriority priority) => Current.Value = priority;

        public void Dispose() => Current.Value = _previous;
    }
}

/// <summary>A converter that holds a gate slot for each request, at the flow's <see cref="ParserSlots.Priority"/>.</summary>
public sealed class GatedParserConverter(IParserConverter inner, IParserSlotGate gate) : IParserConverter
{
    public async Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
    {
        using var slot = await gate.EnterAsync(ParserSlots.Priority, cancellationToken);
        return await inner.ConvertAsync(file, options, cancellationToken);
    }
}
