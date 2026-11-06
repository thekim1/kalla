using System.Diagnostics;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval.Text;

namespace Ume.Rag.Retrieval;

/// <summary>
/// Embeds the normalised query in the profile's vector space (the caller's gateway client, its cache and circuit;
/// knowledge design §5). Throws <see cref="QueryEmbeddingUnavailableException"/> when no vector can be had.
/// </summary>
public delegate Task<ReadOnlyMemory<float>> QueryEmbedder(string normalizedQuery, CancellationToken cancellationToken);

/// <summary>A search over a resolved scope (knowledge design §2.1, §6).</summary>
/// <param name="Query">The query as given; the engine normalises it (§5.1) and refuses an empty or over-long one.</param>
/// <param name="Scope">Collections, levels, profile and settings, resolved by the caller.</param>
/// <param name="TopK">Results, 1–50.</param>
/// <param name="Mode">Lists searched (§6.5).</param>
/// <param name="Filters">Optional filters, applied in the statements and so before coverage (§7).</param>
/// <param name="Embed">The query embedder; required unless <paramref name="Mode"/> is keyword. It runs while statement K runs.</param>
/// <param name="AllowDegraded">
/// Hybrid only: when the embedding fails, answer keyword-only with <c>degraded</c> set and coverage <c>unknown</c>.
/// The evaluator passes false, so a failed embedding fails the question (§5.5).
/// </param>
/// <param name="Decompound">
/// Append <see cref="SwedishDecompounder"/> parts to the keyword query (§5.1); false for the evaluation's
/// <c>hybrid-no-decompounder</c> variant.
/// </param>
/// <param name="Rerank">
/// Ask the <see cref="IReranker"/> at this call's ceiling (the assistant's <c>QuerySteps.retrieval.rerank</c>, §6.7); null
/// keeps the fused order.
/// </param>
public sealed record SearchRequest(
    string Query,
    SearchScope Scope,
    int TopK = 10,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    RetrievalFilters? Filters = null,
    QueryEmbedder? Embed = null,
    bool AllowDegraded = true,
    bool Decompound = true,
    RerankCall? Rerank = null)
{
    public const int MaxTopK = 50;
}

/// <summary>The ranked hits (ids and numbers only; hydration is the caller's, §6.3) with coverage and timings.</summary>
/// <param name="NormalizedQuery">The query as embedded and searched.</param>
/// <param name="Hits">At most <c>TopK</c> hits in final order.</param>
/// <param name="Coverage">The coverage signal (§7).</param>
/// <param name="Degraded">Null, or one of <see cref="RetrievalDegradation"/>.</param>
/// <param name="VectorSearch">How statement V ran; null when it did not run.</param>
/// <param name="Timings">Stage milliseconds.</param>
/// <param name="RerankUsage">The rerank call's token usage when one answered (cost per query in the evaluation); else null.</param>
public sealed record SearchResult(
    string NormalizedQuery,
    IReadOnlyList<SearchHit> Hits,
    Coverage Coverage,
    string? Degraded,
    VectorSearch? VectorSearch,
    SearchTimings Timings,
    RerankUsage? RerankUsage = null);

/// <summary>One result: 1-based rank, the candidate's ids, the final score and the per-list scores.</summary>
public sealed record SearchHit(int Rank, Candidate Candidate, double Score, ChunkScores Scores);

public sealed record SearchTimings(double KeywordMs, double EmbeddingMs, double VectorMs, double RerankMs, double TotalMs);

public sealed class RetrievalEngineOptions
{
    /// <summary><c>Retrieval:MaxConcurrentSearches</c> per instance (knowledge design §6.6): ⌊(pool − 3) ÷ 2⌋ = 4.</summary>
    public int MaxConcurrentSearches { get; set; } = 4;

    /// <summary>How long a search waits for a slot before <c>search_unavailable</c> (§6.6).</summary>
    public TimeSpan SlotWait { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The <see cref="ChatModelReranker"/>'s time limit, text read included (<c>Retrieval:RerankTimeout</c>; B-16: 3 s until
    /// B-12's live p95 sets it; a listwise chat call over 20 passages does not fit §6.7's 500 ms).
    /// </summary>
    public TimeSpan RerankTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The <see cref="GatewayReranker"/>'s time limit, text read included (§6.7: 1.5 s, the POC model's p95 with room,
    /// #438; <c>Retrieval:GatewayRerankTimeout</c>).
    /// </summary>
    public TimeSpan GatewayRerankTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>The time limit of <paramref name="kind"/>.</summary>
    public TimeSpan RerankTimeoutFor(RerankerKind kind) => kind == RerankerKind.Gateway ? GatewayRerankTimeout : RerankTimeout;
}

/// <summary>
/// The search core shared by the knowledge role, the evaluator and the admin test panel (knowledge design §2.1, §6–§8):
/// normalise the query; statement K on its own connection while the query is embedded; statement V (exact or HNSW,
/// §6.4) once the vector is there; RRF fusion (§6.5) with the score-adjuster seam; dedup by <c>(DocumentId, TextHash)</c>
/// and identity group (§8); the optional reranker (§6.7); the per-document cap and <c>top_k</c>; coverage (§7).
/// Reads no config and knows no principals: everything comes in through <see cref="SearchScope"/>.
/// </summary>
public sealed class RetrievalEngine : IDisposable
{
    private readonly ISearchIndex _index;
    private readonly ScopeSizeCache _sizes;
    private readonly IReranker _reranker;
    private readonly IPassageTextReader? _texts;
    private readonly IScoreAdjuster? _adjuster;
    private readonly SwedishDecompounder _decompounder;
    private readonly RetrievalMetrics? _metrics;
    private readonly RetrievalEngineOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _time;

    public RetrievalEngine(
        ISearchIndex index,
        ScopeSizeCache sizes,
        RetrievalEngineOptions? options = null,
        IReranker? reranker = null,
        IPassageTextReader? texts = null,
        IScoreAdjuster? adjuster = null,
        SwedishDecompounder? decompounder = null,
        RetrievalMetrics? metrics = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(sizes);
        _index = index;
        _sizes = sizes;
        _options = options ?? new RetrievalEngineOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrentSearches, 1);
        _reranker = reranker ?? NoopReranker.Instance;
        _texts = texts;
        _adjuster = adjuster;
        _decompounder = decompounder ?? SwedishDecompounder.Default;
        _metrics = metrics;
        _slots = new SemaphoreSlim(_options.MaxConcurrentSearches, _options.MaxConcurrentSearches);
        _time = time ?? TimeProvider.System;
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// Runs the search. Throws <see cref="ArgumentException"/> for an invalid request,
    /// <see cref="SearchUnavailableException"/> when no slot frees within the wait or a statement times out, and
    /// <see cref="QueryEmbeddingUnavailableException"/> when the embedding fails and the mode or request does not allow
    /// degrading.
    /// </summary>
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = request.Scope ?? throw new ArgumentException("The request needs a scope.", nameof(request));
        scope.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(request.TopK, 1, nameof(request));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.TopK, SearchRequest.MaxTopK, nameof(request));
        if (request.Mode != RetrievalMode.Keyword && request.Embed is null)
        {
            throw new ArgumentException("A vector or hybrid search needs a query embedder.", nameof(request));
        }

        var normalized = QueryNormalizer.NormalizeAndValidate(request.Query);
        var total = Stopwatch.StartNew();
        if (scope.Collections.Count == 0)
        {
            // Nothing searchable: no statement runs (§7: low with no searchable collection).
            var coverage = CoverageEvaluator.Evaluate(scope, request.Mode, degraded: false, [], []);
            return new SearchResult(normalized, [], coverage, null, null, new SearchTimings(0, 0, 0, 0, total.Elapsed.TotalMilliseconds));
        }

        if (!await _slots.WaitAsync(_options.SlotWait, cancellationToken))
        {
            _metrics?.SearchUnavailable("slots");
            throw new SearchUnavailableException("No search slot became free in time.");
        }

        try
        {
            return await RunAsync(request, scope, normalized, total, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<SearchResult> RunAsync(SearchRequest request, SearchScope scope, string normalized, Stopwatch total, CancellationToken cancellationToken)
    {
        var ranking = scope.Ranking;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // K starts at once, on its own connection, in parallel with the query embedding (§6.1).
        var keywordTime = 0.0;
        Task<IReadOnlyList<Candidate>> keywordTask = Task.FromResult<IReadOnlyList<Candidate>>([]);
        if (request.Mode != RetrievalMode.Vector)
        {
            var searchText = QueryNormalizer.SearchText(normalized, request.Decompound ? _decompounder : null);
            // The stop list (§6.2, §20) comes from memory once the statistics are read; only a cold cache reads them here.
            keywordTask = TimedAsync(
                async () =>
                {
                    var stop = await _sizes.StopLexemesAsync(scope, linked.Token);
                    var keywordScope = stop.Count == 0 ? scope : scope with { KeywordStopLexemes = stop };
                    return await _index.KeywordAsync(keywordScope, searchText, request.Filters, ranking.Candidates, linked.Token);
                },
                ms => keywordTime = ms);
        }

        try
        {
            string? degraded = null;
            var embeddingTime = 0.0;
            var vectorTime = 0.0;
            VectorSearch? vectorSearch = null;
            IReadOnlyList<Candidate> vectorCandidates = [];
            if (request.Mode != RetrievalMode.Keyword)
            {
                ReadOnlyMemory<float>? vector = null;
                var embedding = Stopwatch.StartNew();
                try
                {
                    vector = await request.Embed!(normalized, linked.Token);
                }
                catch (QueryEmbeddingUnavailableException e) when (request.Mode == RetrievalMode.Hybrid && request.AllowDegraded)
                {
                    degraded = e.Reason;
                }

                embeddingTime = embedding.Elapsed.TotalMilliseconds;
                if (vector is { } v)
                {
                    if (v.Length != scope.Dimensions)
                    {
                        throw new ArgumentException(
                            $"The query embedding has {v.Length} dimensions; the profile has {scope.Dimensions}.", nameof(request));
                    }

                    var plan = ScopeSizeCache.Choose(await _sizes.SizeAsync(scope, linked.Token), scope.Platform);
                    if (plan.ExactLarge)
                    {
                        _metrics?.ExactLarge();
                    }

                    vectorSearch = plan.Search;
                    var started = Stopwatch.StartNew();
                    vectorCandidates = await _index.VectorAsync(scope, v, request.Filters, ranking.Candidates, plan.Search, linked.Token);
                    vectorTime = started.Elapsed.TotalMilliseconds;
                }
            }

            var keywordCandidates = await keywordTask;

            // Fusion (§6.5): in vector and keyword mode the one list's term keeps its order.
            var lists = new List<CandidateList>(2);
            if (vectorSearch is not null)
            {
                lists.Add(new CandidateList(CandidateListKind.Vector, ranking.VectorWeight, vectorCandidates));
            }

            if (request.Mode != RetrievalMode.Vector)
            {
                lists.Add(new CandidateList(CandidateListKind.Keyword, ranking.KeywordWeight, keywordCandidates));
            }

            // §6.5: decay (each chunk's own collection recency) · demote (its source's patterns) · an injected adjuster.
            var adjuster = CombinedAdjuster.Of(RecencyAdjuster.For(scope, _time.GetUtcNow()), DemoteAdjuster.For(scope), _adjuster);
            var deduped = ResultDiversifier.Dedup(RrfFusion.Fuse(lists, adjuster));

            var rerankTime = 0.0;
            IReadOnlyDictionary<Guid, double>? rerankScores = null;
            RerankUsage? rerankUsage = null;
            if (request.Rerank is { } rerank && deduped.Count > 0)
            {
                var started = Stopwatch.StartNew();
                (deduped, rerankScores, rerankUsage, var rerankDegraded) = await RerankAsync(normalized, scope, rerank, deduped, linked.Token);
                degraded ??= rerankDegraded;
                rerankTime = started.Elapsed.TotalMilliseconds;
            }

            var taken = ResultDiversifier.Cap(deduped, ranking.MaxChunksPerDocument, request.TopK);
            var hits = taken
                .Select((f, i) => new SearchHit(
                    i + 1,
                    f.Candidate,
                    f.Score,
                    new ChunkScores(f.Fused, f.Distance, f.VectorRank, f.KeywordScore, f.KeywordRank,
                        rerankScores is not null && rerankScores.TryGetValue(f.Candidate.ChunkId, out var r) ? r : null)))
                .ToList();

            var coverage = CoverageEvaluator.Evaluate(
                scope,
                request.Mode,
                degraded is RetrievalDegradation.EmbeddingUnavailable or RetrievalDegradation.EmbeddingResidency || (vectorSearch is null && request.Mode == RetrievalMode.Hybrid),
                vectorCandidates,
                keywordCandidates,
                rerankScores is { Count: > 0 } ? rerankScores.Values.Max() : null);

            return new SearchResult(normalized, hits, coverage, degraded, vectorSearch,
                new SearchTimings(keywordTime, embeddingTime, vectorTime, rerankTime, total.Elapsed.TotalMilliseconds), rerankUsage);
        }
        catch
        {
            // Do not leave statement K running (or its failure unobserved) when the search fails elsewhere.
            await linked.CancelAsync();
            try
            {
                await keywordTask;
            }
            catch (Exception)
            {
                // The first failure is the one reported.
            }

            throw;
        }
    }

    private async Task<(IReadOnlyList<FusedCandidate> Ordered, IReadOnlyDictionary<Guid, double>? Scores, RerankUsage? Usage, string? Degraded)> RerankAsync(
        string query, SearchScope scope, RerankCall call, IReadOnlyList<FusedCandidate> deduped, CancellationToken cancellationToken)
    {
        // The callers that serve answers ask only when the platform has a model for the reranker (KnowledgeService, B-16); an
        // explicit request without one (the evaluation's hybrid-rerank) reaches the reranker, which refuses: rerank_unavailable.
        var model = scope.Platform.RerankModelFor(call.Reranker);
        var top = deduped.Take(scope.Platform.RerankCandidates).ToList();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RerankTimeoutFor(call.Reranker));
        try
        {
            IReadOnlyDictionary<Guid, string> texts = _texts is null
                ? new Dictionary<Guid, string>()
                : await _texts.TextsAsync(scope, [.. top.Select(f => f.Candidate.ChunkId)], timeout.Token);
            var passages = top.Where(f => texts.ContainsKey(f.Candidate.ChunkId))
                .Select(f => new RerankPassage(f.Candidate.ChunkId, texts[f.Candidate.ChunkId]))
                .ToList();
            if (passages.Count == 0)
            {
                return (deduped, null, null, null); // nothing left to score: the fused order stands
            }

            var result = await _reranker.ScoreAsync(new RerankRequest(query, passages, model, call.Ceiling, call.RunId, call.Reranker), timeout.Token);
            if (result is null)
            {
                return (deduped, null, null, null);
            }

            var scores = result.Scores;
            if (scores.Count != passages.Count || scores.Any(s => s is { } v && !double.IsFinite(v)) || scores.All(s => s is null))
            {
                throw new RerankOutputException("The reranker returned a different number of scores than passages, a score that is not a number, or no score.");
            }

            // Unscored passages (null, #403) stay out of byId, so they keep their fused order below the scored ones.
            var byId = passages.Select((p, i) => (p.ChunkId, Score: scores[i]))
                .Where(x => x.Score is not null)
                .ToDictionary(x => x.ChunkId, x => x.Score!.Value);
            var reordered = top
                .Select((f, i) => (f, i))
                .OrderByDescending(x => byId.TryGetValue(x.f.Candidate.ChunkId, out var s) ? s : double.NegativeInfinity)
                .ThenBy(x => x.i)
                .Select(x => x.f)
                .Concat(deduped.Skip(top.Count))
                .ToList();
            return (reordered, byId, result.Usage, null);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            _metrics?.RerankUnavailable(e is OperationCanceledException && timeout.IsCancellationRequested ? "timeout" : e is RerankOutputException ? "invalid_output" : "error");
            return (deduped, null, null, RetrievalDegradation.RerankUnavailable);
        }
    }

    private static async Task<IReadOnlyList<Candidate>> TimedAsync(Func<Task<IReadOnlyList<Candidate>>> run, Action<double> elapsed)
    {
        var started = Stopwatch.StartNew();
        try
        {
            return await run();
        }
        finally
        {
            elapsed(started.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>No search slot or a statement over its time limit: <c>503 search_unavailable</c> (knowledge design §6.6).</summary>
public sealed class SearchUnavailableException : Exception
{
    public const string Code = "search_unavailable";

    public SearchUnavailableException()
    {
    }

    public SearchUnavailableException(string message)
        : base(message)
    {
    }

    public SearchUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The query embedding failed (knowledge design §5.5). <see cref="Reason"/> is the <c>degraded</c> value a hybrid search
/// reports: <c>embedding_unavailable</c> or <c>embedding_residency</c>.
/// </summary>
public sealed class QueryEmbeddingUnavailableException : Exception
{
    public QueryEmbeddingUnavailableException()
        : this(RetrievalDegradation.EmbeddingUnavailable, "The query embedding is unavailable.")
    {
    }

    public QueryEmbeddingUnavailableException(string message)
        : this(RetrievalDegradation.EmbeddingUnavailable, message)
    {
    }

    public QueryEmbeddingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = RetrievalDegradation.EmbeddingUnavailable;
    }

    public QueryEmbeddingUnavailableException(string reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
