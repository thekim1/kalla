using System.Runtime.CompilerServices;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Infrastructure.Blobs;

/// <summary>A blob store that can give a view whose level-less content keys belong to one level.</summary>
public interface IBlobLevelScope
{
    /// <summary>This store, with the content keys that name no level (see <see cref="EnvelopeBlobStore"/>) at <paramref name="level"/>.</summary>
    IBlobStore ForLevel(string level);
}

public static class BlobLevelScopes
{
    /// <summary>
    /// <paramref name="store"/> scoped to <paramref name="level"/> (<see cref="IBlobLevelScope.ForLevel"/>); a store without
    /// the envelope (a plain store in tests and tools) is returned as it is.
    /// </summary>
    public static IBlobStore ForLevel(this IBlobStore store, string level) =>
        store is IBlobLevelScope scope ? scope.ForLevel(level) : store;
}

/// <summary>
/// The worker's <see cref="IBlobStore"/>: the plain store with envelope encryption (<see cref="BlobEnvelope"/>) by the level
/// the key belongs to. The data-model §9 layout names the level in <c>raw/{level}/…</c>, <c>parse/{level}/…</c> and
/// <c>stages/{level}/…</c>, also below a preview's prefix (<c>previews/{jobId}/raw/{level}/…</c>). Uploads
/// (<c>uploads/{sourceId}/{slot}/{sha256}</c>, written by the admin through <see cref="IBlobEnvelope.PutAsync"/>),
/// quarantined files (<c>quarantine/…</c>) and a preview's own outputs (<c>previews/{jobId}/…</c> otherwise) name none:
/// they belong to the level of the view (<see cref="ForLevel"/>, the source's level), and without one they are refused
/// rather than read or stored in plaintext. Everything else (upload batch manifests; robots.txt; scratch) is stored as it
/// is. Existence, deletes and listings pass through. A promote into an encrypted level is refused, because a scratch blob
/// is not encrypted; a promote to an upload key passes through, because the admin stages uploads through the envelope.
/// </summary>
public sealed class EnvelopeBlobStore : IBlobStore, IBlobLevelScope
{
    private readonly BlobEnvelope _envelope;
    private readonly IBlobStore _inner;
    private readonly string? _level;

    public EnvelopeBlobStore(BlobEnvelope envelope, IBlobStore inner)
        : this(envelope, inner, null)
    {
    }

    private EnvelopeBlobStore(BlobEnvelope envelope, IBlobStore inner, string? level)
    {
        _envelope = envelope;
        _inner = inner;
        _level = level;
    }

    public IBlobStore ForLevel(string level)
    {
        if (!Classification.IsValid(level))
        {
            throw new ArgumentException($"'{level}' is not a level.", nameof(level));
        }

        return new EnvelopeBlobStore(_envelope, _inner, level);
    }

    public Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, CancellationToken cancellationToken) =>
        LevelOf(key) is { } level
            ? _envelope.PutAsync(key, level, content, length, cancellationToken)
            : _inner.PutAsync(key, content, length, cancellationToken);

    public Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken cancellationToken) =>
        LevelOf(key) is { } level
            ? _envelope.OpenStreamAsync(key, level, range, cancellationToken)
            : _inner.OpenReadAsync(key, range, cancellationToken);

    public Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken) => _inner.ExistsAsync(key, cancellationToken);

    public async Task<BlobWriteResult> PromoteAsync(BlobKey from, BlobKey to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(to);
        // The admin stages an upload through the envelope and promotes it unchanged: the header, not the key, is the
        // segments' associated data (Urb2).
        if (IsUpload(to.Value.Split('/')))
        {
            return await _inner.PromoteAsync(from, to, cancellationToken);
        }

        if (LevelOf(to) is { } level && await _envelope.EncryptsAsync(level, cancellationToken))
        {
            throw new InvalidOperationException($"'{to}' is envelope-encrypted ({level}); write it with PutAsync, not by promoting a scratch blob.");
        }

        return await _inner.PromoteAsync(from, to, cancellationToken);
    }

    public Task DeleteAsync(BlobKey key, CancellationToken cancellationToken) => _inner.DeleteAsync(key, cancellationToken);

    public Task DeletePrefixAsync(BlobKey prefix, CancellationToken cancellationToken) => _inner.DeletePrefixAsync(prefix, cancellationToken);

    public async IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in _inner.ListAsync(prefix, cancellationToken))
        {
            yield return blob;
        }
    }

    /// <summary>The level whose encryption rule applies to <paramref name="key"/>; null for the areas stored as they are.</summary>
    private string? LevelOf(BlobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var segments = key.Value.Split('/');
        var preview = segments[0] == "previews";
        var first = preview ? 2 : 0;
        if (segments.Length > first + 2 && segments[first] is "raw" or "parse" or "stages")
        {
            var level = segments[first + 1];
            return Classification.IsValid(level) ? level : throw new ArgumentException($"Blob key '{key}' names no level.", nameof(key));
        }

        if (preview || segments[0] == "quarantine" || IsUpload(segments))
        {
            return _level ?? throw new InvalidOperationException(
                $"Blob key '{key}' names no level; use the store's ForLevel view, so its content is encrypted by its level.");
        }

        return null;
    }

    /// <summary>An upload blob <c>uploads/{sourceId}/{slot}/{sha256}</c>, not a batch manifest <c>uploads/{sourceId}/batches/…</c>.</summary>
    private static bool IsUpload(string[] segments) => segments is ["uploads", _, not "batches", _];
}
