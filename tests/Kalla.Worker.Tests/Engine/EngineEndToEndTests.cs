using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// P1a item 9's done-when (P1 README §3): a URL-list source syncs into <b>live chunks</b> against the FakeGateway on the test
/// database (vectors from the gateway, <c>SearchVector</c> filled, the profile's HNSW index used), a second run is all
/// <c>unchanged</c>; and the negative proofs: a set held for personal data never goes live, and the deletion guard trips
/// with nothing deleted.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class EngineEndToEndTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-engine-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_url_list_syncs_into_live_chunks_and_a_second_run_is_unchanged()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name))));
        var urls = new[] { site.Url("/p/a"), site.Url("/p/b"), site.Url("/p/c") };
        var seed = await EngineKit.SeedUrlListAsync(database, urls);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var first = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            first.ShouldBe(new RunCounters(RunStates.Succeeded, 3, 0, 0, 0, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(3);
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(3);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
                WHERE d."SourceId" = $1 AND vector_dims(c."Embedding") = 1024 AND c."EmbeddingProfileId" = $2
                """, seed.SourceId, EngineKit.ProfileId)).ShouldBe(await EngineKit.LiveChunksAsync(database, seed));
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
                WHERE d."SourceId" = $1 AND c."SearchVector" @@ to_tsquery('swedish', 'bygglov')
                """, seed.SourceId)).ShouldBeGreaterThanOrEqualTo(3);
            (await ProfileIndexPlanAsync()).ShouldContain("IX_Chunks_Embedding_" + EngineKit.ProfileId.ToString("N"));
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."NodeManifest" WHERE "NodeType" IN ('fetch', 'detect', 'parse', 'clean', 'chunk', 'personal-data', 'embed', 'index')
                """)).ShouldBe(8);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."WorkerNodeSupport" WHERE "InstanceId" = $1""",
                worker.InstanceId)).ShouldBe(9);

            var sets = await Sql.ScalarAsync<long>(database.Connection(null),
                """SELECT count(*) FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId" WHERE d."SourceId" = $1""", seed.SourceId);
            var second = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            second.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 3, 0, 0, 0));
            (await Sql.ScalarAsync<long>(database.Connection(null),
                """SELECT count(*) FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId" WHERE d."SourceId" = $1""", seed.SourceId))
                .ShouldBe(sets);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_page_with_a_personal_number_in_a_public_collection_is_held_and_never_goes_live()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("ärende " + name,
            name == "flagged" ? "Sökande: Tolvan Tolvansson, personnummer 19121212-1212." : string.Empty))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/flagged"), site.Url("/p/clean")], Classification.Public);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            run.Added.ShouldBe(2);
            var held = await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id"
                WHERE d."SourceId" = $1 AND s."State" = 'held' AND s."PiiFlagged" AND d."Url" LIKE '%/p/flagged'
                """, seed.SourceId);
            held.ShouldBe(1);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."Id" = c."DocumentId"
                WHERE d."SourceId" = $1 AND d."Url" LIKE '%/p/flagged' AND c."DeletedAt" IS NULL
                """, seed.SourceId)).ShouldBe(0, "a held set's chunks are never searchable");
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThan(0, "the clean page is live");
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Reason" FROM content."RunItems" WHERE "RunId" = $1 AND "Identity" LIKE '%/p/flagged' AND "Outcome" = 'added'
                """, await LastRunAsync(seed))).ShouldBe("pii.flagged");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task When_most_pages_vanish_the_deletion_guard_trips_and_nothing_is_deleted()
    {
        var gone = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(gone.ContainsKey(name)
            ? Results.NotFound()
            : EngineKit.Html(EngineKit.Page("förskola " + name))));
        var urls = Enumerable.Range(0, 6).Select(i => site.Url($"/p/s{i}")).ToArray();
        var seed = await EngineKit.SeedUrlListAsync(database, urls);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(6);
            foreach (var i in Enumerable.Range(0, 5))
            {
                gone[$"s{i}"] = true;
            }

            var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            run.State.ShouldBe(RunStates.AwaitingDeletionConfirmation);
            run.ProposedDeletions.ShouldBe(5);
            run.Deleted.ShouldBe(0);
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(6, "the guard deleted nothing");
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."DeletionProposals" WHERE "SourceId" = $1 AND "Decision" = 'pending' AND "Reason" = 'http.404'
                """, seed.SourceId)).ShouldBe(5);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_page_removed_from_the_list_is_deleted_by_the_reconcile_phase()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("avfall " + name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/kept"), site.Url("/p/removed")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            await EngineKit.SetUrlsAsync(database, seed, [site.Url("/p/kept")]);

            var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 1, 1, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(1);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."Id" = c."DocumentId"
                WHERE d."SourceId" = $1 AND d."DeletedAt" IS NOT NULL AND c."DeletedAt" IS NULL
                """, seed.SourceId)).ShouldBe(0, "a soft-deleted document's chunks are not searchable");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private Task<Guid> LastRunAsync(SourceSeed seed) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Runs" WHERE "SourceId" = $1 ORDER BY "StartedAt" DESC LIMIT 1""", seed.SourceId);

    /// <summary>The plan of a nearest-neighbour query over the profile's chunks (sequential scans off, as a large table would choose).</summary>
    private async Task<string> ProfileIndexPlanAsync()
    {
        var query = "[" + string.Join(',', Enumerable.Range(0, EngineKit.Dimensions).Select(i => (i % 7 / 7.0).ToString("0.###", CultureInfo.InvariantCulture))) + "]";
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off; SET LOCAL enable_sort = off", connection, transaction))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand($"""
            EXPLAIN SELECT "Id" FROM content."Chunks"
            WHERE "EmbeddingProfileId" = '{EngineKit.ProfileId}' AND "DeletedAt" IS NULL
            ORDER BY "Embedding"::halfvec(1024) <=> '{query}'::halfvec(1024) LIMIT 5
            """, connection, transaction);
        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}
