using BenchmarkDotNet.Attributes;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Text;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// The .NET side of one search (knowledge design §5–§8), everything except the two SQL statements: query normalisation
/// and decompounding, reading the candidate rows into <see cref="Candidate"/>s, RRF fusion with the decay and demote
/// multipliers, dedup and the per-document cap, coverage, and the whole <see cref="RetrievalEngine"/> over an in-memory
/// index (the engine's own overhead per search). <see cref="CandidatesPerList"/> 100 is the default
/// <c>RetrievalSettings.candidates</c>, 200 its V16 maximum.
/// </summary>
[MemoryDiagnoser]
public class RetrievalBenchmarks
{
    private const string Query = "Hur lång är handläggningstiden för bygglov för en tillbyggnad på ett attefallshus?";
    private readonly Guid _collection = Guid.NewGuid();
    private readonly Guid _source = Guid.NewGuid();
    private readonly Guid _profile = Guid.NewGuid();
    private SearchScope _scope = null!;
    private List<Candidate> _vector = null!;
    private List<Candidate> _keyword = null!;
    private CandidateList[] _lists = null!;
    private IReadOnlyList<FusedCandidate> _fused = null!;
    private IReadOnlyList<FusedCandidate> _deduped = null!;
    private IScoreAdjuster _adjuster = null!;
    private RetrievalEngine _engine = null!;
    private ScopeSizeCache _sizes = null!;
    private ReadOnlyMemory<float> _queryVector;
    private QueryEmbedder _embed = null!;

    [Params(100, 200)]
    public int CandidatesPerList { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scope = BenchData.Scope(_collection, _source, _profile);
        (_vector, _keyword) = BenchData.CandidateLists(CandidatesPerList, overlap: 0.4, _collection, _source);
        _lists = [new(CandidateListKind.Vector, 1.0, _vector), new(CandidateListKind.Keyword, 1.0, _keyword)];
        _adjuster = CombinedAdjuster.Of(RecencyAdjuster.For(_scope, DateTimeOffset.UtcNow), DemoteAdjuster.For(_scope))!;
        _fused = RrfFusion.Fuse(_lists, _adjuster);
        _deduped = ResultDiversifier.Dedup(_fused);
        _ = SwedishDecompounder.Default.WithParts("bygglov"); // load the word list outside the measurements

        var index = new InMemoryIndex(_vector, _keyword, _profile, _collection);
        _sizes = new ScopeSizeCache(index, TimeProvider.System);
        _engine = new RetrievalEngine(index, _sizes);
        _queryVector = BenchData.UnitVector(new Random(5));
        _embed = (_, _) => Task.FromResult(_queryVector);
        _engine.SearchAsync(new SearchRequest(Query, _scope, Embed: _embed), CancellationToken.None).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        _sizes.Dispose();
    }

    [Benchmark(Description = "Normalize query (NFC, whitespace)")]
    public string NormalizeQuery() => QueryNormalizer.NormalizeAndValidate(Query);

    [Benchmark(Description = "Keyword search text (decompounder)")]
    public string SearchText() => QueryNormalizer.SearchText(Query, SwedishDecompounder.Default);

    /// <summary>What the Postgres reader does per row on top of reading the columns: a record plus its two key strings.</summary>
    [Benchmark(Description = "Materialise candidates (both lists)")]
    public int MaterialiseCandidates()
    {
        var total = 0;
        foreach (var c in _vector)
        {
            total += new Candidate(c.ChunkId, c.DocumentId, c.ChunkSetId, c.Ordinal, c.TextHash, c.CollectionId, c.Level, c.DocumentType, c.SourceModifiedAt,
                c.IdentityKind, c.IdentityHash, c.SourceId, c.Url, c.Distance).Ordinal;
        }

        foreach (var c in _keyword)
        {
            total += new Candidate(c.ChunkId, c.DocumentId, c.ChunkSetId, c.Ordinal, c.TextHash, c.CollectionId, c.Level, c.DocumentType, c.SourceModifiedAt,
                c.IdentityKind, c.IdentityHash, c.SourceId, c.Url, null, c.KeywordScore).Ordinal;
        }

        return total;
    }

    [Benchmark(Description = "RRF fusion, no adjusters")]
    public IReadOnlyList<FusedCandidate> Fuse() => RrfFusion.Fuse(_lists);

    [Benchmark(Description = "RRF fusion + decay + demote")]
    public IReadOnlyList<FusedCandidate> FuseAdjusted() => RrfFusion.Fuse(_lists, _adjuster);

    [Benchmark(Description = "Build adjusters for the scope")]
    public IScoreAdjuster? BuildAdjusters() => CombinedAdjuster.Of(RecencyAdjuster.For(_scope, DateTimeOffset.UtcNow), DemoteAdjuster.For(_scope));

    [Benchmark(Description = "Dedup (text, identity group)")]
    public IReadOnlyList<FusedCandidate> Dedup() => ResultDiversifier.Dedup(_fused);

    [Benchmark(Description = "Per-document cap, top 10")]
    public IReadOnlyList<FusedCandidate> Cap() => ResultDiversifier.Cap(_deduped, 3, 10);

    [Benchmark(Description = "Coverage")]
    public Coverage Coverage() => CoverageEvaluator.Evaluate(_scope, RetrievalMode.Hybrid, false, _vector, _keyword);

    [Benchmark(Description = "Engine search, hybrid, in-memory index")]
    public Task<SearchResult> EngineSearch() => _engine.SearchAsync(new SearchRequest(Query, _scope, Embed: _embed), CancellationToken.None);

    /// <summary>Returns fixed lists at once: the engine's cost with the database and the gateway taken out.</summary>
    private sealed class InMemoryIndex(List<Candidate> vector, List<Candidate> keyword, Guid profile, Guid collection) : ISearchIndex, IScopeSizeSource
    {
        public Task<IReadOnlyList<Candidate>> KeywordAsync(SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candidate>>(keyword);

        public Task<IReadOnlyList<Candidate>> VectorAsync(SearchScope scope, ReadOnlyMemory<float> vector1, RetrievalFilters? filters, int limit, VectorSearch search,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Candidate>>(vector);

        public Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CollectionChunkCount>>([new CollectionChunkCount(profile, collection, 50_000)]);

        public Task<IReadOnlyList<FrequentLexeme>> KeywordStatisticsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FrequentLexeme>>([new FrequentLexeme(profile, collection, "kommun", 40_000, 50_000)]);
    }
}
