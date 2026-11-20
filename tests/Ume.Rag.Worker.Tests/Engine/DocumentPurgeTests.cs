using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Tests.CommandJobs;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// A-37 (data-model §8; connectors design §13.4): a document that a live source no longer has is soft-deleted, and
/// <see cref="DocumentPurge"/> deletes it with its chunk sets and chunks once its 14-day grace has passed, not before. A
/// document seen again within the grace is revived and kept. A purged document's raw blobs are tombstoned, unless another
/// document of the level has the same content.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class DocumentPurgeTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(database.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_document_removed_from_a_live_source_is_purged_after_its_grace_unless_seen_again()
    {
        var seed = await EngineKit.SeedUploadAsync(database, Classification.Public);
        const string revivedUrl = "https://site.test/revived";
        var removed = await CommandSeeds.DocumentAsync(database, seed, "https://site.test/removed");
        var revived = await CommandSeeds.DocumentAsync(database, seed, revivedUrl);
        var twin = await CommandSeeds.DocumentAsync(database, seed, "https://site.test/twin");
        var kept = await CommandSeeds.DocumentAsync(database, seed, "https://site.test/kept");
        // The twin has the content of a document the source still has.
        await Sql.ExecuteAsync(database.Connection(null), """
            UPDATE content."Documents" SET "ContentHash" = (SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $2) WHERE "Id" = $1
            """, twin, kept);
        var removedSet = await CommandSeeds.SetAsync(database, seed, removed, "live");
        var revivedSet = await CommandSeeds.SetAsync(database, seed, revived, "live");
        var keptSet = await CommandSeeds.SetAsync(database, seed, kept, "live");
        var run = await CommandSeeds.RunAsync(database, seed, RunStates.Running, DateTimeOffset.UtcNow);
        var removedHash = await HashAsync(removed);
        var revivedHash = await HashAsync(revived);
        var twinHash = await HashAsync(twin);

        // The source no longer has three of them: they leave the index at once and wait out their grace.
        var now = DateTimeOffset.UtcNow;
        (await new IndexWriter(database.Database(RagDatabase.Worker)).SoftDeleteDocumentsAsync([removed, revived, twin], now, Ct)).ShouldBe(3);
        (await CommandSeeds.VisibleChunksAsync(database, removedSet)).ShouldBe(0);
        (await CommandSeeds.VisibleChunksAsync(database, revivedSet)).ShouldBe(0);

        // Seen again on day 13 with the same content: revived without re-processing, its chunks back in the index.
        var store = new DocumentStore(database.Database(RagDatabase.Worker));
        var upsert = await store.UpsertFetchedAsync(
            new FetchedDocument(seed.SourceId, seed.Classification, "url", revivedUrl, revivedHash, 1, revivedUrl, null, null), run, now.AddDays(13), Ct);
        upsert.ContentChanged.ShouldBeFalse();
        upsert.Document.DeletedAt.ShouldBeNull();
        (await CommandSeeds.VisibleChunksAsync(database, revivedSet)).ShouldBe(2);

        // Within the grace nothing is purged.
        var clock = new FakeTimeProvider(now.AddDays(13));
        await Purge(clock).RunAsync(Ct);
        (await DocumentsAsync(removed, revived, twin, kept)).ShouldBe(4);
        (await TombstonesAsync(removedHash)).ShouldBe(0);

        // After it the removed documents go with their sets and chunks; the revived one and the live one stay.
        clock.Advance(TimeSpan.FromDays(2));
        (await Purge(clock).RunAsync(Ct)).Purged.ShouldBeGreaterThanOrEqualTo(2);
        (await DocumentsAsync(removed, twin)).ShouldBe(0);
        (await DocumentsAsync(revived, kept)).ShouldBe(2);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ChunkSets" WHERE "Id" = $1""", removedSet)).ShouldBe(0);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Chunks" WHERE "DocumentId" = $1""", removed)).ShouldBe(0);
        (await CommandSeeds.VisibleChunksAsync(database, revivedSet)).ShouldBe(2);
        (await CommandSeeds.VisibleChunksAsync(database, keptSet)).ShouldBe(2);

        // The removed document's raw blob and links are tombstoned; the twin's content is the live document's, so not its.
        foreach (var suffix in new[] { "", ".links.json.gz" })
        {
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Reason" FROM content."BlobTombstones" WHERE "Key" = $1""",
                RawKey(removedHash) + suffix)).ShouldBe(DocumentPurge.TombstoneReason);
        }

        (await TombstonesAsync(twinHash)).ShouldBe(0);
        (await TombstonesAsync(revivedHash)).ShouldBe(0);

        // A second run finds nothing more of this source.
        await Purge(clock).RunAsync(Ct);
        (await DocumentsAsync(revived, kept)).ShouldBe(2);
    }

    private DocumentPurge Purge(TimeProvider clock) => new(database.Database(RagDatabase.Worker), database.Queue(RagDatabase.Worker), clock);

    private static string RawKey(byte[] hash) => $"raw/{Classification.Public}/{Convert.ToHexStringLower(hash)}";

    private Task<byte[]> HashAsync(Guid document) =>
        Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", document);

    private Task<long> DocumentsAsync(params Guid[] documents) =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Documents" WHERE "Id" = ANY($1)""", documents);

    private Task<long> TombstonesAsync(byte[] hash) =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."BlobTombstones" WHERE "Key" LIKE $1 || '%'""", RawKey(hash));
}
