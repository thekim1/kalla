using System.Diagnostics.Metrics;

namespace Kalla.Retrieval;

/// <summary>Search-core metrics on meter <c>Kalla</c> (knowledge design §6.4, §15; repo-and-deployment §9).</summary>
public sealed class RetrievalMetrics
{
    private readonly Counter<long> _exactLarge;
    private readonly Counter<long> _unavailable;
    private readonly Counter<long> _statistics;
    private readonly Counter<long> _rerankUnavailable;

    public RetrievalMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _exactLarge = meter.CreateCounter<long>("rag.retrieval.exact_large",
            description: "Searches over more than exactScanMaxChunks chunks run exactly because the scope is too selective for HNSW.");
        _unavailable = meter.CreateCounter<long>("rag.retrieval.search_unavailable",
            description: "Searches refused for want of a slot or a statement that timed out, by reason.");
        _statistics = meter.CreateCounter<long>("rag.retrieval.keyword_statistics_unavailable",
            description: "Failed reads of the keyword stop list's statistics; K runs with the previous list, or with every term when there is none.");
        _rerankUnavailable = meter.CreateCounter<long>("rag.retrieval.rerank_unavailable",
            description: "Searches that asked for a rerank and kept the fused order (degraded rerank_unavailable), by reason: timeout, invalid_output, error.");
    }

    public void ExactLarge() => _exactLarge.Add(1);

    public void SearchUnavailable(string reason) => _unavailable.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void KeywordStatisticsUnavailable() => _statistics.Add(1);

    public void RerankUnavailable(string reason) => _rerankUnavailable.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
