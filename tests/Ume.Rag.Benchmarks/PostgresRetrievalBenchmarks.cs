using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Npgsql;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.Retrieval.Text;
using Ume.Rag.TestKit;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// The database half of a search (knowledge design §6) on a real pgvector container with the migrated schema and the
/// api role: statement K for a rare and a common query (with the stop list the engine applies), statement V exact and
/// approximate (HNSW, halfvec), hydrating 10 hits with their neighbours, and the whole <see cref="RetrievalEngine"/>
/// (K and V in parallel, fusion, coverage) with a precomputed query vector, so only the embedding call is left out.
/// <see cref="Chunks"/> 10,000 is the exact-scan side of the planner rule (≤ 10,000 chunks, §6.4), 50,000 the HNSW side.
/// Vectors are clustered by topic (random vectors are pessimistic for HNSW, performance pass §7).
/// Needs Docker. Runs in process so the seeded database is shared by every case of a size (seeding takes minutes).
/// </summary>
[Config(typeof(InProcessConfig))]
[MemoryDiagnoser]
public class PostgresRetrievalBenchmarks
{
    private const int Topics = 200;
    private const int ChunksPerDocument = 10;
    private static readonly Dictionary<int, Seeded> Databases = [];
    private Seeded _db = null!;
    private SearchScope _scope = null!;
    private SearchScope _stopped = null!;
    private float[] _query = null!;
    private string _rare = null!;
    private string _common = null!;
    private RetrievalEngine _engine = null!;
    private ScopeSizeCache _sizes = null!;
    private HydrationRequest _hydration = null!;

    [Params(10_000, 50_000)]
    public int Chunks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _db = Databases.TryGetValue(Chunks, out var seeded) ? seeded : Databases[Chunks] = Seed(Chunks).GetAwaiter().GetResult();
        _scope = new SearchScope(_db.Profile, BenchData.Dimensions, [new CollectionInScope(_db.Collection, RetrievalSettings.Default)], ["public"],
            RetrievalPlatformSettings.Default);
        _sizes = new ScopeSizeCache(_db.Index, TimeProvider.System);
        var stop = _sizes.StopLexemesAsync(_scope, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        _stopped = _scope with { KeywordStopLexemes = stop };
        _engine = new RetrievalEngine(_db.Index, _sizes, texts: _db.Hydrator);
        var random = new Random(99);
        _query = Near(_db.Centroids[17], random, 0.15f);
        _rare = QueryNormalizer.SearchText("ämne17 ämne42 handläggning", SwedishDecompounder.Default);
        // Frequent words (the stop list drops them, §6.2) plus two mid-frequency ones that still select a few hundred chunks.
        _common = QueryNormalizer.SearchText("hur ansöker jag om bygglov hos kommunen och vad kostar ord120 ord987", SwedishDecompounder.Default);
        var hits = _db.Index.VectorAsync(_scope, _query, null, 10, VectorSearch.Exact, CancellationToken.None).GetAwaiter().GetResult();
        _hydration = new HydrationRequest([.. hits.Select(h => h.ChunkId)], [.. hits.Select(h => h.DocumentId).Distinct()], ["public"], [_db.Collection],
            _db.Sources, NeighbourRadius: 1);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        _sizes.Dispose();
    }

    [Benchmark(Description = "K: rare terms")]
    public Task<IReadOnlyList<Candidate>> KeywordRare() => _db.Index.KeywordAsync(_stopped, _rare, null, 100, CancellationToken.None);

    [Benchmark(Description = "K: common terms (stop list applied)")]
    public Task<IReadOnlyList<Candidate>> KeywordCommon() => _db.Index.KeywordAsync(_stopped, _common, null, 100, CancellationToken.None);

    /// <summary>The same query before the stop list's statistics are read (a cold cache, or a scope under its 1,000 chunks).</summary>
    [Benchmark(Description = "K: common terms, no stop list")]
    public Task<IReadOnlyList<Candidate>> KeywordCommonNoStopList() => _db.Index.KeywordAsync(_scope, _common, null, 100, CancellationToken.None);

    /// <summary>The stop list's statistics (ts_stat over every search vector): every 10 minutes in the background, and on a cold cache the first search waits for it.</summary>
    [Benchmark(Description = "Stop-list statistics (ts_stat)")]
    public Task<IReadOnlyList<FrequentLexeme>> StopListStatistics() =>
        _db.Index.FrequentLexemesAsync(ScopeSizeCache.KeywordStatisticsMinChunks, ScopeSizeCache.KeywordStatisticsShare, CancellationToken.None);

    [Benchmark(Description = "V: exact (top-N sort)")]
    public Task<IReadOnlyList<Candidate>> VectorExact() => _db.Index.VectorAsync(_scope, _query, null, 100, VectorSearch.Exact, CancellationToken.None);

    [Benchmark(Description = "V: approximate (HNSW halfvec)")]
    public Task<IReadOnlyList<Candidate>> VectorApproximate() =>
        _db.Index.VectorAsync(_scope, _query, null, 100, VectorSearch.Approximate, CancellationToken.None);

    [Benchmark(Description = "Hydrate 10 hits ± 1 neighbour")]
    public Task<Hydration> Hydrate() => _db.Hydrator.HydrateAsync(_hydration, CancellationToken.None);

    [Benchmark(Description = "Engine: hybrid search (no embedding call)")]
    public Task<SearchResult> EngineHybrid() => _engine.SearchAsync(
        new SearchRequest("Hur lång är handläggningstiden för ämne17?", _scope, Embed: (_, _) => Task.FromResult<ReadOnlyMemory<float>>(_query)),
        CancellationToken.None);

    private static async Task<Seeded> Seed(int chunks)
    {
        var database = new RagDatabase();
        await database.InitializeAsync();
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(BenchData.Dimensions);
        var collection = Guid.NewGuid();
        Guid[] sources = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())];
        var random = new Random(23);
        var centroids = Enumerable.Range(0, Topics).Select(_ => BenchData.UnitVector(random)).ToArray();
        for (var d = 0; d < chunks / ChunksPerDocument; d++)
        {
            var topic = d % Topics;
            var url = $"https://www.umea.se/omrade{d % 37}/sida-{d}";
            await writer.AddDocumentAsync(
                new FixtureDocumentSpec
                {
                    CollectionId = collection, EmbeddingProfileId = profile, SourceId = sources[d % sources.Length], Url = url, Title = $"Sida {d}",
                    SourceModifiedAt = DateTimeOffset.UtcNow.AddDays(-(d % 900)),
                },
                [.. Enumerable.Range(0, ChunksPerDocument).Select(i =>
                {
                    var text = $"ämne{topic} ämne{(topic * 7) % Topics} ord{random.Next(2000)} ord{random.Next(2000)} ord{random.Next(2000)} " +
                        BenchData.SwedishText(120, seed: (d * 31) + i);
                    return new FixtureChunk(text) { SearchText = text, Embedding = Near(centroids[topic], random, 0.25f), TokenCount = 300 };
                })]);
        }

        await using (var superuser = new NpgsqlConnection(database.Connection(null)))
        {
            await superuser.OpenAsync();
            foreach (var table in new[] { "Chunks", "Documents" })
            {
                // One statement each: VACUUM cannot run in a multi-statement command.
                await using var analyze = new NpgsqlCommand($"""VACUUM ANALYZE content."{table}" """, superuser);
                await analyze.ExecuteNonQueryAsync();
            }
        }

        var api = RetrievalDataSource.Create(database.Connection(RagDatabase.Api));
        return new Seeded(database, api, new PostgresSearchIndex(api), new PostgresHydrator(api), profile, collection, sources, centroids);
    }

    private static float[] Near(float[] centroid, Random random, float noise)
    {
        var v = new float[centroid.Length];
        double norm = 0;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = centroid[i] + (noise * (float)((random.NextDouble() * 2) - 1) / MathF.Sqrt(v.Length / 16f));
            norm += v[i] * v[i];
        }

        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= scale;
        }

        return v;
    }

    private sealed record Seeded(
        RagDatabase Database, NpgsqlDataSource Api, PostgresSearchIndex Index, PostgresHydrator Hydrator, Guid Profile, Guid Collection, Guid[] Sources,
        float[][] Centroids);

    /// <summary>In process (one seeded database per size), 3 warm-ups and 15 iterations: the statements take milliseconds.</summary>
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() =>
            AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(15).WithId("InProcess"));
    }
}
