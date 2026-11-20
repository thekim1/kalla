using System.Diagnostics.CodeAnalysis;

namespace Kalla.Domain.Ports;

/// <summary>
/// The write-once blob store (data-model §9, performance pass §3.13). A key is never overwritten: new content gets a new
/// key, and a second write of a key is success without a change (keys are content-addressed, so both writers carry the
/// same bytes). Every member is S3-safe: the file system implementation (<c>Kalla.Infrastructure/Blobs</c>) serves dev
/// and the single-host pilot, an S3 implementation the multi-host deployments (P1, to-verify #114), and both pass the
/// same contract tests (<c>tests/Kalla.ContractTests</c>). The store moves bytes only: envelope encryption of
/// <c>internal</c> blobs (data-model §7) is a layer above it, and tombstones are database rows (see
/// <see cref="DeleteAsync"/>).
/// </summary>
public interface IBlobStore
{
    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="key"/> if the key is absent. Atomic: the key becomes visible
    /// only when the whole content is stored (file system: temp file, fsync, no-overwrite link; S3: PUT with
    /// <c>If-None-Match: *</c> or a completed multipart upload). An existing key is left as it is and reported as
    /// <see cref="BlobWriteResult.AlreadyExisted"/>, also when concurrent writers race: exactly one of them creates it. A
    /// failed or cancelled write leaves no key. When <paramref name="length"/> is given, content of another length fails
    /// with <see cref="InvalidDataException"/> and leaves no key.
    /// </summary>
    Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the blob, or the bytes of <paramref name="range"/>, for reading; null when the key is absent. A range is
    /// clamped to the end of the blob, and one that starts at or after the end reads as empty.
    /// </summary>
    Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a job scratch blob (<paramref name="from"/> under <c>tmp/</c>) to <paramref name="to"/> outside <c>tmp/</c>
    /// without overwriting (file system: link, then unlink; S3: conditional server-side copy, then delete). When
    /// <paramref name="to"/> exists, <paramref name="from"/> is removed and the result is
    /// <see cref="BlobWriteResult.AlreadyExisted"/>; a retry after a completed promote reports the same. Throws
    /// <see cref="KeyNotFoundException"/> when neither key exists.
    /// </summary>
    Task<BlobWriteResult> PromoteAsync(BlobKey from, BlobKey to, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the blob now; idempotent (an absent key is success). This is the <b>purge</b>, not the deletion a user or a
    /// retention rule makes: those record a tombstone in the database, in the transaction of their own change, and the
    /// worker's purge sweep calls this once <c>Settings.BlobPurgeDelayDays</c> (35) have passed, so every Postgres restore
    /// point within the backup retention still finds its blobs (data-model §8, §9). Scratch and preview blobs are removed
    /// directly.
    /// </summary>
    Task DeleteAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every blob under <paramref name="prefix"/> (the keys that start with <c>prefix + "/"</c>). Only for the
    /// <c>tmp/</c> and <c>previews/</c> sweeps (<see cref="BlobKey.IsSweepable"/>); any other prefix throws
    /// <see cref="ArgumentException"/>.
    /// </summary>
    Task DeletePrefixAsync(BlobKey prefix, CancellationToken cancellationToken);

    /// <summary>
    /// The blobs under <paramref name="prefix"/> (the keys that start with <c>prefix + "/"</c>), in no particular order.
    /// Only for the <c>tmp/</c> and <c>previews/</c> sweeps and the weekly orphan audit, never on a hot path. Writes in
    /// progress are never listed.
    /// </summary>
    IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, CancellationToken cancellationToken);
}

/// <summary>The outcome of a write-once <see cref="IBlobStore.PutAsync"/> or <see cref="IBlobStore.PromoteAsync"/>.</summary>
public enum BlobWriteResult
{
    /// <summary>This call stored the key.</summary>
    Created,

    /// <summary>The key was already there and is unchanged: success for content-addressed keys.</summary>
    AlreadyExisted,
}

/// <summary>A stored blob as listed by <see cref="IBlobStore.ListAsync"/>.</summary>
public sealed record BlobInfo(BlobKey Key, long Length, DateTimeOffset LastModified);

/// <summary>
/// A byte range of a blob: <see cref="Length"/> bytes from <see cref="Offset"/>, or to the end when it is null (HTTP
/// <c>bytes=offset-</c>). Suffix ranges (the last n bytes) are not supported.
/// </summary>
public readonly record struct BlobRange
{
    public BlobRange(long offset, long? length = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(length));
        }

        Offset = offset;
        Length = length;
    }

    public long Offset { get; }

    public long? Length { get; }
}

/// <summary>
/// A blob key or prefix: a relative path below the container (data-model §9 table), segments separated by <c>/</c>. The
/// first segment is one of <see cref="Areas"/>. A segment is 1–255 characters of lower-case <c>a–z</c>, digits,
/// <c>-</c>, <c>_</c> and <c>.</c> and neither starts nor ends with <c>.</c> (so no <c>.</c>, <c>..</c> or hidden names);
/// there is no leading, trailing or doubled <c>/</c>, no backslash, and at most <see cref="MaxLength"/> characters (the
/// S3 key limit). Lower case only, so two keys never collide on a case-insensitive file system. A key that names a blob
/// has at least two segments (<see cref="IsBlob"/>); a one-segment key is a prefix only.
/// </summary>
public sealed record BlobKey
{
    public const int MaxLength = 1024;
    private const int MaxSegmentLength = 255;

    /// <summary>The top-level paths of data-model §9 (<c>chat-tmp</c> is P2).</summary>
    public static readonly IReadOnlySet<string> Areas = new HashSet<string>(StringComparer.Ordinal)
    {
        "uploads", "quarantine", "raw", "parse", "stages", "tmp", "chat-tmp", "previews", "crawl",
    };

    public BlobKey(string value)
    {
        if (Validate(value) is { } error)
        {
            throw new ArgumentException($"Invalid blob key '{value}': {error}.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    /// <summary>The first segment, one of <see cref="Areas"/>.</summary>
    public string Area
    {
        get
        {
            var slash = Value.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? Value : Value[..slash];
        }
    }

    /// <summary>True when the key can name a blob (two segments or more); a one-segment key is a prefix only.</summary>
    public bool IsBlob => Value.Contains('/', StringComparison.Ordinal);

    /// <summary>Job scratch (<c>tmp/</c>): the only source of <see cref="IBlobStore.PromoteAsync"/>.</summary>
    public bool IsScratch => Area == "tmp";

    /// <summary>Under <c>tmp/</c> or <c>previews/</c>: the only prefixes <see cref="IBlobStore.DeletePrefixAsync"/> takes.</summary>
    public bool IsSweepable => Area is "tmp" or "previews";

    public static bool TryCreate([NotNullWhen(true)] string? value, [NotNullWhen(true)] out BlobKey? key)
    {
        key = Validate(value) is null ? new BlobKey(value!) : null;
        return key is not null;
    }

    public override string ToString() => Value;

    private static string? Validate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "empty";
        }

        if (value.Length > MaxLength)
        {
            return $"longer than {MaxLength} characters";
        }

        var segments = value.Split('/');
        if (!Areas.Contains(segments[0]))
        {
            return $"the first segment must be one of {string.Join(", ", Areas)}";
        }

        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > MaxSegmentLength)
            {
                return "an empty or over-long segment (leading, trailing or doubled '/')";
            }

            if (segment[0] == '.' || segment[^1] == '.')
            {
                return "a segment starts or ends with '.'";
            }

            foreach (var c in segment)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
                {
                    return $"character '{c}' is not allowed (lower-case a-z, 0-9, '-', '_', '.')";
                }
            }
        }

        return null;
    }
}
