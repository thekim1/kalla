using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Kalla.Pipelines.Schemas;

namespace Kalla.Pipelines.Jobs;

/// <summary>What an upload batch entry does to its slot (connectors design §9.1, §9.3).</summary>
public enum UploadOperation
{
    /// <summary>A new slot; on a deleted slot of the same id, a restore.</summary>
    Add,

    /// <summary>New bytes in the same slot: same document, new version.</summary>
    Replace,

    /// <summary>The slot's document is soft-deleted (<c>upload.deleted</c>, exempt from the guard).</summary>
    Delete,
}

/// <summary>
/// The batch manifest <c>uploads/{sourceId}/batches/{batchId}.json</c> (connectors design §9.1): written by the admin API at
/// commit, read by the worker's <c>sync</c> (<c>trigger</c> <c>upload</c>, dedup <c>sync-upload:{batchId}</c>). Each added or
/// replaced file is already stored at <c>uploads/{sourceId}/{uploadId}/{sha256}</c>; the admin allocates <c>uploadId</c>
/// (v7) for an add.
/// </summary>
[Description("Upload batch manifest uploads/{sourceId}/batches/{batchId}.json (connectors design §9.1), written by the admin at commit and read by the worker's sync.")]
public sealed record UploadBatchManifest
{
    public required Guid BatchId { get; init; }

    public required Guid SourceId { get; init; }

    [Description("At most 100 files per batch (admin design §4.5); deletes count too.")]
    [MinLength(1)]
    [MaxLength(100)]
    public required IReadOnlyList<UploadManifestEntry> Entries { get; init; }

    [Description("The admin's audit actor who committed the batch (Uploads.UploadedBy).")]
    [MaxLength(200)]
    public string? CommittedBy { get; init; }

    public DateTimeOffset? CommittedAt { get; init; }
}

/// <summary>One file of a batch, or a delete.</summary>
[SchemaKeyword("allOf", """
    [
      { "if": { "properties": { "operation": { "enum": ["add", "replace"] } }, "required": ["operation"] }, "then": { "required": ["fileName", "bytes", "sha256"] } }
    ]
    """)]
public sealed record UploadManifestEntry
{
    public required UploadOperation Operation { get; init; }

    [Description("The upload slot (content.Uploads.Id): the document identity is upload:<uploadId>.")]
    public required Guid UploadId { get; init; }

    [Description("The file name as uploaded: the document's DisplayName, never its Title.")]
    [MinLength(1)]
    [MaxLength(300)]
    public string? FileName { get; init; }

    [Description("The Content-Type the browser declared; the worker's sniffed type wins (connectors design §9.2).")]
    [MaxLength(200)]
    public string? DeclaredMediaType { get; init; }

    [Range(0, 52_428_800)]
    public long? Bytes { get; init; }

    [Description("SHA-256 (hex) computed while streaming; the blob key's last segment.")]
    [RegularExpression(ContractPatterns.Sha256Hex)]
    public string? Sha256 { get; init; }
}
