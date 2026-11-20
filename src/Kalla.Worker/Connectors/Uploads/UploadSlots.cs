using Kalla.Pipelines.Jobs;

namespace Kalla.Worker.Connectors.Uploads;

/// <summary><c>Uploads.State</c> (connectors design §9.1).</summary>
public enum UploadState
{
    Received,
    Clean,
    Unscanned,
    Malware,
    Refused,
    Indexed,
    Deleted,
}

/// <summary>
/// A <c>content."Uploads"</c> row, one per upload slot (data-model §2.2, connectors design §9.1). <see cref="Sha256"/> is
/// lower-case hex here; the Npgsql store maps it to the <c>bytea</c> column.
/// </summary>
public sealed record UploadSlot
{
    public required Guid Id { get; init; }

    public required Guid SourceId { get; init; }

    public required string Classification { get; init; }

    /// <summary>The batch of the last change.</summary>
    public required Guid BatchId { get; init; }

    public required string FileName { get; init; }

    public string? DeclaredMediaType { get; init; }

    public string? SniffedMediaType { get; init; }

    public long Bytes { get; init; }

    public required string Sha256 { get; init; }

    public UploadState State { get; init; } = UploadState.Received;

    public string? Reason { get; init; }

    /// <summary>When the current bytes passed a malware scan; an add or a replace clears it, <c>indexed</c> keeps it (§9.1).</summary>
    public DateTimeOffset? ScannedAt { get; init; }

    public Guid? DocumentId { get; init; }

    public string? UploadedBy { get; init; }

    public DateTimeOffset UploadedAt { get; init; }

    public DateTimeOffset? ReplacedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public string Identity => UploadConnector.IdentityOf(Id);
}

/// <summary>Reads a source's upload slots (<c>content."Uploads"</c>, index on <c>SourceId</c>).</summary>
public interface IUploadSlotReader
{
    Task<IReadOnlyList<UploadSlot>> ListAsync(Guid sourceId, CancellationToken cancellationToken);
}

/// <summary>A manifest entry the sync does not apply, and why (<c>upload.duplicate</c>, <c>upload.unknown_slot</c>, <c>upload.slot_in_use</c>).</summary>
public sealed record UploadSkip(Guid UploadId, string? FileName, string Reason);

/// <param name="Writes">Slots to upsert (added, replaced, restored or deleted).</param>
/// <param name="Fetches">One <c>fetch</c> item per added, replaced or restored file (dedup <c>fetch:{sourceId}:upload:{uploadId}</c>).</param>
/// <param name="Skipped">Entries refused at sync (run items <c>skipped</c>).</param>
/// <param name="Deleted">Slots deleted by this batch: their documents are soft-deleted with the exempt reason <c>upload.deleted</c>.</param>
public sealed record UploadBatchPlan(IReadOnlyList<UploadSlot> Writes, IReadOnlyList<WorkItem> Fetches, IReadOnlyList<UploadSkip> Skipped, IReadOnlyList<UploadSlot> Deleted);

/// <summary>
/// What an upload batch does to the source's slots (connectors design §9.1, §9.3): an <c>add</c> opens a slot, unless its
/// bytes equal a live slot of the source (<c>upload.duplicate</c>, no slot) — an <c>add</c> on a deleted slot of the same
/// id is a restore; a <c>replace</c> writes new bytes into the same slot (same document, new version); a <c>delete</c>
/// closes the slot. Re-planning a batch after a crash re-emits the fetches of the slots it wrote (they carry its batch id;
/// the <c>fetch</c> dedup key absorbs the repeats).
/// </summary>
public static class UploadBatchPlanner
{
    public const string Duplicate = "upload.duplicate";
    public const string UnknownSlot = "upload.unknown_slot";
    public const string SlotInUse = "upload.slot_in_use";

    public static UploadBatchPlan Plan(UploadBatchManifest manifest, string classification, IReadOnlyList<UploadSlot> slots, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(slots);
        var byId = slots.ToDictionary(s => s.Id);
        var writes = new List<UploadSlot>();
        var fetches = new List<WorkItem>();
        var skipped = new List<UploadSkip>();
        var deleted = new List<UploadSlot>();
        foreach (var entry in manifest.Entries)
        {
            byId.TryGetValue(entry.UploadId, out var slot);
            switch (entry.Operation)
            {
                case UploadOperation.Add when slot is { DeletedAt: null } && slot.BatchId != manifest.BatchId:
                    skipped.Add(new UploadSkip(entry.UploadId, entry.FileName, SlotInUse));
                    break;
                case UploadOperation.Add:
                    var reused = slot is { BatchId: var batch } && batch == manifest.BatchId;
                    if (!reused && slot is null && byId.Values.Any(s => s.DeletedAt is null && string.Equals(s.Sha256, entry.Sha256, StringComparison.Ordinal)))
                    {
                        skipped.Add(new UploadSkip(entry.UploadId, entry.FileName, Duplicate));
                        break;
                    }

                    var added = Version(manifest, classification, entry, slot, now) with { ReplacedAt = slot?.ReplacedAt };
                    writes.Add(added);
                    byId[added.Id] = added;
                    fetches.Add(Fetch(added));
                    break;
                case UploadOperation.Replace when slot is null || slot.DeletedAt is not null:
                    skipped.Add(new UploadSkip(entry.UploadId, entry.FileName, UnknownSlot));
                    break;
                case UploadOperation.Replace:
                    var replaced = Version(manifest, classification, entry, slot, now) with { ReplacedAt = now };
                    writes.Add(replaced);
                    byId[replaced.Id] = replaced;
                    fetches.Add(Fetch(replaced));
                    break;
                case UploadOperation.Delete when slot is null:
                    skipped.Add(new UploadSkip(entry.UploadId, entry.FileName, UnknownSlot));
                    break;
                case UploadOperation.Delete:
                    var closed = slot with { BatchId = manifest.BatchId, State = UploadState.Deleted, DeletedAt = slot.DeletedAt ?? now };
                    writes.Add(closed);
                    byId[closed.Id] = closed;
                    deleted.Add(closed);
                    break;
            }
        }

        return new UploadBatchPlan(writes, fetches, skipped, deleted);
    }

    private static UploadSlot Version(UploadBatchManifest manifest, string classification, UploadManifestEntry entry, UploadSlot? slot, DateTimeOffset now) =>
        new()
        {
            Id = entry.UploadId,
            SourceId = manifest.SourceId,
            Classification = classification,
            BatchId = manifest.BatchId,
            FileName = entry.FileName ?? slot?.FileName ?? throw new InvalidDataException("An add or replace entry names its file."),
            DeclaredMediaType = entry.DeclaredMediaType,
            Bytes = entry.Bytes ?? 0,
            Sha256 = entry.Sha256 ?? throw new InvalidDataException("An add or replace entry carries its SHA-256."),
            State = UploadState.Received,
            DocumentId = slot?.DocumentId,
            UploadedBy = manifest.CommittedBy,
            UploadedAt = manifest.CommittedAt ?? now,
        };

    private static WorkItem Fetch(UploadSlot slot) =>
        new(IdentityKind.Upload, slot.Identity, null, 0, new WorkHints(Upload: new UploadHint(slot.Id, slot.Sha256, slot.FileName, slot.DeclaredMediaType)));
}
