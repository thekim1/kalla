using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// A-36 (#878; Decisions "Deleting knowledge bases, sources and assistants"; data-model §8): <c>delete-source</c> cancels the
/// deleted source's running run, soft-deletes its documents and the chunks of their live sets, and deletes its upload slots
/// with a tombstone per blob; another source is untouched, a repeat finds nothing, and a source that is not deleted is
/// refused. <see cref="DeletedSourcePurge"/> removes the documents, their sets and chunks after the 14-day grace and
/// tombstones their raw blobs.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class DeleteSourceCommandTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task A_deleted_sources_content_leaves_the_index_at_once_and_is_purged_after_its_grace()
    {
        var seed = await EngineKit.SeedUploadAsync(database, Classification.Public);
        var other = await EngineKit.SeedUploadAsync(database, Classification.Public);
        var first = await CommandSeeds.DocumentAsync(database, seed, $"upload:{Guid.CreateVersion7():D}");
        var second = await CommandSeeds.DocumentAsync(database, seed, $"upload:{Guid.CreateVersion7():D}");
        var kept = await CommandSeeds.DocumentAsync(database, other, $"upload:{Guid.CreateVersion7():D}");
        var firstSet = await CommandSeeds.SetAsync(database, seed, first, "live");
        var secondSet = await CommandSeeds.SetAsync(database, seed, second, "live");
        var keptSet = await CommandSeeds.SetAsync(database, other, kept, "live");
        var slots = new[] { await SlotAsync(seed, first), await SlotAsync(seed, second) };
        await SlotAsync(other, kept);
        var run = await CommandSeeds.RunAsync(database, seed, RunStates.Running, DateTimeOffset.UtcNow);

        // Not deleted (yet): refused, nothing changes.
        (await _runner.RunAsync(new DeleteSourceCommand { SourceId = seed.SourceId }, seed.DepartmentId, Classification.Public)).Result
            .ShouldBe(new JobResult.FatalError(DeleteSourceHandler.SourceNotDeleted));
        (await _runner.RunAsync(new DeleteSourceCommand { SourceId = Guid.NewGuid() }, seed.DepartmentId, Classification.Public)).Result
            .ShouldBe(new JobResult.FatalError(DeleteSourceHandler.SourceNotFound));
        (await CommandSeeds.VisibleChunksAsync(database, firstSet)).ShouldBe(2);

        // The admin's delete sets Sources.DeletedAt and queues the command with it.
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "DeletedAt" = now() WHERE "Id" = $1""", seed.SourceId);
        var delete = await _runner.RunAsync(new DeleteSourceCommand { SourceId = seed.SourceId }, seed.DepartmentId, Classification.Public);
        delete.Result.ShouldBeOfType<JobResult.Committed>();
        var result = await CommandSeeds.ResultAsync(database, delete.Job.Id);
        (result.GetProperty("documents").GetInt32(), result.GetProperty("uploads").GetInt32()).ShouldBe((2, 2));

        // Nothing of it is searchable; the other source keeps its documents.
        (await CommandSeeds.VisibleChunksAsync(database, firstSet)).ShouldBe(0);
        (await CommandSeeds.VisibleChunksAsync(database, secondSet)).ShouldBe(0);
        (await CommandSeeds.VisibleChunksAsync(database, keptSet)).ShouldBe(2);
        (await Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Documents"
             WHERE "SourceId" = $1 AND "DeletedAt" IS NOT NULL AND "PurgeAfter" BETWEEN now() + interval '13 days' AND now() + interval '15 days'
            """, seed.SourceId)).ShouldBe(2);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Documents" WHERE "Id" = $1 AND "DeletedAt" IS NULL""", kept))
            .ShouldBe(1);
        (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."Runs" WHERE "Id" = $1""", run)).ShouldBe(RunStates.Cancelled);

        // Its upload slots are deleted, each blob tombstoned for the delayed purge.
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Uploads" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL""",
            seed.SourceId)).ShouldBe(0);
        foreach (var (slot, sha) in slots)
        {
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Reason" FROM content."BlobTombstones" WHERE "Key" = $1""",
                $"uploads/{seed.SourceId:D}/{slot:D}/{Convert.ToHexStringLower(sha)}")).ShouldBe(DeleteSourceHandler.TombstoneReason);
        }

        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Uploads" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL""",
            other.SourceId)).ShouldBe(1);

        // A repeat (a retried job) finds nothing left.
        var repeat = await _runner.RunAsync(new DeleteSourceCommand { SourceId = seed.SourceId }, seed.DepartmentId, Classification.Public);
        (await CommandSeeds.ResultAsync(database, repeat.Job.Id)).GetProperty("documents").GetInt32().ShouldBe(0);

        // The purge: nothing within the grace; after it, the documents with their sets and chunks, and their raw blobs tombstoned.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(13));
        (await new DeletedSourcePurge(database.Database(RagDatabase.Worker), clock).RunAsync(Ct)).ShouldBe(0);
        var hash = await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", first);
        clock.Advance(TimeSpan.FromDays(2));
        (await new DeletedSourcePurge(database.Database(RagDatabase.Worker), clock).RunAsync(Ct)).ShouldBe(2);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId))
            .ShouldBe(0);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ChunkSets" WHERE "Id" = ANY($1)""",
            new[] { firstSet, secondSet })).ShouldBe(0);
        (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Reason" FROM content."BlobTombstones" WHERE "Key" = $1""",
            $"raw/{Classification.Public}/{Convert.ToHexStringLower(hash)}")).ShouldBe(DeletedSourcePurge.TombstoneReason);
        (await CommandSeeds.VisibleChunksAsync(database, keptSet)).ShouldBe(2);
    }

    /// <summary>A live upload slot of the source for the document; returns its id and hash.</summary>
    private async Task<(Guid Slot, byte[] Sha)> SlotAsync(SourceSeed seed, Guid document)
    {
        var slot = Guid.CreateVersion7();
        var sha = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Uploads" ("Id", "SourceId", "Classification", "BatchId", "FileName", "Bytes", "Sha256", "State", "DocumentId", "UploadedAt")
            VALUES ($1, $2, $3, $4, 'a.md', 10, $5, 'indexed', $6, now())
            """, slot, seed.SourceId, seed.Classification, Guid.CreateVersion7(), sha, document);
        return (slot, sha);
    }
}
