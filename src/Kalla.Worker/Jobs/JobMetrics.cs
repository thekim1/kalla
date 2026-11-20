using System.Diagnostics.Metrics;
using Kalla.Infrastructure.Gateway;

namespace Kalla.Worker.Jobs;

/// <summary>
/// Queue and model-call metrics of engine design §17 and the maintenance metrics, on meter <c>Kalla</c> (repo-and-deployment §9).
/// </summary>
public sealed class JobMetrics
{
    /// <summary>The <c>source</c> of <c>rag.ingestion.cost</c>, as <c>rag.chat.cost</c>'s (chat design §17).</summary>
    private const string GatewaySource = "gateway";
    private const string EstimateSource = "estimate";

    private readonly Histogram<double> _duration;
    private readonly Counter<long> _requeued;
    private readonly Counter<long> _leaseLost;
    private readonly Counter<long> _dead;
    private readonly Counter<long> _reclaimed;
    private readonly Counter<long> _retentionDeleted;
    private readonly Gauge<long> _defaultRows;
    private readonly Counter<long> _drift;
    private readonly Counter<long> _embedTokens;
    private readonly Counter<double> _ingestionCost;

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
        _embedTokens = meter.CreateCounter<long>("rag.embed.tokens", description: "Input tokens of the worker's embedding calls the gateway answered.");
        _ingestionCost = meter.CreateCounter<double>("rag.ingestion.cost", "USD",
            "Cost of the worker's model calls the gateway answered, by job class and source (gateway or estimate).");
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

    /// <summary>
    /// One model call of a <paramref name="jobClass"/> job the gateway answered (engine design §10.6, §17): its input tokens in
    /// <c>rag.embed.tokens</c> when it was an <paramref name="embedding"/> call, and its cost in <c>rag.ingestion.cost</c>, the
    /// gateway's or, when the gateway gave none, the estimate from the mirror's prices.
    /// </summary>
    public void ModelCall(string jobClass, UsageRecord record, bool embedding)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (embedding)
        {
            _embedTokens.Add(record.InputTokens);
        }

        _ingestionCost.Add((double)(record.Estimated ? record.EstimatedCostUsd : record.CostUsd),
            new("class", jobClass), new("source", record.Estimated ? EstimateSource : GatewaySource));
    }
}
