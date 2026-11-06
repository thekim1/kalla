using Ume.Rag.Domain;

namespace Ume.Rag.Retrieval;

/// <summary>
/// The rerank seam (knowledge design §6.7; Decisions "Rerank pulled forward"). <see cref="RetrievalEngine"/> calls it after
/// fusion and dedup, before the per-document cap, over the top <c>rerankCandidates</c> results, and only when the request
/// carries a <see cref="RerankCall"/> (the assistant's <c>QuerySteps.retrieval.rerank</c>, off by default). The interim
/// implementation is <see cref="ChatModelReranker"/> (a listwise rerank through the gateway's chat endpoint);
/// <c>GatewayReranker</c> replaces it when the gateway's rerank endpoint exists.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// One score per passage, in the passages' order (higher is more relevant), or null when this reranker does not
    /// rerank (the fused order stands, nothing is degraded). A thrown exception or a timeout keeps the fused order with
    /// <c>degraded: "rerank_unavailable"</c>.
    /// </summary>
    Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken);
}

/// <summary>A passage to score: its chunk id and its text.</summary>
public sealed record RerankPassage(Guid ChunkId, string Text);

/// <summary>
/// What a search asks of the reranker: the normalised query, the passages in fused order, the platform's rerank model
/// (<c>Settings.Retrieval.rerankModel</c>; null while unset), the residency ceiling of the call (the assistant's, as for
/// its chat answer) and the run id sent as <c>x-ume-run-id</c>.
/// </summary>
public sealed record RerankRequest(string Query, IReadOnlyList<RerankPassage> Passages, string? Model, Residency Ceiling, Guid? RunId);

/// <summary>
/// The scores in the passages' order, and the call's token usage when the reranker reports it (evaluation cost). A null
/// score is a passage the reranker left unscored: it keeps its fused order below every scored passage (#403); at least
/// one passage must be scored.
/// </summary>
public sealed record RerankScores(IReadOnlyList<double?> Scores, RerankUsage? Usage = null);

/// <summary>Token usage of one rerank call, for cost per query (the price comes from the model mirror).</summary>
public sealed record RerankUsage(string Model, int PromptTokens, int CompletionTokens);

/// <summary>
/// A search's request to rerank (<see cref="SearchRequest.Rerank"/>): the ceiling of the rerank call and the run id. Every
/// model call carries an explicit ceiling (AGENTS.md), so a search cannot ask for a rerank without one.
/// </summary>
public sealed record RerankCall(Residency Ceiling, Guid? RunId = null);

/// <summary>A reranker that never reranks (tests and processes without a reranker).</summary>
public sealed class NoopReranker : IReranker
{
    public static NoopReranker Instance { get; } = new();

    public Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken) => Task.FromResult<RerankScores?>(null);
}

/// <summary>Reads the texts the reranker scores (one extra statement, knowledge design §6.7).</summary>
public interface IPassageTextReader
{
    /// <summary>The texts of <paramref name="chunkIds"/> that are still searchable in the scope; missing ids are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> TextsAsync(SearchScope scope, IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken);
}
