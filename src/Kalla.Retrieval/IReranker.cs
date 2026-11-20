using Kalla.Domain;
using Kalla.Domain.Retrieval;
using Kalla.Infrastructure.Gateway;

namespace Kalla.Retrieval;

/// <summary>
/// The rerank seam (knowledge design §6.7; Decisions "Rerank pulled forward"). <see cref="RetrievalEngine"/> calls it after
/// fusion and dedup, before the per-document cap, over the top <c>rerankCandidates</c> results, and only when the request
/// carries a <see cref="RerankCall"/> (the assistant's <c>QuerySteps.retrieval.rerank</c>, off by default) and the platform
/// has a model for its <see cref="RerankerKind"/>. Two implementations, chosen per assistant (B-16):
/// <see cref="ChatModelReranker"/> (a listwise rerank through the gateway's chat endpoint) and <see cref="GatewayReranker"/>
/// (the gateway's optional <c>/v1/rerank</c>); <see cref="RerankRouter"/> sends each request to the one it names.
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
/// What a search asks of the reranker: the normalised query, the passages in fused order, the platform's model for
/// <paramref name="Reranker"/> (<c>Settings.Retrieval.rerankModel</c> or <c>gatewayRerankModel</c>; the engine never asks
/// while it is unset), the residency ceiling of the call (the assistant's, as for its chat answer), the run id sent as
/// <c>x-ume-run-id</c> and the reranker to use.
/// </summary>
public sealed record RerankRequest(string Query, IReadOnlyList<RerankPassage> Passages, string? Model, Residency Ceiling, Guid? RunId,
    RerankerKind Reranker = RerankerKind.ChatModel);

/// <summary>
/// The scores in the passages' order, and the rerank call's usage (step <c>rerank</c>, the model, its tokens and the
/// gateway's cost; chat design §8.3) when a call answered: evaluation cost and the turn's usage records. A null score is a
/// passage the reranker left unscored: it keeps its fused order below every scored passage (#403); at least one passage
/// must be scored.
/// </summary>
public sealed record RerankScores(IReadOnlyList<double?> Scores, GatewayCallUsage? Usage = null);

/// <summary>
/// A search's request to rerank (<see cref="SearchRequest.Rerank"/>): the ceiling of the rerank call, the run id and the
/// reranker. Every model call carries an explicit ceiling (AGENTS.md), so a search cannot ask for a rerank without one.
/// </summary>
public sealed record RerankCall(Residency Ceiling, Guid? RunId = null, RerankerKind Reranker = RerankerKind.ChatModel);

/// <summary>A reranker that never reranks (tests and processes without a reranker).</summary>
public sealed class NoopReranker : IReranker
{
    public static NoopReranker Instance { get; } = new();

    public Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken) => Task.FromResult<RerankScores?>(null);
}

/// <summary>Sends each request to the reranker its <see cref="RerankRequest.Reranker"/> names (chat, admin test panel).</summary>
public sealed class RerankRouter(IReranker chatModel, IReranker gateway) : IReranker
{
    public Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (request.Reranker == RerankerKind.Gateway ? gateway : chatModel).ScoreAsync(request, cancellationToken);
    }
}

/// <summary>Reads the texts the reranker scores (one extra statement, knowledge design §6.7).</summary>
public interface IPassageTextReader
{
    /// <summary>The texts of <paramref name="chunkIds"/> that are still searchable in the scope; missing ids are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> TextsAsync(SearchScope scope, IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken);
}
