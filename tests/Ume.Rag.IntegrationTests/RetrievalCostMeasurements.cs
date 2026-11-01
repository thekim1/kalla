using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// Cost measurements for to-verify #5 (statement K with frequent terms at 100k chunks) and #6 (exact scan over 10,000
/// TOASTed <c>vector(1024)</c> values). Explicit: they load 110k chunks and take about a minute, so they run only on
/// request (<c>dotnet test --project tests/Ume.Rag.IntegrationTests -c Release -- --filter-class
/// "*RetrievalCostMeasurements" --explicit only</c>); the numbers go to the test output and <c>results-b.md</c>.
/// They assert only generous bounds, so a slow developer machine does not fail them.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class RetrievalCostMeasurements(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Exact_scan_over_ten_thousand_1024_dimension_vectors()
    {
        await using var writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin));
        var profile = await writer.AddEmbeddingProfileAsync(1024, Ct);
        var collection = Guid.NewGuid();
        var random = new Random(11);
        for (var document = 0; document < 100; document++)
        {
            await writer.AddDocumentAsync(new FixtureDocumentSpec { CollectionId = collection, EmbeddingProfileId = profile },
                [.. Enumerable.Range(0, 100).Select(i => new FixtureChunk($"dokument {document} del {i}")
                {
                    SearchText = $"dokument {document} del {i}",
                    Embedding = RandomUnitVector(random, 1024),
                })], Ct);
        }

        await Sql.ExecuteAsync(database.Connection(null), """VACUUM ANALYZE content."Chunks" """);
        var toast = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT pg_size_pretty(pg_relation_size(reltoastrelid)) FROM pg_class WHERE oid = 'content."Chunks"'::regclass
            """);
        await using var api = RetrievalDataSource.Create(database.Connection(Api));
        var index = new PostgresSearchIndex(api);
        using var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        var scope = new SearchScope(profile, 1024, [new CollectionInScope(collection, RetrievalSettings.Default)], ["public"], RetrievalPlatformSettings.Default);
        ScopeSizeCache.Choose(await sizes.SizeAsync(scope, Ct), scope.Platform).Search.ShouldBe(VectorSearch.Exact);

        var exact = await TimeAsync(() => index.VectorAsync(scope, RandomUnitVector(random, 1024), null, 100, VectorSearch.Exact, Ct));
        var approximate = await TimeAsync(() => index.VectorAsync(scope, RandomUnitVector(random, 1024), null, 100, VectorSearch.Approximate, Ct));
        var plan = await ExplainAnalyzeAsync(RetrievalStatements.VectorExact(profile, 1024, 100, FilterFragments.None), collection,
            "'" + VectorLiteral(RandomUnitVector(random, 1024)) + "'::vector");

        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine($"#6 exact V over 10,000 vector(1024) chunks (Chunks TOAST relation: {toast}): {exact}");
        output.WriteLine($"#6 for comparison, approximate V (HNSW halfvec) on the same scope: {approximate}");
        output.WriteLine(plan);
        exact.P95.ShouldBeLessThan(500);
    }

    [Fact(Explicit = true)]
    public async Task Keyword_statement_with_frequent_terms_at_one_hundred_thousand_chunks()
    {
        await using var writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        var collection = Guid.NewGuid();
        var random = new Random(13);
        string[] common = ["umeå", "kommun", "kontakt", "information"];
        string[] rare = ["bygglov", "förskoleplats", "avfallstaxa", "parkeringstillstånd", "äldreomsorg", "simhall", "bibliotek", "färdtjänst"];
        for (var document = 0; document < 1000; document++)
        {
            await writer.AddDocumentAsync(new FixtureDocumentSpec { CollectionId = collection, EmbeddingProfileId = profile },
                [.. Enumerable.Range(0, 100).Select(_ =>
                {
                    // ~120 words: the four frequent terms in nine of ten chunks, one topic word in one of eight, filler words.
                    var words = new List<string>();
                    if (random.NextDouble() < 0.9)
                    {
                        words.AddRange(common);
                    }

                    words.Add(rare[random.Next(rare.Length)]);
                    words.AddRange(Enumerable.Range(0, 115).Select(_ => "ord" + random.Next(20_000).ToString(CultureInfo.InvariantCulture)));
                    var text = string.Join(' ', words.OrderBy(_ => random.Next()));
                    return new FixtureChunk(text) { SearchText = text, Embedding = new float[8] };
                })], Ct);
        }

        await Sql.ExecuteAsync(database.Connection(null), """VACUUM ANALYZE content."Chunks" """);
        await using var api = RetrievalDataSource.Create(database.Connection(Api));
        var index = new PostgresSearchIndex(api);
        var scope = new SearchScope(profile, 8, [new CollectionInScope(collection, RetrievalSettings.Default)], ["public"], RetrievalPlatformSettings.Default);
        var output = TestContext.Current.TestOutputHelper!;

        // The stop list's statistics (ts_stat over the scope's search vectors), read in the background every 10 minutes.
        var statistics = Stopwatch.StartNew();
        var lexemes = await index.FrequentLexemesAsync(ScopeSizeCache.KeywordStatisticsMinChunks, ScopeSizeCache.KeywordStatisticsShare, Ct);
        output.WriteLine($"#5 stop-list statistics over 100,000 chunks: {statistics.Elapsed.TotalMilliseconds:F0} ms, {lexemes.Count} lexemes at ≥ 10 %");
        using var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        var stop = await sizes.StopLexemesAsync(scope, Ct);
        output.WriteLine($"#5 stop list (> 30 %): {string.Join(", ", stop)}");
        stop.Count.ShouldBe(4);
        var stopped = scope with { KeywordStopLexemes = stop };

        foreach (var query in new[] { "umeå kommun", "bygglov i umeå kommun", "kontakt information umeå kommun bygglov", "förskoleplats", "simhall öppettider" })
        {
            var searchText = QueryNormalizer.SearchText(query, Ume.Rag.Retrieval.Text.SwedishDecompounder.Default);
            var without = await TimeAsync(() => index.KeywordAsync(scope, searchText, null, 100, Ct));
            var with = await TimeAsync(() => index.KeywordAsync(stopped, searchText, null, 100, Ct));
            output.WriteLine($"#5 K \"{query}\" over 100,000 chunks: with the stop list {with}; without {without}");
        }

        var lexemeArray = "ARRAY[" + string.Join(", ", stop.Select(l => "'" + l + "'")) + "]::text[]";
        output.WriteLine(await ExplainAnalyzeAsync(RetrievalStatements.Keyword(profile, 100, FilterFragments.None), collection, "'bygglov i umeå kommun'", lexemeArray));
        output.WriteLine(await ExplainGenericAnalyzeAsync(RetrievalStatements.Keyword(profile, 100, FilterFragments.None), collection, "'bygglov i umeå kommun'", lexemeArray));
    }

    private static float[] RandomUnitVector(Random random, int dimensions)
    {
        var v = new float[dimensions];
        double norm = 0;
        for (var i = 0; i < dimensions; i++)
        {
            v[i] = (float)((random.NextDouble() * 2) - 1);
            norm += v[i] * v[i];
        }

        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < dimensions; i++)
        {
            v[i] *= scale;
        }

        return v;
    }

    private static string VectorLiteral(float[] v) => "[" + string.Join(',', v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static async Task<Timing> TimeAsync<T>(Func<Task<T>> run)
    {
        for (var i = 0; i < 5; i++)
        {
            await run(); // warm-up: connection, auto-prepare, buffers
        }

        var samples = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var started = Stopwatch.StartNew();
            await run();
            samples.Add(started.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return new Timing(samples[24], samples[47], samples[^1]);
    }

    private async Task<string> ExplainAnalyzeAsync(string statement, Guid collection, string first, string stop = "ARRAY[]::text[]")
    {
        var sql = statement.Replace("$1", first, StringComparison.Ordinal)
            .Replace("$2", $"ARRAY['{collection}']::uuid[]", StringComparison.Ordinal)
            .Replace("$3", "ARRAY['public']", StringComparison.Ordinal)
            .Replace("$4::text[]", stop, StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await connection.OpenAsync(Ct);
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    /// <summary>The plan a prepared statement runs once Postgres switches to the generic plan (plan_cache_mode forced).</summary>
    private async Task<string> ExplainGenericAnalyzeAsync(string statement, Guid collection, string first, string stop)
    {
        await using var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var setup = new NpgsqlCommand("SET LOCAL plan_cache_mode = force_generic_plan", connection, transaction))
        {
            await setup.ExecuteNonQueryAsync(Ct);
        }

        await using (var prepare = new NpgsqlCommand("PREPARE measured AS " + statement, connection, transaction))
        {
            await prepare.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand(
            $"EXPLAIN (ANALYZE, BUFFERS) EXECUTE measured({first}, ARRAY['{collection}']::uuid[], ARRAY['public'], {stop})",
            connection, transaction);
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string> { "Generic plan:" };
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private sealed record Timing(double P50, double P95, double Max)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"p50 {P50:F1} ms, p95 {P95:F1} ms, max {Max:F1} ms (50 runs, client side)");
    }
}
