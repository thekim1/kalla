using Microsoft.Extensions.Options;
using Ume.Rag.Worker.Connectors.Hosts;

namespace Ume.Rag.Worker.Connectors.Render;

/// <summary>The outcome of asking for a render slot.</summary>
public abstract record RenderSlot
{
    private RenderSlot()
    {
    }

    /// <summary>A slot: hold it through the fetch and the render, then dispose it.</summary>
    public sealed record Granted(RenderLease Lease) : RenderSlot;

    /// <summary>
    /// No slot: <c>render.busy</c> (none free, back after <see cref="RenderReasons.BusyDelay"/>) or <c>render.not_configured</c>
    /// (no <c>Render:Endpoint</c>, back after <see cref="RenderReasons.RetryAfter"/>); the job is released without an attempt.
    /// </summary>
    public sealed record Refused(string Reason, TimeSpan Delay) : RenderSlot;
}

/// <summary>One page's hold on a render slot of this worker and of its host.</summary>
public sealed class RenderLease : IDisposable
{
    private readonly RenderSlots _slots;
    private int _released;

    internal RenderLease(RenderSlots slots, string host)
    {
        _slots = slots;
        Host = host;
    }

    public string Host { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _slots.Release(Host);
        }
    }
}

/// <summary>
/// Render capacity (connectors design §8.3, "Capacity"): <c>Render:MaxConcurrent</c> pages at once in this worker, and per
/// host its politeness profile's <c>renderPerHost</c> (§6.6, default 1). A fetch job takes a slot only when one is free,
/// before its host slot and its request; otherwise it is released back to the queue (<c>render.busy</c>, no attempt), so
/// browser work never holds the <c>fetch</c> slots HTTP sources need. Per instance: with more <c>fetch</c> instances each
/// has its own slots, and the host budget still paces every request.
/// </summary>
public sealed class RenderSlots(IOptions<RenderOptions> options, ICrawlPolicySource policy, RenderMetrics metrics)
{
    private readonly Dictionary<string, int> _perHost = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _total;

    /// <summary>Pages being rendered in this worker.</summary>
    public int InUse
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    /// <summary>A slot for a page of <paramref name="host"/> (the URL's authority, as the host budget keys it), never waiting.</summary>
    public RenderSlot TryAcquire(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!options.Value.Configured)
        {
            return new RenderSlot.Refused(RenderReasons.NotConfigured, RenderReasons.RetryAfter);
        }

        var perHost = policy.Current.ProfileOf(host).RenderPerHost;
        lock (_lock)
        {
            var onHost = _perHost.GetValueOrDefault(host);
            if (_total >= options.Value.MaxConcurrent || onHost >= perHost)
            {
                metrics.Busy();
                return new RenderSlot.Refused(RenderReasons.Busy, RenderReasons.BusyDelay);
            }

            _total++;
            _perHost[host] = onHost + 1;
        }

        metrics.SlotTaken();
        return new RenderSlot.Granted(new RenderLease(this, host));
    }

    internal void Release(string host)
    {
        lock (_lock)
        {
            _total--;
            if (--_perHost[host] == 0)
            {
                _perHost.Remove(host);
            }
        }

        metrics.SlotReleased();
    }
}
