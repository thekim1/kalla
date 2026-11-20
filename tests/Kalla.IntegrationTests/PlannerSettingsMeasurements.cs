using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Kalla.Retrieval.Postgres;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// Item A-22 (#500): statements K, V-exact, V-approximate and H under PostgreSQL's built-in planner settings and under
/// <c>deploy/postgres/postgresql.conf</c>'s (<c>jit = off</c>, <c>random_page_cost = 1.1</c>,
/// <c>effective_io_concurrency = 200</c>), each set per transaction with <c>SET LOCAL</c>, so one corpus serves both.
/// Explicit: it loads 55,000 chunks of <c>vector(1024)</c> and takes several minutes (<c>dotnet test --project
/// tests/Kalla.IntegrationTests -c Release -- --filter-class "*PlannerSettingsMeasurements" --explicit only</c>); the
/// plans and timings go to the test output and <c>results-a.md</c>. The test database runs on tmpfs, so the I/O settings
/// can change plans here but not read latency. It asserts nothing about speed.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class PlannerSettingsMeasurements(RagDatabaseFixture database)
{
    private const int Dimensions = 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Statements_under_built_in_and_configured_planner_settings()
    {
        await using var writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin));
        var profile = await writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var large = Guid.NewGuid(); // 50,000 chunks: above exactScanMaxChunks, so V is approximate (HNSW)
        var small = Guid.NewGuid(); // 5,000 chunks: V is exact
        var random = new Random(17);
        await LoadAsync(writer, profile, large, 1000, random);
        await LoadAsync(writer, profile, small, 100, random);
        await Sql.ExecuteAsync(database.Connection(null), """VACUUM ANALYZE content."Chunks" """);
        await Sql.ExecuteAsync(database.Connection(null), """VACUUM ANALYZE content."Documents" """);

        var output = TestContext.Current.TestOutputHelper!;
        var server = await Sql.ScalarAsync<string>(database.Connection(null), "SELECT version()");
        var jitAvailable = await Sql.ScalarAsync<bool>(database.Connection(null), "SELECT pg_jit_available()");
        var ioDefault = await Sql.ScalarAsync<string>(database.Connection(null),
            "SELECT boot_val FROM pg_settings WHERE name = 'effective_io_concurrency'");
        output.WriteLine($"{server}; JIT available: {jitAvailable}");

        var builtIn = new Settings("built-in", "on", "4", ioDefault);
        var configured = new Settings("configured", "off", "1.1", "200");
        var vector = "'" + VectorLiteral(RandomUnitVector(random, Dimensions)) + "'";
        var hitIds = await HitIdsAsync(RetrievalStatements.VectorApproximate(profile, Dimensions, 10, FilterFragments.None)
            .Replace("$1", vector, StringComparison.Ordinal)
            .Replace("$2", Uuids(large), StringComparison.Ordinal)
            .Replace("$3", "ARRAY['public']", StringComparison.Ordinal));

        var statements = new (string Name, string Sql)[]
        {
            ("K 'bygglov i umeå kommun', 50,000 chunks", RetrievalStatements.Keyword(profile, 100, FilterFragments.None)
                .Replace("$1", "'bygglov i umeå kommun'", StringComparison.Ordinal)
                .Replace("$2", Uuids(large), StringComparison.Ordinal)
                .Replace("$3", "ARRAY['public']", StringComparison.Ordinal)
                .Replace("$4::text[]", "ARRAY[]::text[]", StringComparison.Ordinal)),
            ("V-exact, 5,000 chunks", RetrievalStatements.VectorExact(profile, Dimensions, 100, FilterFragments.None)
                .Replace("$1", vector + "::vector", StringComparison.Ordinal)
                .Replace("$2", Uuids(small), StringComparison.Ordinal)
                .Replace("$3", "ARRAY['public']", StringComparison.Ordinal)),
            ("V-approximate, 50,000 chunks", RetrievalStatements.VectorApproximate(profile, Dimensions, 100, FilterFragments.None)
                .Replace("$1", vector, StringComparison.Ordinal)
                .Replace("$2", Uuids(large), StringComparison.Ordinal)
                .Replace("$3", "ARRAY['public']", StringComparison.Ordinal)),
            ("H, 10 hits, radius 2", RetrievalStatements.Hits
                .Replace("$1", Uuids([.. hitIds]), StringComparison.Ordinal)
                .Replace("$2", "ARRAY['public']", StringComparison.Ordinal)
                .Replace("$3", "2", StringComparison.Ordinal)
                .Replace("$4", Uuids(large), StringComparison.Ordinal)),
        };

        foreach (var (name, sql) in statements)
        {
            foreach (var settings in new[] { builtIn, configured })
            {
                var (timing, plan) = await MeasureAsync(sql, settings);
                output.WriteLine($"=== {name}, {settings}: {timing}");
                output.WriteLine(plan);
            }
        }
    }

    private static async Task LoadAsync(ContentFixtureWriter writer, Guid profile, Guid collection, int documents, Random random)
    {
        string[] common = ["umeå", "kommun", "kontakt", "information"];
        string[] rare = ["bygglov", "förskoleplats", "avfallstaxa", "parkeringstillstånd", "äldreomsorg", "simhall", "bibliotek", "färdtjänst"];
        for (var document = 0; document < documents; document++)
        {
            await writer.AddDocumentAsync(new FixtureDocumentSpec { CollectionId = collection, EmbeddingProfileId = profile },
                [.. Enumerable.Range(0, 50).Select(i =>
                {
                    var words = new List<string>();
                    if (random.NextDouble() < 0.9)
                    {
                        words.AddRange(common);
                    }

                    words.Add(rare[random.Next(rare.Length)]);
                    words.AddRange(Enumerable.Range(0, 115).Select(_ => "ord" + random.Next(20_000).ToString(CultureInfo.InvariantCulture)));
                    var text = string.Join(' ', words.OrderBy(_ => random.Next()));
                    return new FixtureChunk(text)
                    {
                        SearchText = text,
                        HeadingPath = $"Avsnitt {i / 5}", // sections of five chunks, for H's neighbours
                        Embedding = RandomUnitVector(random, Dimensions),
                    };
                })], Ct);
        }
    }

    private async Task<List<Guid>> HitIdsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<(Timing Timing, string Plan)> MeasureAsync(string sql, Settings settings)
    {
        await using var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand(
            $"SET LOCAL jit = {settings.Jit}; SET LOCAL random_page_cost = {settings.RandomPageCost}; SET LOCAL effective_io_concurrency = {settings.IoConcurrency}",
            connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        var samples = new List<double>();
        for (var i = 0; i < 35; i++)
        {
            var started = Stopwatch.StartNew();
            await using (var run = new NpgsqlCommand(sql, connection, transaction))
            await using (var reader = await run.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                }
            }

            if (i >= 5) // five warm-up runs
            {
                samples.Add(started.Elapsed.TotalMilliseconds);
            }
        }

        samples.Sort();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection, transaction);
        await using var plan = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await plan.ReadAsync(Ct))
        {
            lines.Add(plan.GetString(0));
        }

        return (new Timing(samples[14], samples[28], samples[^1]), string.Join('\n', lines));
    }

    private static string Uuids(params Guid[] ids) => "ARRAY[" + string.Join(", ", ids.Select(id => $"'{id}'")) + "]::uuid[]";

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

    private sealed record Settings(string Name, string Jit, string RandomPageCost, string IoConcurrency)
    {
        public override string ToString() =>
            $"{Name} (jit {Jit}, random_page_cost {RandomPageCost}, effective_io_concurrency {IoConcurrency})";
    }

    private sealed record Timing(double P50, double P95, double Max)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"p50 {P50:F1} ms, p95 {P95:F1} ms, max {Max:F1} ms (30 runs, client side)");
    }
}
