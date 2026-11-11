using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// B-33 (#499 step 2, api part; knowledge design §6.2): rag-api builds the keyword stop list from the worker's
/// <c>content."KeywordStatistics"</c> rows and runs no <c>ts_stat</c>; the stop list equals the one rag-api computed with
/// <c>ts_stat</c> over the live chunks before (the statement is kept here as the reference), on the evaluation smoke
/// collection and on scopes large enough to have one.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class KeywordStatisticsReadTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const int Dims = 3;

    /// <summary>rag-api's statement until B-33 (<c>RetrievalStatements.FrequentLexemes</c>), the reference for "today's" list.</summary>
    private const string FrequentLexemesBeforeB33 = """
        WITH counts AS (
            SELECT "EmbeddingProfileId" AS profile, "CollectionId" AS collection, count(*) AS chunks
            FROM content."Chunks"
            WHERE "DeletedAt" IS NULL AND "Kind" = 'child' AND "EmbeddingProfileId" IS NOT NULL
            GROUP BY 1, 2
            HAVING count(*) >= $1
        )
        SELECT counts.profile, counts.collection, s.word, s.ndoc::bigint
        FROM counts, LATERAL ts_stat(format(
            'SELECT "SearchVector" FROM content."Chunks" WHERE "Kind" = ''child'' AND "DeletedAt" IS NULL AND "EmbeddingProfileId" = %L AND "CollectionId" = %L',
            counts.profile, counts.collection)) s
        WHERE s.ndoc >= counts.chunks * $2
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ContentFixtureWriter _writer = null!;
    private NpgsqlDataSource _api = null!;
    private Guid _profile;

    public async ValueTask InitializeAsync()
    {
        _writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin), (_, dims) => [1, .. new float[dims - 1]]);
        _api = RetrievalDataSource.Create(database.Connection(Api));
        _profile = await _writer.AddEmbeddingProfileAsync(Dims, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Rag_api_reads_the_worker_rows_and_runs_no_ts_stat()
    {
        // No statement text of the search core can run it.
        typeof(RetrievalStatements).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ShouldNotContain(sql => sql.Contains("ts_stat", StringComparison.OrdinalIgnoreCase));

        var collection = await AddCollectionAsync(1_200, "trumpet", every: 3);
        await _writer.RefreshKeywordStatisticsAsync(collection, Ct);

        // What the api role sends: Npgsql's tracing, filtered to a data source of its own (other tests share the process).
        const string traced = "b33-api";
        var builder = new NpgsqlDataSourceBuilder(database.Connection(Api)) { Name = traced };
        await using var api = RetrievalDataSource.Configure(builder).Build();
        var statements = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var tags = activity.TagObjects.Select(t => t.Value?.ToString() ?? "").ToList();
                if (tags.Contains(traced))
                {
                    lock (statements)
                    {
                        statements.AddRange(tags);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var index = new PostgresSearchIndex(api);
        using var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        using var engine = new RetrievalEngine(index, sizes);
        (await sizes.WarmAsync(Ct)).ShouldBeTrue();
        var stop = await sizes.StopLexemesAsync(Scope(collection), Ct);
        stop.ShouldContain("kommun");
        stop.ShouldContain(await LexemeAsync("trumpet")); // 400 of 1,200
        (await engine.SearchAsync(new SearchRequest("trumpet i Umeå kommun", Scope(collection), Mode: RetrievalMode.Keyword), Ct)).Hits.ShouldBeEmpty();

        lock (statements)
        {
            statements.ShouldContain(s => s.Contains("""FROM content."KeywordStatistics" """.TrimEnd(), StringComparison.Ordinal));
            statements.ShouldNotContain(s => s.Contains("ts_stat", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task The_stop_list_equals_the_one_from_ts_stat_over_the_live_chunks()
    {
        // The evaluation smoke collection (41 chunks: no rows, no stop list either way), two collections of 1,200 and one
        // of 400 with the same words at other shares, alone and together.
        var smoke = (await SmokeSet.SeedAsync(_writer, _profile, cancellationToken: Ct)).CollectionId;
        var large = await AddCollectionAsync(1_200, "fagott", every: 3);
        var other = await AddCollectionAsync(1_200, "fagott", every: 5);
        var small = await AddCollectionAsync(400, "fagott", every: 2);
        foreach (var collection in new[] { smoke, large, other, small })
        {
            await _writer.RefreshKeywordStatisticsAsync(collection, Ct);
        }

        var index = new PostgresSearchIndex(_api);
        using var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        var before = await FrequentLexemesBeforeAsync();
        var counts = (await index.CountChunksAsync(Ct)).Where(c => c.EmbeddingProfileId == _profile)
            .GroupBy(c => c.CollectionId).ToDictionary(g => g.Key, g => g.Sum(c => c.Chunks));

        Guid[][] scopes = [[smoke], [large], [other], [small], [large, other], [other, small], [large, other, small], [smoke, other]];
        foreach (var scope in scopes)
        {
            var now = await sizes.StopLexemesAsync(Scope(scope), Ct);
            now.ShouldBe(TodaysStopList(scope, before, counts), $"scope of {scope.Length}");
        }

        (await sizes.StopLexemesAsync(Scope(smoke), Ct)).ShouldBeEmpty();
        var fagott = await LexemeAsync("fagott");
        (await sizes.StopLexemesAsync(Scope(large), Ct)).ShouldContain(fagott); // 400 of 1,200
        (await sizes.StopLexemesAsync(Scope(large, other), Ct)).ShouldNotContain(fagott); // 400 + 240 of 2,400
        (await sizes.StopLexemesAsync(Scope(large, other), Ct)).ShouldContain("kommun");
    }

    /// <summary>ScopeSizeCache's rule before B-33 over the reference statement's rows and the live counts.</summary>
    private static List<string> TodaysStopList(
        Guid[] scope, IReadOnlyList<(Guid Collection, string Lexeme, long Chunks)> lexemes, Dictionary<Guid, long> counts)
    {
        var chunks = scope.Sum(c => counts.GetValueOrDefault(c));
        if (chunks < ScopeSizeCache.KeywordStopMinChunks)
        {
            return [];
        }

        return [.. lexemes.Where(l => scope.Contains(l.Collection)).GroupBy(l => l.Lexeme)
            .Where(g => g.Sum(l => l.Chunks) > ScopeSizeCache.KeywordStopShare * chunks).Select(g => g.Key).Order(StringComparer.Ordinal)];
    }

    private async Task<List<(Guid Collection, string Lexeme, long Chunks)>> FrequentLexemesBeforeAsync()
    {
        var rows = new List<(Guid, string, long)>();
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(FrequentLexemesBeforeB33, connection)
        {
            Parameters = { new() { Value = ScopeSizeCache.KeywordStatisticsMinChunks }, new() { Value = ScopeSizeCache.KeywordStatisticsShare } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            if (reader.GetGuid(0) == _profile)
            {
                rows.Add((reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3)));
            }
        }

        return rows;
    }

    /// <summary>The Swedish stem of one word, as <c>SearchVector</c> holds it.</summary>
    private async Task<string> LexemeAsync(string word)
    {
        await using var connection = await _api.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT (unnest(to_tsvector('swedish', $1))).lexeme", connection) { Parameters = { new() { Value = word } } };
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A collection of <paramref name="chunks"/> chunks with "Umeå kommun" in all and the topic word in every <paramref name="every"/>th.</summary>
    private async Task<Guid> AddCollectionAsync(int chunks, string topic, int every)
    {
        var collection = Guid.NewGuid();
        for (var start = 0; start < chunks; start += 100)
        {
            await _writer.AddDocumentAsync(
                new FixtureDocumentSpec { CollectionId = collection, EmbeddingProfileId = _profile, Classification = "public", Title = "Fixture" },
                [.. Enumerable.Range(start, Math.Min(100, chunks - start)).Select(n => new FixtureChunk(
                    $"Umeå kommun informerar om sida {n}{(n % every == 0 ? " " + topic : "")}") { Embedding = [1, 0, 0] })], Ct);
        }

        return collection;
    }

    private SearchScope Scope(params Guid[] collections) => new(
        _profile, Dims, [.. collections.Select(c => new CollectionInScope(c, RetrievalSettings.Default))], ["public"], RetrievalPlatformSettings.Default);
}
