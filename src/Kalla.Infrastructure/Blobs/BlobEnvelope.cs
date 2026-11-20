using System.Security.Cryptography;
using Kalla.Domain.Ports;

namespace Kalla.Infrastructure.Blobs;

/// <summary>
/// Envelope encryption of blobs (data-model §7 "Blobs"): a level whose policy names a blob master key
/// (<see cref="IBlobKeyPolicy"/>; <c>internal</c>: <c>rag-blob-internal</c>) stores its blobs as <c>URB2</c>
/// (<see cref="Urb2"/>) under a fresh data key wrapped into the blob's header; <c>public</c> blobs are stored as they are.
/// kalla-api's citation download (<c>GET /v1/documents/{id}/content</c>, knowledge design §3.5) reads through
/// <see cref="OpenReadAsync"/>; the worker reaches it through <see cref="EnvelopeBlobStore"/>.
/// </summary>
public interface IBlobEnvelope
{
    /// <summary>
    /// Opens the blob as plaintext; null when the key is absent. A level without a blob master key (<c>public</c>) is
    /// streamed as stored. Otherwise the blob must be <c>URB2</c>: its data key is unwrapped with the master key its header
    /// names, and each 64 KiB segment is verified before any of its bytes are returned. A blob without the header, a
    /// failing tag, reordered segments or a missing final segment throw <see cref="BlobIntegrityException"/> (on open or
    /// on the read that reaches the damage, so a response may already have started); a missing or wrong master key throws
    /// <see cref="KeyProviderException"/>. The caller disposes <see cref="PlaintextBlob.Content"/>.
    /// </summary>
    Task<PlaintextBlob?> OpenReadAsync(BlobKey key, string level, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="key"/> as <see cref="IBlobStore.PutAsync"/> does, encrypted
    /// when <paramref name="level"/> has a blob master key; <paramref name="length"/> is the plaintext length.
    /// </summary>
    Task<BlobWriteResult> PutAsync(BlobKey key, string level, Stream content, long? length, CancellationToken cancellationToken);
}

/// <summary>
/// A blob's plaintext and its length (for <c>internal</c> blobs computed from the stored length and the segment count).
/// </summary>
public sealed record PlaintextBlob(Stream Content, long Length);

/// <summary>A stored blob is not what its level requires, or it was changed, reordered or cut.</summary>
public sealed class BlobIntegrityException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The <see cref="IBlobEnvelope"/> over the plain store <paramref name="store"/>.</summary>
public sealed class BlobEnvelope(IBlobStore store, IKeyProvider keys, IBlobKeyPolicy policy) : IBlobEnvelope
{
    public async Task<PlaintextBlob?> OpenReadAsync(BlobKey key, string level, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var blob = await store.OpenReadAsync(key, null, cancellationToken);
        if (blob is null)
        {
            return null;
        }

        long length;
        try
        {
            length = blob.Length;
        }
        catch (NotSupportedException e)
        {
            await blob.DisposeAsync();
            throw new InvalidOperationException($"The blob store returned no length for '{key}'; the envelope needs it.", e);
        }

        if (await policy.MasterKeyNameAsync(level, cancellationToken) is null)
        {
            return new PlaintextBlob(blob, length);
        }

        var plaintext = await DecryptingStream.OpenAsync(blob, key, keys, cancellationToken);
        try
        {
            return new PlaintextBlob(plaintext, Urb2.PlaintextLength(key, length, plaintext.HeaderLength));
        }
        catch
        {
            await plaintext.DisposeAsync();
            throw;
        }
    }

    public async Task<BlobWriteResult> PutAsync(BlobKey key, string level, Stream content, long? length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        if (await policy.MasterKeyNameAsync(level, cancellationToken) is not { } keyName)
        {
            return await store.PutAsync(key, content, length, cancellationToken);
        }

        if (length is { } declared)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(declared, nameof(length));
        }

        var dataKey = RandomNumberGenerator.GetBytes(Urb2.DataKeySize);
        try
        {
            var wrapped = await keys.WrapAsync(keyName, dataKey, cancellationToken);
            var noncePrefix = RandomNumberGenerator.GetBytes(Urb2.NoncePrefixSize);
            var header = Urb2.Header(wrapped, noncePrefix);
            await using var sealedContent = new EncryptingStream(content, dataKey, header, noncePrefix, length, key);
            var stored = length is { } plain ? Urb2.CiphertextLength(header.Length, plain) : (long?)null;
            return await store.PutAsync(key, sealedContent, stored, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// The plaintext of <paramref name="key"/> at <paramref name="level"/> as <see cref="IBlobStore.OpenReadAsync"/> gives
    /// it: a range of an encrypted blob is reached by verifying and skipping the segments before it.
    /// </summary>
    internal async Task<Stream?> OpenStreamAsync(BlobKey key, string level, BlobRange? range, CancellationToken cancellationToken)
    {
        if (await policy.MasterKeyNameAsync(level, cancellationToken) is null)
        {
            return await store.OpenReadAsync(key, range, cancellationToken);
        }

        if (await store.OpenReadAsync(key, null, cancellationToken) is not { } blob)
        {
            return null;
        }

        var plaintext = await DecryptingStream.OpenAsync(blob, key, keys, cancellationToken);
        if (range is not { } r)
        {
            return plaintext;
        }

        try
        {
            await plaintext.SkipAsync(r.Offset, cancellationToken);
        }
        catch
        {
            await plaintext.DisposeAsync();
            throw;
        }

        return r.Length is { } count ? new RangeReadStream(plaintext, count) : plaintext;
    }

    /// <summary>Whether blobs of <paramref name="level"/> are encrypted.</summary>
    internal async Task<bool> EncryptsAsync(string level, CancellationToken cancellationToken) =>
        await policy.MasterKeyNameAsync(level, cancellationToken) is not null;
}
