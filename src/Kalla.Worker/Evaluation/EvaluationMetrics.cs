using System.Diagnostics.Metrics;

namespace Kalla.Worker.Evaluation;

/// <summary>Evaluation metrics of processing design §15 on meter <c>Kalla</c>: the nightly recall per collection and the runs written per kind.</summary>
public sealed class EvaluationMetrics
{
    private readonly Gauge<double> _recall;
    private readonly Counter<long> _runs;

    public EvaluationMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _recall = meter.CreateGauge<double>("rag.evaluation.recall_at_10",
            description: "recall@10 of the latest nightly run of a published collection (processing design §14.8).");
        _runs = meter.CreateCounter<long>("rag.evaluation.runs", description: "Evaluation runs written, by kind.");
    }

    public void Recall(Guid collectionId, decimal recall) =>
        _recall.Record((double)recall, new KeyValuePair<string, object?>("collection", collectionId.ToString()));

    public void Runs(string kind, int count) => _runs.Add(count, new KeyValuePair<string, object?>("kind", kind));
}
