namespace Ume.Rag.Retrieval;

/// <summary>
/// The rerank seam (knowledge design §6.7). <see cref="RetrievalEngine"/> calls it after fusion and dedup, before the
/// per-document cap, over the top <c>rerankCandidates</c> results, and only when the request asks for reranking. P1
/// registers <see cref="NoopReranker"/>; the gateway reranker (<c>bge-reranker-v2-m3</c>) is P2.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// One score per passage, in the passages' order (higher is more relevant), or null when this reranker does not
    /// rerank (the fused order stands, nothing is degraded). A thrown exception or a timeout keeps the fused order with
    /// <c>degraded: "rerank_unavailable"</c>.
    /// </summary>
    Task<IReadOnlyList<double>?> ScoreAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken);
}

public sealed record RerankPassage(Guid ChunkId, string Text);

/// <summary>The P1 reranker: never reranks.</summary>
public sealed class NoopReranker : IReranker
{
    public static NoopReranker Instance { get; } = new();

    public Task<IReadOnlyList<double>?> ScoreAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<double>?>(null);
}

/// <summary>Reads the texts the reranker scores (one extra statement, knowledge design §6.7).</summary>
public interface IPassageTextReader
{
    /// <summary>The texts of <paramref name="chunkIds"/> that are still searchable in the scope; missing ids are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> TextsAsync(SearchScope scope, IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken);
}
