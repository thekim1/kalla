using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// B-25 (#499): how long the search statistics take as <c>ume_rag_api</c> as the corpus grows, against the role's 5 s
/// <c>statement_timeout</c>. Since B-33 the keyword statistics are a read of the worker's rows (written here per full
/// collection, as the worker would). Explicit: it loads up to <c>UME_RAG_MEASURE_CHUNKS</c> chunks (default 1,000,000, in
/// collections of 50,000; about 10 minutes and 3–4 GB in the container), so it runs only on request:
/// <c>UME_RAG_TEST_POSTGRES_TMPFS=1 dotnet test --project tests/Ume.Rag.IntegrationTests -c Release -- --filter-class
/// "*StatisticsCostMeasurements" --explicit only --output Detailed</c> (the variable keeps the database in RAM, so the
/// load does not write gigabytes to disk). The numbers go to the test output and <c>results-b.md</c>.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class StatisticsCostMeasurements(RagDatabaseFixture database)
{
    private const int CollectionChunks = 50_000;
    private const int DocumentChunks = 500;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Search_statistics_against_the_api_role_timeout()
    {
        var target = int.TryParse(Environment.GetEnvironmentVariable("UME_RAG_MEASURE_CHUNKS"), CultureInfo.InvariantCulture, out var n) ? n : 1_000_000;
        var checkpoints = new[] { 100_000, 250_000, 500_000, 1_000_000, 2_000_000 }.Where(c => c < target).Append(target).ToArray();
        await using var writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var api = RetrievalDataSource.Create(database.Connection(Api));
        var index = new PostgresSearchIndex(api);
        var output = TestContext.Current.TestOutputHelper!;
        var random = new Random(17);
        string[] common = ["umeå", "kommun", "kontakt", "information"];
        string[] topics = ["bygglov", "förskoleplats", "avfallstaxa", "parkeringstillstånd", "äldreomsorg", "simhall", "bibliotek", "färdtjänst"];
        string[]?[] tagSets = [null, ["bygga-bo"], ["skola"], ["omsorg"], ["bygga-bo", "trafik"]];
        var loaded = 0;
        var collection = Guid.Empty;

        foreach (var checkpoint in checkpoints)
        {
            var started = Stopwatch.StartNew();
            while (loaded < checkpoint)
            {
                if (loaded % CollectionChunks == 0)
                {
                    if (collection != Guid.Empty)
                    {
                        await writer.RefreshKeywordStatisticsAsync(collection, Ct); // the worker's rows (A-29) for the full one
                    }

                    collection = Guid.NewGuid();
                }

                await writer.AddDocumentAsync(new FixtureDocumentSpec { CollectionId = collection, EmbeddingProfileId = profile },
                    [.. Enumerable.Range(0, DocumentChunks).Select(_ =>
                    {
                        // The P1 measurement's shape (~120 words): frequent terms in nine of ten chunks, one topic word, filler.
                        var words = new List<string>(124);
                        if (random.NextDouble() < 0.9)
                        {
                            words.AddRange(common);
                        }

                        words.Add(topics[random.Next(topics.Length)]);
                        for (var i = 0; i < 115; i++)
                        {
                            words.Add("ord" + random.Next(20_000).ToString(CultureInfo.InvariantCulture));
                        }

                        var text = string.Join(' ', words);
                        return new FixtureChunk(text) { SearchText = text, Embedding = new float[8], Tags = tagSets[random.Next(tagSets.Length)] };
                    })], Ct);
                loaded += DocumentChunks;
            }

            await Sql.ExecuteAsync(database.Connection(null), """VACUUM ANALYZE content."Chunks" """);
            var size = await Sql.ScalarAsync<string>(database.Connection(null),
                """SELECT pg_size_pretty(pg_total_relation_size('content."Chunks"'))""");
            output.WriteLine($"{loaded:N0} chunks in {loaded / CollectionChunks} collections (loaded in {started.Elapsed.TotalSeconds:F0} s; Chunks {size}):");

            await writer.RefreshKeywordStatisticsAsync(collection, Ct);
            var counts = await TimeUnlimitedAsync(RetrievalStatements.CountChunks, []);
            var lexemes = await TimeUnlimitedAsync(RetrievalStatements.KeywordStatistics, []);
            output.WriteLine($"  CountChunks {counts}");
            output.WriteLine($"  KeywordStatistics {lexemes}");
            output.WriteLine($"  through PostgresSearchIndex (role timeout 5 s): CountChunks {await OutcomeAsync(() => index.CountChunksAsync(Ct))}, " +
                $"KeywordStatistics {await OutcomeAsync(() => index.KeywordStatisticsAsync(Ct))}");
        }
    }

    /// <summary>Three runs as <c>ume_rag_api</c> with the server-side limit lifted, so the true duration shows.</summary>
    private async Task<string> TimeUnlimitedAsync(string sql, object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await connection.OpenAsync(Ct);
        var runs = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var limit = new NpgsqlCommand("SET LOCAL statement_timeout = 0", connection, transaction))
            {
                await limit.ExecuteNonQueryAsync(Ct);
            }

#pragma warning disable CA2100 // The statements are the constants of RetrievalStatements.
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
#pragma warning restore CA2100
            foreach (var value in parameters)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = value });
            }

            var started = Stopwatch.StartNew();
            var rows = 0;
            await using (var reader = await command.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    rows++;
                }
            }

            runs.Add(started.Elapsed.TotalMilliseconds);
            await transaction.CommitAsync(Ct);
            if (i == 2)
            {
                runs.Sort();
                return string.Create(CultureInfo.InvariantCulture, $"median {runs[1]:F0} ms (min {runs[0]:F0}, max {runs[2]:F0}; {rows} rows)");
            }
        }

        throw new UnreachableException();
    }

    private static async Task<string> OutcomeAsync<T>(Func<Task<IReadOnlyList<T>>> run)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var rows = await run();
            return string.Create(CultureInfo.InvariantCulture, $"completed in {started.Elapsed.TotalMilliseconds:F0} ms ({rows.Count} rows)");
        }
        catch (SearchUnavailableException e)
        {
            return string.Create(CultureInfo.InvariantCulture, $"FAILED after {started.Elapsed.TotalMilliseconds:F0} ms ({e.InnerException?.Message})");
        }
    }
}
