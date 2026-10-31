using System.Diagnostics.Metrics;

namespace Ume.Rag.Retrieval;

/// <summary>Search-core metrics on meter <c>Ume.Rag</c> (knowledge design §6.4, §15; repo-and-deployment §9).</summary>
public sealed class RetrievalMetrics
{
    private readonly Counter<long> _exactLarge;
    private readonly Counter<long> _unavailable;

    public RetrievalMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _exactLarge = meter.CreateCounter<long>("rag.retrieval.exact_large",
            description: "Searches over more than exactScanMaxChunks chunks run exactly because the scope is too selective for HNSW.");
        _unavailable = meter.CreateCounter<long>("rag.retrieval.search_unavailable",
            description: "Searches refused for want of a slot or a statement that timed out, by reason.");
    }

    public void ExactLarge() => _exactLarge.Add(1);

    public void SearchUnavailable(string reason) => _unavailable.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
