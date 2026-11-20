using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// B-53 (#878; data-model §8; Decisions "Deleting knowledge bases, sources and assistants"): <c>config.rag_retention_config_step</c>
/// deletes a deleted source's row only once no document of it remains and no tombstone of its blobs waits for its purge, and
/// a deleted knowledge base's row only once its last source row is gone. A live source and a live knowledge base stay.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class SourceDeletionRetentionTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_deleted_source_is_kept_while_a_document_or_a_tombstone_is_left_and_its_knowledge_base_after_its_last_source()
    {
        var (collection, sources) = await SeedAsync(3);
        var (withDocument, withTombstone, bare) = (sources[0], sources[1], sources[2]);
        var (liveCollection, liveSources) = await SeedAsync(1);
        var live = liveSources[0];

        var at = DateTimeOffset.UtcNow.AddDays(-20);
        await ExecuteAsync("""UPDATE config."Collections" SET "DeletedAt" = $2 WHERE "Id" = $1""", collection, at);
        await ExecuteAsync("""UPDATE config."Sources" SET "DeletedAt" = $2 WHERE "CollectionId" = $1""", collection, at);

        // A soft-deleted document still waits for the worker's purge; so does an upload blob tombstoned at the delete.
        var document = Guid.CreateVersion7();
        var identity = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await ExecuteAsync($"""
            INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash",
                "ContentHashVersion", "ConsecutiveFailures", "FirstSeenAt", "LastSeenAt", "DeletedAt", "PurgeAfter")
            VALUES ($1, $2, 'public', 'url', 'https://www.umea.se/{identity}', '\x{identity}', 1, 0, now(), now(), $3, $3 + interval '14 days')
            """, document, withDocument, at);
        var tombstone = $"uploads/{withTombstone:D}/{Guid.NewGuid():D}/{identity}";
        await ExecuteAsync("""INSERT INTO content."BlobTombstones" ("Key", "DeletedAt", "Reason") VALUES ($1, $2, 'source.deleted')""",
            tombstone, at);

        await StepAsync();
        (await SourceRowsAsync(bare)).ShouldBe(0);
        (await SourceRowsAsync(withDocument)).ShouldBe(1); // its document is not purged yet
        (await SourceRowsAsync(withTombstone)).ShouldBe(1); // its blob is not purged yet
        (await CollectionRowsAsync(collection)).ShouldBe(1); // two of its sources are left
        (await SourceRowsAsync(live)).ShouldBe(1);
        (await CollectionRowsAsync(liveCollection)).ShouldBe(1);

        // The worker purges the document and, after the purge delay, the blob; the next run takes both rows and then the
        // knowledge base.
        await ExecuteAsync("""DELETE FROM content."Documents" WHERE "Id" = $1""", document);
        await StepAsync();
        (await SourceRowsAsync(withDocument)).ShouldBe(0);
        (await SourceRowsAsync(withTombstone)).ShouldBe(1);
        (await CollectionRowsAsync(collection)).ShouldBe(1);

        await ExecuteAsync("""DELETE FROM content."BlobTombstones" WHERE "Key" = $1""", tombstone);
        await StepAsync();
        (await SourceRowsAsync(withTombstone)).ShouldBe(0);
        (await CollectionRowsAsync(collection)).ShouldBe(0);
        (await ScalarAsync("""SELECT count(*) FROM config."CollectionShares" WHERE "CollectionId" = $1""", collection)).ShouldBe(0);
        (await SourceRowsAsync(live)).ShouldBe(1);
        (await CollectionRowsAsync(liveCollection)).ShouldBe(1);
    }

    private async Task StepAsync()
    {
        await using var connection = new NpgsqlConnection(database.Connection(Admin));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT config.rag_retention_config_step(1000)", connection);
        await command.ExecuteScalarAsync(Ct);
    }

    private Task<long> SourceRowsAsync(Guid id) => ScalarAsync("""SELECT count(*) FROM config."Sources" WHERE "Id" = $1""", id);

    private Task<long> CollectionRowsAsync(Guid id) => ScalarAsync("""SELECT count(*) FROM config."Collections" WHERE "Id" = $1""", id);

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A knowledge base shared with a second department, with <paramref name="count"/> upload sources.</summary>
    private async Task<(Guid Collection, Guid[] Sources)> SeedAsync(int count)
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var now = DateTimeOffset.UtcNow;
        var code = Guid.NewGuid().ToString("N")[..10];
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Purge " + code, CostCenterCode = code, SyncedAt = now };
        var other = new Department { Id = Guid.CreateVersion7(), Name = "Purge other " + code, CostCenterCode = code + "o", SyncedAt = now };
        var profile = new EmbeddingProfile
        {
            Name = "purge-" + code, GatewayModel = "bge-m3", Dimensions = 3, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            UpdatedAt = now, UpdatedBy = "test",
        };
        var pipeline = new Pipeline { Name = "purge-" + code, TemplateId = "url-list", UpdatedAt = now };
        db.AddRange(department, other, profile, pipeline);
        await db.SaveChangesAsync(Ct);
        var collection = new Collection
        {
            Slug = "purge-" + code, Name = "Purge " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Id, UpdatedAt = now, UpdatedBy = "test",
        };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(Ct);
        db.CollectionShares.Add(new CollectionShare { CollectionId = collection.Id, DepartmentId = other.Id, SharedBy = "test", SharedAt = now });
        var sources = Enumerable.Range(0, count).Select(i => new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = $"Source {i}", Classification = Classification.Public,
            PipelineId = pipeline.Id, UpdatedAt = now,
        }).ToArray();
        db.Sources.AddRange(sources);
        await db.SaveChangesAsync(Ct);
        return (collection.Id, [.. sources.Select(x => x.Id)]);
    }
}
