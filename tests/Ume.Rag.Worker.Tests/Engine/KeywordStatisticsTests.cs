using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Ume.Rag.Domain;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Tests.CommandJobs;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// A-29 (knowledge design §6.2; #499 step 2): the worker keeps <c>content."KeywordStatistics"</c>. Rows are written at the
/// first check of a collection of at least 100 live child chunks, kept while the count moves by at most 10 %, recomputed
/// past it or after 7 days, and removed under 100; they equal <see cref="RetrievalStatements.FrequentLexemes"/> on the
/// same data.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class KeywordStatisticsTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Row(string Lexeme, long Ndoc, long ChunkCount, DateTime ComputedAt);

    [Fact]
    public async Task Rows_are_written_kept_under_a_10_percent_move_recomputed_past_it_or_after_7_days_and_removed_under_100_chunks()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var statistics = new KeywordStatistics(database.Connection(RagDatabase.Worker), TimeProvider.System, NullLogger<KeywordStatistics>.Instance);
            var collection = seed.CollectionId;

            // 99 chunks: under the floor, nothing is written.
            var first = await AddChunksAsync(seed, 99);
            (await statistics.CheckAsync(collection, Ct)).ShouldBe(new KeywordStatisticsCheck(0, 0, 0));
            (await RowsAsync(collection)).ShouldBeEmpty();

            // 120: the first check writes the lexemes in at least 10 % of the chunks, as FrequentLexemes finds them.
            await AddChunksAsync(seed, 21);
            (await statistics.CheckAsync(collection, Ct)).ShouldBe(new KeywordStatisticsCheck(1, 0, 0));
            var rows = await RowsAsync(collection);
            rows.Select(r => (r.Lexeme, r.Ndoc, r.ChunkCount)).ShouldBe([("bygglov", 120L, 120L), ("kommun", 120L, 120L), ("altan", 31L, 120L), ("x", 120L, 120L)], ignoreOrder: true); // "x": the heading path is in the search vector too
            rows.Select(r => (r.Lexeme, r.Ndoc)).ShouldBe(await FrequentLexemesAsync(collection), ignoreOrder: true);

            // +5 (4 %): kept as they are.
            await AddChunksAsync(seed, 5);
            (await statistics.CheckAsync(collection, Ct)).ShouldBe(new KeywordStatisticsCheck(0, 1, 0));
            (await RowsAsync(collection)).ShouldBe(rows, ignoreOrder: true);

            // +10 more (135 against the stored 120, 12.5 %): recomputed.
            await AddChunksAsync(seed, 10);
            (await statistics.CheckAsync(collection, Ct)).ShouldBe(new KeywordStatisticsCheck(1, 0, 0));
            (await RowsAsync(collection)).ShouldAllBe(r => r.ChunkCount == 135);

            // Older than 7 days: recomputed although the count is the same.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."KeywordStatistics" SET "ComputedAt" = now() - interval '8 days' WHERE "CollectionId" = $1""",
                collection);
            (await statistics.CheckAsync(collection, Ct)).ShouldBe(new KeywordStatisticsCheck(1, 0, 0));
            (await RowsAsync(collection)).ShouldAllBe(r => r.ComputedAt > DateTime.UtcNow.AddDays(-1));

            // Under 100 live chunks (the first document's set deleted): the rows go.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Chunks" SET "DeletedAt" = now() WHERE "DocumentId" = $1""", first);
            (await statistics.CheckAsync(collection, Ct)).ShouldBe(new KeywordStatisticsCheck(0, 0, 1));
            (await RowsAsync(collection)).ShouldBeEmpty();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>
    /// <paramref name="count"/> live child chunks in a new document: "kommun" and "bygglov" in each, "altan" in every fourth
    /// (25 %), and one word of its own (under 10 %).
    /// </summary>
    private async Task<Guid> AddChunksAsync(SourceSeed seed, int count)
    {
        var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
        var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, RandomNumberGenerator.GetBytes(32)),
            seed.Classification, seed.CollectionId, RandomNumberGenerator.GetBytes(32)) { ChunkCount = count, State = ChunkSetState.Live };
        var chunks = Enumerable.Range(0, count).Select(i =>
        {
            var text = $"Kommun bygglov {(i % 4 == 0 ? "altan " : "")}unik{Guid.NewGuid():N}";
            return new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, i, new ChunkDraft
            {
                Ordinal = i, Prefix = "x", HeadingPath = "x", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text),
                TokenCount = 4, DocumentType = "page",
            }, EngineKit.ProfileId, new float[EngineKit.Dimensions]);
        }).ToList();
        await new IndexWriter(database.Database(RagDatabase.Worker)).WriteInvisibleAsync(set, chunks, Ct);
        return document;
    }

    private async Task<List<Row>> RowsAsync(Guid collection)
    {
        var rows = new List<Row>();
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Lexeme", "Ndoc", "ChunkCount", "ComputedAt" FROM content."KeywordStatistics" WHERE "CollectionId" = $1
            """, connection) { Parameters = { new() { Value = collection } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new Row(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDateTime(3)));
        }

        return rows;
    }

    /// <summary>rag-api's statement until B-33, on the same data: this collection's rows.</summary>
    private async Task<List<(string, long)>> FrequentLexemesAsync(Guid collection)
    {
        var rows = new List<(string, long)>();
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(RetrievalStatements.FrequentLexemes, connection)
        {
            Parameters = { new() { Value = 100L }, new() { Value = 0.10 } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            if (reader.GetGuid(1) == collection)
            {
                rows.Add((reader.GetString(2), reader.GetInt64(3)));
            }
        }

        return rows;
    }
}
