using System.Security.Cryptography;
using System.Text;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Engine;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// An upload slot follows the run item that ends the processing of its bytes (connectors design §9.1, #533), in the item's
/// commit (<see cref="EngineCommit.AddRunItemAsync"/>): <c>indexed</c> with its document when the live pointer names a set
/// (live or held) of the slot's bytes, <c>refused</c> or <c>malware</c> with the reason when they ended without one; an
/// item about other bytes (a replace in between), a superseded item and a deleted slot leave the slot as it is.
/// <c>indexed</c> keeps the slot's <c>ScannedAt</c>; the fetch's mark (<c>clean</c> with it, <c>unscanned</c> without)
/// applies only to the bytes it scanned.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class UploadSlotStateTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SourceSeed _seed = null!;
    private Guid _run;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _seed = await EngineKit.SeedUploadAsync(database);
        _run = await CommandSeeds.RunAsync(database, _seed, RunStates.Running, DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(ChunkSetStates.Live)]
    [InlineData(ChunkSetStates.Held)]
    public async Task A_set_of_the_slots_bytes_going_live_or_held_marks_it_indexed_with_its_document(string state)
    {
        var (slot, bytes) = await AddSlotAsync(UploadStates.Clean, "parser.timeout");
        var document = await DocumentAsync(slot, bytes);
        await CommandSeeds.SetAsync(database, _seed, document, state);

        await ItemAsync(slot, document, Outcome.Added, state == ChunkSetStates.Held ? "pii.held" : null, bytes);

        (await ReadSlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, document));
    }

    [Theory]
    [InlineData(Outcome.Encrypted, "parser.encrypted")]
    [InlineData(Outcome.Skipped, "parse.empty")]
    [InlineData(Outcome.Failed, "embed.input_missing")]
    public async Task Processing_that_ends_without_a_set_marks_the_slot_refused_with_the_reason(Outcome outcome, string reason)
    {
        var (slot, bytes) = await AddSlotAsync(UploadStates.Clean);
        var document = await DocumentAsync(slot, bytes);

        await ItemAsync(slot, document, outcome, reason, bytes);

        (await ReadSlotAsync(slot)).ShouldBe((UploadStates.Refused, reason, document));
    }

    [Fact]
    public async Task Indexed_keeps_the_time_the_bytes_passed_the_scan()
    {
        var (slot, bytes) = await AddSlotAsync(UploadStates.Clean);
        var scannedAt = new DateTime(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Uploads" SET "ScannedAt" = $2 WHERE "Id" = $1""", slot, scannedAt);
        var document = await DocumentAsync(slot, bytes);
        await CommandSeeds.SetAsync(database, _seed, document, ChunkSetStates.Live);

        await ItemAsync(slot, document, Outcome.Added, null, bytes);

        (await ReadSlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, document));
        (await ScannedAtAsync(slot)).ShouldBe(scannedAt);
    }

    [Fact]
    public async Task The_fetch_marks_only_the_bytes_it_scanned_clean_with_their_time_or_unscanned_without_one()
    {
        var store = new PostgresUploadStore(database.Database(RagDatabase.Worker));
        var (slot, bytes) = await AddSlotAsync(UploadStates.Received);
        var scannedAt = new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);

        // A fetch of bytes a replace has since swapped out leaves the slot alone.
        await store.SetStateAsync(slot, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), UploadState.Clean, scannedAt, null, null, null, Ct);
        (await ReadSlotAsync(slot)).State.ShouldBe(UploadStates.Received);
        (await ScannedAtAsync(slot)).ShouldBeNull();

        await store.SetStateAsync(slot, Convert.ToHexStringLower(bytes), UploadState.Clean, scannedAt, null, null, null, Ct);
        (await ReadSlotAsync(slot)).State.ShouldBe(UploadStates.Clean);
        (await ScannedAtAsync(slot)).ShouldBe(scannedAt.UtcDateTime);

        var (unscanned, unscannedBytes) = await AddSlotAsync(UploadStates.Received);
        await store.SetStateAsync(unscanned, Convert.ToHexStringLower(unscannedBytes), UploadState.Unscanned, null, null, null, null, Ct);
        (await ReadSlotAsync(unscanned)).State.ShouldBe(UploadStates.Unscanned);
        (await ScannedAtAsync(unscanned)).ShouldBeNull();
        (await store.ListAsync(_seed.SourceId, Ct)).Single(s => s.Id == unscanned).State.ShouldBe(UploadState.Unscanned);
    }

    [Fact]
    public async Task A_finding_without_a_document_marks_the_slot_malware()
    {
        var (slot, bytes) = await AddSlotAsync(UploadStates.Received);

        await ItemAsync(slot, null, Outcome.Malware, "malware.found", bytes);

        (await ReadSlotAsync(slot)).ShouldBe((UploadStates.Malware, "malware.found", null));
    }

    [Fact]
    public async Task A_replace_still_processing_is_not_marked_by_the_old_bytes_set_or_failure()
    {
        var (slot, newBytes) = await AddSlotAsync(UploadStates.Clean);
        var oldBytes = RandomNumberGenerator.GetBytes(32);
        var document = await DocumentAsync(slot, oldBytes);
        await CommandSeeds.SetAsync(database, _seed, document, ChunkSetStates.Live);

        await ItemAsync(slot, document, Outcome.Updated, null, oldBytes);
        await ItemAsync(slot, document, Outcome.Failed, "embed.input_missing", oldBytes);

        (await ReadSlotAsync(slot)).ShouldBe((UploadStates.Clean, null, null));
        (await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "Sha256" FROM content."Uploads" WHERE "Id" = $1""", slot)).ShouldBe(newBytes);
    }

    [Fact]
    public async Task New_bytes_that_fail_while_the_old_set_stays_live_are_refused_and_a_failed_reprocess_of_indexed_bytes_stays_indexed()
    {
        var (replaced, newBytes) = await AddSlotAsync(UploadStates.Clean);
        var replacedDocument = await DocumentAsync(replaced, RandomNumberGenerator.GetBytes(32));
        await CommandSeeds.SetAsync(database, _seed, replacedDocument, ChunkSetStates.Live);
        var (indexed, bytes) = await AddSlotAsync(UploadStates.Indexed);
        var indexedDocument = await DocumentAsync(indexed, bytes);
        await CommandSeeds.SetAsync(database, _seed, indexedDocument, ChunkSetStates.Live);

        await ItemAsync(replaced, replacedDocument, Outcome.Failed, "parser.timeout", newBytes);
        await ItemAsync(indexed, indexedDocument, Outcome.Failed, "parser.timeout", bytes);

        (await ReadSlotAsync(replaced)).ShouldBe((UploadStates.Refused, "parser.timeout", replacedDocument));
        (await ReadSlotAsync(indexed)).ShouldBe((UploadStates.Indexed, null, indexedDocument));
    }

    [Fact]
    public async Task A_superseded_item_an_item_without_bytes_and_a_deleted_slot_leave_the_slot_alone()
    {
        var (slot, bytes) = await AddSlotAsync(UploadStates.Clean);
        var document = await DocumentAsync(slot, bytes);
        await ItemAsync(slot, document, Outcome.Superseded, "execution.superseded", bytes);
        await ItemAsync(slot, document, Outcome.Failed, "parser.timeout", null);
        (await ReadSlotAsync(slot)).ShouldBe((UploadStates.Clean, null, null));

        var (deleted, deletedBytes) = await AddSlotAsync(UploadStates.Deleted, deleted: true);
        var deletedDocument = await DocumentAsync(deleted, deletedBytes);
        await CommandSeeds.SetAsync(database, _seed, deletedDocument, ChunkSetStates.Live);
        await ItemAsync(deleted, deletedDocument, Outcome.Unchanged, null, deletedBytes);
        (await ReadSlotAsync(deleted)).ShouldBe((UploadStates.Deleted, null, null));
    }

    private async Task<(Guid Slot, byte[] Bytes)> AddSlotAsync(string state, string? reason = null, bool deleted = false)
    {
        var (slot, bytes) = (Guid.CreateVersion7(), RandomNumberGenerator.GetBytes(32));
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Uploads" ("Id", "SourceId", "Classification", "BatchId", "FileName", "Bytes", "Sha256", "State", "Reason", "UploadedAt", "DeletedAt")
            VALUES ($1, $2, $3, $4, $5, 100, $6, $7, $8, now(), CASE WHEN $9 THEN now() END)
            """, slot, _seed.SourceId, _seed.Classification, Guid.CreateVersion7(), $"{slot:N}.md", bytes, state, reason ?? (object)DBNull.Value, deleted);
        return (slot, bytes);
    }

    private async Task<Guid> DocumentAsync(Guid slot, byte[] contentHash)
    {
        var (id, identity) = (Guid.CreateVersion7(), UploadConnector.IdentityOf(slot));
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash", "ContentHash",
                                            "ContentHashVersion", "DisplayName", "MediaType", "CurrentExecutionId", "FirstSeenAt", "LastSeenAt")
            VALUES ($1, $2, $3, 'upload', $4, $5, $6, 1, $7, 'text/markdown', $8, now(), now())
            """, id, _seed.SourceId, _seed.Classification, identity, SHA256.HashData(Encoding.UTF8.GetBytes(identity)), contentHash, $"{slot:N}.md",
            Guid.CreateVersion7());
        return id;
    }

    private async Task ItemAsync(Guid slot, Guid? document, Outcome outcome, string? reason, byte[]? contentHash)
    {
        await using var commit = await EngineCommit.BeginAsync(database.Database(RagDatabase.Worker), Ct);
        await commit.AddRunItemAsync(new RunItemRow(_run, _seed.Classification, document, UploadConnector.IdentityOf(slot), outcome)
        {
            Reason = reason,
            ContentHash = contentHash,
        }, Ct);
        await commit.CommitAsync(Ct);
    }

    private Task<DateTime?> ScannedAtAsync(Guid slot) =>
        Sql.ScalarAsync<DateTime?>(database.Connection(null), """SELECT "ScannedAt" FROM content."Uploads" WHERE "Id" = $1""", slot);

    private async Task<(string State, string? Reason, Guid? Document)> ReadSlotAsync(Guid slot)
    {
        var row = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT "State" || '|' || coalesce("Reason", '') || '|' || coalesce("DocumentId"::text, '') FROM content."Uploads" WHERE "Id" = $1
            """, slot);
        var parts = row.Split('|');
        return (parts[0], parts[1].Length == 0 ? null : parts[1], parts[2].Length == 0 ? null : Guid.Parse(parts[2]));
    }
}
