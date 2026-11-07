using System.Diagnostics.Metrics;

namespace Ume.Rag.Worker.Connectors.Render;

/// <summary>
/// The renderer's metrics (connectors design §8.6) on meter <c>Ume.Rag</c>: render duration by outcome, the browser's
/// requests by how they were answered, and the render slots (in use, and fetch jobs released because none was free).
/// No host names or URLs.
/// </summary>
public sealed class RenderMetrics
{
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _requests;
    private readonly UpDownCounter<long> _slots;

    public RenderMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _duration = meter.CreateHistogram<double>("rag.worker.render.duration", "s", "Page renders by outcome (ready, navigated, timeout, unavailable).");
        _requests = meter.CreateCounter<long>("rag.worker.render.requests", description: "Requests of rendered pages by answer (fetched, cached, blocked, refused, truncated, failed).");
        _slots = meter.CreateUpDownCounter<long>("rag.worker.render.slots", description: "Render slots in use (state in_use), and fetch jobs released without a free slot (state busy).");
    }

    public void Rendered(string outcome, TimeSpan elapsed) => _duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void Requests(RenderStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        Add("fetched", stats.Fetched);
        Add("cached", stats.Cached);
        Add("blocked", stats.Blocked);
        Add("refused", stats.Refused);
        Add("truncated", stats.Truncated);
        Add("failed", stats.Failed);
    }

    public void SlotTaken() => _slots.Add(1, new KeyValuePair<string, object?>("state", "in_use"));

    public void SlotReleased() => _slots.Add(-1, new KeyValuePair<string, object?>("state", "in_use"));

    public void Busy() => _slots.Add(1, new KeyValuePair<string, object?>("state", "busy"));

    private void Add(string kind, int count)
    {
        if (count > 0)
        {
            _requests.Add(count, new KeyValuePair<string, object?>("kind", kind));
        }
    }
}
