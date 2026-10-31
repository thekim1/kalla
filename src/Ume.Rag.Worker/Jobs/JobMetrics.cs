using System.Diagnostics.Metrics;

namespace Ume.Rag.Worker.Jobs;

/// <summary>Queue metrics of engine design §17 and the maintenance metrics, on meter <c>Ume.Rag</c> (repo-and-deployment §9).</summary>
public sealed class JobMetrics
{
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _requeued;
    private readonly Counter<long> _leaseLost;
    private readonly Counter<long> _dead;
    private readonly Counter<long> _reclaimed;
    private readonly Counter<long> _retentionDeleted;
    private readonly Gauge<long> _defaultRows;
    private readonly Counter<long> _drift;

    public JobMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _duration = meter.CreateHistogram<double>("rag.jobs.duration", "s", "Job run time by class and outcome.");
        _requeued = meter.CreateCounter<long>("rag.jobs.requeued", description: "Jobs put back without using an attempt, by class and reason.");
        _leaseLost = meter.CreateCounter<long>("rag.jobs.lease_lost", description: "Running jobs whose lease this instance lost.");
        _dead = meter.CreateCounter<long>("rag.jobs.dead", description: "Jobs dead-lettered (attempts used up or poison suspect).");
        _reclaimed = meter.CreateCounter<long>("rag.jobs.reclaimed", description: "Jobs the reaper took back from an expired lease or a silent instance.");
        _retentionDeleted = meter.CreateCounter<long>("rag.retention.deleted", description: "Rows removed by retention, by table.");
        _defaultRows = meter.CreateGauge<long>("rag.partitions.default_rows",
            description: "Run items left in RunItems_default after partition maintenance; above 0 means maintenance lagged.");
        _drift = meter.CreateCounter<long>("rag.reconcile.drift", description: "Documents the nightly drift sweep re-enqueued; 0 when no work was lost.");
    }

    public void Duration(string jobClass, string outcome, TimeSpan elapsed) =>
        _duration.Record(elapsed.TotalSeconds, new("class", jobClass), new("outcome", outcome));

    public void Requeued(string jobClass, string reason) => _requeued.Add(1, new("class", jobClass), new("reason", reason));

    public void LeaseLost(string jobClass) => _leaseLost.Add(1, new KeyValuePair<string, object?>("class", jobClass));

    public void Dead(string jobClass) => _dead.Add(1, new KeyValuePair<string, object?>("class", jobClass));

    public void Reclaimed(string jobClass) => _reclaimed.Add(1, new KeyValuePair<string, object?>("class", jobClass));

    public void RetentionDeleted(string table, long rows) => _retentionDeleted.Add(rows, new KeyValuePair<string, object?>("table", table));

    public void DefaultRows(long rows) => _defaultRows.Record(rows);

    public void Drift(long documents) => _drift.Add(documents);
}
