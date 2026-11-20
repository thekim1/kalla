using System.Security.Cryptography;
using Kalla.Domain;
using Kalla.TestKit;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Tests.CommandJobs;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// B-30 (#554 review; admin design §4.5, Decisions "Editable embedding profiles before use"): the engine's set insert checks
/// that the profile still has the model, size and <c>RequestDimensions</c> it embedded with, under the profile's row lock. A
/// change an admin saved while the document was being embedded refuses the insert with <c>embed.profile_changed</c>,
/// requeued without an attempt, and leaves no set; the embedding cache's put is guarded the same way and writes nothing.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class IndexWriterProfileGuardTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_profile_changed_between_embedding_and_the_set_insert_refuses_the_insert_and_an_unchanged_one_writes()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        var model = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "GatewayModel" FROM config."EmbeddingProfiles" WHERE "Id" = $1""",
            EngineKit.ProfileId);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var writer = new IndexWriter(database.Database(RagDatabase.Worker));
            var embeddedWith = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;

            // The admin saves another model after the worker read the profile and embedded.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = 'another-model' WHERE "Id" = $1""",
                EngineKit.ProfileId);
            var (refusedSet, refusedChunk) = Set(seed, document);
            var refused = await Should.ThrowAsync<StageTransientException>(() => writer.WriteInvisibleAsync(refusedSet, [refusedChunk], embeddedWith, Ct));
            refused.Code.ShouldBe("embed.profile_changed");
            refused.CountsAttempt.ShouldBeFalse();
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ChunkSets" WHERE "Id" = $1""", refusedSet.Id)).ShouldBe(0);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1""", refusedSet.Id)).ShouldBe(0);

            // Back to the values it embedded with: the set and its chunk are written.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = $2 WHERE "Id" = $1""",
                EngineKit.ProfileId, model);
            var (set, chunk) = Set(seed, document);
            await writer.WriteInvisibleAsync(set, [chunk], embeddedWith, Ct);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1""", set.Id)).ShouldBe(1);
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = $2 WHERE "Id" = $1""",
                EngineKit.ProfileId, model);
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_cache_put_after_the_profile_changed_writes_nothing_and_an_unchanged_one_writes()
    {
        // #559 review: the cache is keyed by profile id, not model; a batch embedded with the old model and cached after the
        // admin's reset would come back on the retry under the new model.
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        var model = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "GatewayModel" FROM config."EmbeddingProfiles" WHERE "Id" = $1""",
            EngineKit.ProfileId);
        var cache = new PostgresEmbeddingCache(database.Database(RagDatabase.Worker));
        var embeddedWith = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;
        var stale = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var vector = Enumerable.Repeat(0.5f, EngineKit.Dimensions).ToArray();
        try
        {
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = 'another-model' WHERE "Id" = $1""",
                EngineKit.ProfileId);
            await cache.PutAsync(Classification.Public, embeddedWith, new Dictionary<string, float[]> { [stale] = vector }, DateTimeOffset.UtcNow, Ct);
            (await cache.GetAsync(Classification.Public, EngineKit.ProfileId, [stale], Ct)).ShouldBeEmpty();

            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = $2 WHERE "Id" = $1""",
                EngineKit.ProfileId, model);
            await cache.PutAsync(Classification.Public, embeddedWith, new Dictionary<string, float[]> { [fresh] = vector }, DateTimeOffset.UtcNow, Ct);
            (await cache.GetAsync(Classification.Public, EngineKit.ProfileId, [fresh], Ct)).Keys.ShouldBe([fresh]);
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = $2 WHERE "Id" = $1""",
                EngineKit.ProfileId, model);
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private static (ChunkSetRecord Set, ChunkRecord Chunk) Set(SourceSeed seed, Guid document)
    {
        var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, RandomNumberGenerator.GetBytes(32)),
            seed.Classification, seed.CollectionId, RandomNumberGenerator.GetBytes(32)) { ChunkCount = 1 };
        var text = $"Text {Guid.NewGuid():N}";
        var chunk = new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, 0, new ChunkDraft
        {
            Ordinal = 0, Prefix = "x", HeadingPath = "x", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text),
            TokenCount = 4, DocumentType = "page",
        }, EngineKit.ProfileId, new float[EngineKit.Dimensions]) { DeletedAt = DateTimeOffset.UtcNow };
        return (set, chunk);
    }
}
