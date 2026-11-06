using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Embedding;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// Retrieval evaluation as the Knowledge API retrieves (processing design §13.5): <see cref="RetrievalEngine"/> on the
/// worker's pool over the one collection with every level, <c>k = 10</c>, no filters, <c>AllowDegraded = false</c>, the
/// variant's mode and decompounding (§13.4). <see cref="Open"/> gives a session per job: one chunk count for the
/// exact/HNSW rule and one query embedding per question, shared by every variant of the job. <c>hybrid-rerank</c> (#399)
/// reranks through <see cref="ChatModelReranker"/> and <c>hybrid-rerank-gateway</c> (#414) through
/// <see cref="GatewayReranker"/> (<c>/v1/rerank</c>), both on the <c>gateway-rerank</c> client behind a
/// <see cref="RerankRouter"/>, with the passages read by <see cref="PostgresHydrator"/> on the same pool.
/// </summary>
public sealed class RetrievalEvaluator(
    NpgsqlDataSource dataSource,
    EvaluationQueryEmbedder embedder,
    IHttpClientFactory clients,
    ILogger<ChatModelReranker> rerankLogger,
    ILogger<GatewayReranker> gatewayRerankLogger,
    TimeProvider time)
{
    /// <summary>
    /// The statements' client-side limit: the API's 2 s (knowledge design §6.6) is too tight beside ingestion on the worker's
    /// pool; the worker role's <c>statement_timeout</c> (60 s) stays the backstop.
    /// </summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The rerank limit of <c>hybrid-rerank</c> and <c>hybrid-rerank-gateway</c>: the API's limits (3 s for the chat
    /// reranker, 500 ms for the gateway's) would partly measure the fallback, not the rerank over 20 passages, so the run
    /// allows the call this long and reports its real latency (#399, #414).
    /// </summary>
    public static readonly TimeSpan RerankTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A session for <paramref name="target"/>. The profile's last canary check must not have failed (a vector space that
    /// is not the chunks' gives meaningless numbers, processing design §11).
    /// </summary>
    /// <exception cref="StageTransientException">The last canary check failed.</exception>
    public EvaluationSession Open(EvaluationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EmbeddingCanary.EnsureNotFailed(target.Profile);
        return new EvaluationSession(target, dataSource, embedder.For(target.Profile, target.Classification),
            new RerankRouter(new ChatModelReranker(clients, ChatModelReranker.HttpClientName, rerankLogger),
                new GatewayReranker(clients, ChatModelReranker.HttpClientName, gatewayRerankLogger)), time);
    }
}

/// <summary>
/// The engines of one evaluation job: the collection's index, the scratch keyword copy for <c>hybrid-no-decompounder</c>,
/// and the index with the reranker and its passage reader for <c>hybrid-rerank</c> and <c>hybrid-rerank-gateway</c>.
/// </summary>
public sealed class EvaluationSession : IDisposable
{
    private readonly ScopeSizeCache _sizes;
    private readonly RetrievalEngine _engine;
    private readonly RetrievalEngine _withoutDecompounder;
    private readonly RetrievalEngine _reranked;
    private readonly QueryEmbedder _embed;
    private readonly Dictionary<string, ReadOnlyMemory<float>> _vectors = new(StringComparer.Ordinal);

    internal EvaluationSession(EvaluationTarget target, NpgsqlDataSource dataSource, QueryEmbedder embed, IReranker reranker, TimeProvider time)
    {
        Target = target;
        var statements = new PostgresSearchOptions { StatementTimeout = RetrievalEvaluator.StatementTimeout };
        var index = new PostgresSearchIndex(dataSource, statements);
        _sizes = new ScopeSizeCache(index, time);
        // Questions run one after another (the eval class has concurrency 1), so one slot each.
        var options = new RetrievalEngineOptions { MaxConcurrentSearches = 1 };
        _engine = new RetrievalEngine(index, _sizes, options);
        _withoutDecompounder = new RetrievalEngine(new NoDecompounderSearchIndex(dataSource, index, RetrievalEvaluator.StatementTimeout), _sizes, options);
        _reranked = new RetrievalEngine(index, _sizes, new RetrievalEngineOptions
            {
                MaxConcurrentSearches = 1,
                RerankTimeout = RetrievalEvaluator.RerankTimeout,
                GatewayRerankTimeout = RetrievalEvaluator.RerankTimeout,
            },
            reranker, new PostgresHydrator(dataSource, statements));
        _embed = embed;
    }

    public EvaluationTarget Target { get; }

    /// <summary>
    /// The reranker of a variant: <see cref="RerankerKind.ChatModel"/> for <c>hybrid-rerank</c>,
    /// <see cref="RerankerKind.Gateway"/> for <c>hybrid-rerank-gateway</c>; null for a variant without rerank.
    /// </summary>
    public static RerankerKind? Reranker(EvaluationVariant variant) => variant switch
    {
        EvaluationVariant.HybridRerank => RerankerKind.ChatModel,
        EvaluationVariant.HybridRerankGateway => RerankerKind.Gateway,
        _ => null,
    };

    /// <summary>
    /// The ceiling of a reranked call: the collection level's (identity design §4.7) capped at <c>Eu</c>, since
    /// the worker holds no <c>External</c> key (#399).
    /// </summary>
    public Residency RerankCeiling
    {
        get
        {
            var level = ResidencyCeiling.ForLevel(Target.Classification);
            return level > Residency.Eu ? Residency.Eu : level;
        }
    }

    /// <summary>
    /// The request a question is searched with: the same <see cref="SearchRequest"/> <c>POST /v1/retrieve</c> builds for
    /// this scope (issue #120), with the variant's mode and decompounding and <c>AllowDegraded = false</c>; a reranked
    /// variant adds the rerank call with its reranker at <see cref="RerankCeiling"/> with <paramref name="runId"/> as <c>x-ume-run-id</c>.
    /// </summary>
    public SearchRequest Request(EvaluationQuestionRow question, EvaluationVariant variant, RetrievalSettings settings, Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new SearchRequest(
            question.Question,
            Target.Scope(settings),
            TopK: Domain.Entities.EvaluationRun.DefaultK,
            Mode: variant == EvaluationVariant.Vector ? RetrievalMode.Vector : RetrievalMode.Hybrid,
            Embed: EmbedOnceAsync,
            AllowDegraded: false,
            Decompound: variant != EvaluationVariant.HybridNoDecompounder,
            Rerank: Reranker(variant) is { } reranker ? new RerankCall(RerankCeiling, runId, reranker) : null);
    }

    /// <summary>Searches one question without a run id.</summary>
    public Task<SearchResult> SearchAsync(EvaluationQuestionRow question, EvaluationVariant variant, RetrievalSettings settings, CancellationToken cancellationToken) =>
        SearchAsync(question, variant, settings, null, cancellationToken);

    /// <summary>
    /// Searches one question; <c>hybrid-no-decompounder</c> goes to the scratch keyword copy, a reranked variant to
    /// the engine with the reranker.
    /// </summary>
    public Task<SearchResult> SearchAsync(EvaluationQuestionRow question, EvaluationVariant variant, RetrievalSettings settings, Guid? runId,
        CancellationToken cancellationToken)
    {
        var engine = variant switch
        {
            EvaluationVariant.HybridNoDecompounder => _withoutDecompounder,
            EvaluationVariant.HybridRerank or EvaluationVariant.HybridRerankGateway => _reranked,
            _ => _engine,
        };
        return engine.SearchAsync(Request(question, variant, settings, runId), cancellationToken);
    }

    /// <summary>Every question of the set in its order, one after another; <paramref name="runId"/> goes with each rerank call.</summary>
    public async Task<IReadOnlyList<QuestionOutcome>> RunAsync(EvaluationVariant variant, RetrievalSettings settings, Guid runId, CancellationToken cancellationToken)
    {
        var outcomes = new List<QuestionOutcome>(Target.Questions.Count);
        foreach (var question in Target.Questions)
        {
            var result = await SearchAsync(question, variant, settings, runId, cancellationToken);
            outcomes.Add(new QuestionOutcome(question, result, question.Answerable ? EvaluationScoring.ExpectedRank(result, question.ExpectedDocumentIds) : null));
        }

        return outcomes;
    }

    public void Dispose()
    {
        _engine.Dispose();
        _withoutDecompounder.Dispose();
        _reranked.Dispose();
        _sizes.Dispose();
    }

    private async Task<ReadOnlyMemory<float>> EmbedOnceAsync(string normalizedQuery, CancellationToken cancellationToken)
    {
        if (!_vectors.TryGetValue(normalizedQuery, out var vector))
        {
            vector = await _embed(normalizedQuery, cancellationToken);
            _vectors[normalizedQuery] = vector;
        }

        return vector;
    }
}
