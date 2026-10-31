using Npgsql;
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
/// variant's mode and decompounding (§13.4). <see cref="OpenAsync"/> gives a session per job: one chunk count for the
/// exact/HNSW rule and one query embedding per question, shared by every variant of the job.
/// </summary>
public sealed class RetrievalEvaluator(NpgsqlDataSource dataSource, EvaluationQueryEmbedder embedder, EmbeddingCanary canary, TimeProvider time)
{
    /// <summary>
    /// The statements' client-side limit: the API's 2 s (knowledge design §6.6) is too tight beside ingestion on the worker's
    /// pool; the worker role's <c>statement_timeout</c> (60 s) stays the backstop.
    /// </summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A session for <paramref name="target"/>. The profile's canary must not have failed on this instance (a vector space
    /// that is not the chunks' gives meaningless numbers, processing design §11); unchecked, it is checked now.
    /// </summary>
    /// <exception cref="StageTransientException">The canary failed or could not be checked.</exception>
    public async Task<EvaluationSession> OpenAsync(EvaluationTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var state = canary.For(target.Profile.Id)?.State ?? (await canary.CheckAsync(target.Profile, cancellationToken)).State;
        if (state == EmbeddingCanary.CanaryState.Failed)
        {
            throw new StageTransientException("embed.canary_mismatch", TimeSpan.FromMinutes(5), countsAttempt: false);
        }

        return new EvaluationSession(target, dataSource, embedder.For(target.Profile, target.Classification), time);
    }
}

/// <summary>The engines of one evaluation job: the collection's index, and the scratch keyword copy for <c>hybrid-no-decompounder</c>.</summary>
public sealed class EvaluationSession : IDisposable
{
    private readonly ScopeSizeCache _sizes;
    private readonly RetrievalEngine _engine;
    private readonly RetrievalEngine _withoutDecompounder;
    private readonly QueryEmbedder _embed;
    private readonly Dictionary<string, ReadOnlyMemory<float>> _vectors = new(StringComparer.Ordinal);

    internal EvaluationSession(EvaluationTarget target, NpgsqlDataSource dataSource, QueryEmbedder embed, TimeProvider time)
    {
        Target = target;
        var index = new PostgresSearchIndex(dataSource, new PostgresSearchOptions { StatementTimeout = RetrievalEvaluator.StatementTimeout });
        _sizes = new ScopeSizeCache(index, time);
        // Questions run one after another (the eval class has concurrency 1), so one slot each.
        var options = new RetrievalEngineOptions { MaxConcurrentSearches = 1 };
        _engine = new RetrievalEngine(index, _sizes, options);
        _withoutDecompounder = new RetrievalEngine(new NoDecompounderSearchIndex(dataSource, index, RetrievalEvaluator.StatementTimeout), _sizes, options);
        _embed = embed;
    }

    public EvaluationTarget Target { get; }

    /// <summary>
    /// The request a question is searched with: the same <see cref="SearchRequest"/> <c>POST /v1/retrieve</c> builds for
    /// this scope (issue #120), with the variant's mode and decompounding and <c>AllowDegraded = false</c>.
    /// </summary>
    public SearchRequest Request(EvaluationQuestionRow question, EvaluationVariant variant, RetrievalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new SearchRequest(
            question.Question,
            Target.Scope(settings),
            TopK: Domain.Entities.EvaluationRun.DefaultK,
            Mode: variant == EvaluationVariant.Vector ? RetrievalMode.Vector : RetrievalMode.Hybrid,
            Embed: EmbedOnceAsync,
            AllowDegraded: false,
            Decompound: variant != EvaluationVariant.HybridNoDecompounder);
    }

    /// <summary>Searches one question; <c>hybrid-no-decompounder</c> goes to the scratch keyword copy.</summary>
    public Task<SearchResult> SearchAsync(EvaluationQuestionRow question, EvaluationVariant variant, RetrievalSettings settings, CancellationToken cancellationToken) =>
        (variant == EvaluationVariant.HybridNoDecompounder ? _withoutDecompounder : _engine).SearchAsync(Request(question, variant, settings), cancellationToken);

    /// <summary>Every question of the set in its order, one after another.</summary>
    public async Task<IReadOnlyList<QuestionOutcome>> RunAsync(EvaluationVariant variant, RetrievalSettings settings, CancellationToken cancellationToken)
    {
        var outcomes = new List<QuestionOutcome>(Target.Questions.Count);
        foreach (var question in Target.Questions)
        {
            var result = await SearchAsync(question, variant, settings, cancellationToken);
            outcomes.Add(new QuestionOutcome(question, result, question.Answerable ? EvaluationScoring.ExpectedRank(result, question.ExpectedDocumentIds) : null));
        }

        return outcomes;
    }

    public void Dispose()
    {
        _engine.Dispose();
        _withoutDecompounder.Dispose();
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
