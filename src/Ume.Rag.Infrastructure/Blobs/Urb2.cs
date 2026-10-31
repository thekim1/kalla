using System.Buffers.Binary;
using System.Text;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Infrastructure.Blobs;

/// <summary>
/// The <c>URB2</c> blob envelope (data-model §7 "Blobs"). Header: <c>URB2</c> ‖ key name length (1 byte) ‖ key name
/// (ASCII) ‖ key version length (1 byte) ‖ key version (ASCII) ‖ wrapped data key length (2 bytes, big-endian) ‖ wrapped
/// data key ‖ 7-byte nonce prefix. Then the plaintext in <see cref="SegmentSize"/> segments, each AES-256-GCM under the
/// per-blob data key with its 16-byte tag appended (STREAM construction): the nonce is the prefix ‖ the segment number (4
/// bytes, big-endian) ‖ 1 on the final segment and 0 before it, and the whole header is the associated data of every
/// segment. Every segment but the final one is full; the final one holds 0 to <see cref="SegmentSize"/> bytes (0 only for
/// empty content). Reordered segments fail by their number, a cut at a segment boundary by the final flag.
/// </summary>
internal static class Urb2
{
    public const int SegmentSize = 64 * 1024;
    public const int TagSize = 16;
    public const int DataKeySize = 32;
    public const int NoncePrefixSize = 7;
    public const int NonceSize = 12;
    public const int MaxWrappedKeyLength = 4096;

    public static ReadOnlySpan<byte> Magic => "URB2"u8;

    /// <summary>The header for a data key wrapped as <paramref name="wrapped"/> and the segment nonce prefix.</summary>
    public static byte[] Header(WrappedKey wrapped, ReadOnlySpan<byte> noncePrefix)
    {
        var name = Ascii(wrapped.KeyName, "key name");
        var version = Ascii(wrapped.KeyVersion, "key version");
        if (wrapped.Ciphertext.Length is 0 or > MaxWrappedKeyLength)
        {
            throw new ArgumentException($"A wrapped data key of {wrapped.Ciphertext.Length} bytes does not fit the URB2 header.", nameof(wrapped));
        }

        using var header = new MemoryStream();
        header.Write(Magic);
        header.WriteByte((byte)name.Length);
        header.Write(name);
        header.WriteByte((byte)version.Length);
        header.Write(version);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)wrapped.Ciphertext.Length);
        header.Write(length);
        header.Write(wrapped.Ciphertext);
        header.Write(noncePrefix);
        return header.ToArray();
    }

    /// <summary>
    /// Reads the header from the start of <paramref name="blob"/>: the header bytes (the segments' associated data), the
    /// wrapped data key and the nonce prefix. A blob that does not start with <c>URB2</c> or whose header is cut or
    /// malformed throws <see cref="BlobIntegrityException"/>.
    /// </summary>
    public static async Task<(byte[] Header, WrappedKey Wrapped, byte[] NoncePrefix)> ReadHeaderAsync(Stream blob, BlobKey key, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var magic = await ReadAsync(blob, header, Magic.Length, key, cancellationToken);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new BlobIntegrityException($"Blob '{key}' is not envelope-encrypted (no URB2 header), but its level requires it.");
        }

        var name = await ReadStringAsync(blob, header, key, cancellationToken);
        var version = await ReadStringAsync(blob, header, key, cancellationToken);
        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(blob, header, 2, key, cancellationToken));
        if (wrappedLength is 0 or > MaxWrappedKeyLength)
        {
            throw new BlobIntegrityException($"Blob '{key}': the URB2 header names a wrapped data key of {wrappedLength} bytes.");
        }

        var wrapped = await ReadAsync(blob, header, wrappedLength, key, cancellationToken);
        var prefix = await ReadAsync(blob, header, NoncePrefixSize, key, cancellationToken);
        return (header.ToArray(), new WrappedKey(name, version, wrapped), prefix);
    }

    /// <summary>The nonce of segment <paramref name="counter"/>.</summary>
    public static void Nonce(ReadOnlySpan<byte> prefix, uint counter, bool final, Span<byte> nonce)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(NoncePrefixSize, 4), counter);
        nonce[NonceSize - 1] = final ? (byte)1 : (byte)0;
    }

    /// <summary>The stored length of <paramref name="plaintextLength"/> bytes behind a header of <paramref name="headerLength"/>.</summary>
    public static long CiphertextLength(int headerLength, long plaintextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        var segments = Math.Max(1, (plaintextLength + SegmentSize - 1) / SegmentSize);
        return headerLength + plaintextLength + (segments * TagSize);
    }

    /// <summary>
    /// The plaintext length of a stored blob of <paramref name="blobLength"/> bytes (header included); a length no
    /// segment layout gives throws <see cref="BlobIntegrityException"/>.
    /// </summary>
    public static long PlaintextLength(BlobKey key, long blobLength, int headerLength)
    {
        const int stored = SegmentSize + TagSize;
        var body = blobLength - headerLength;
        var rest = body % stored;
        if (body < TagSize || rest is > 0 and < TagSize)
        {
            throw new BlobIntegrityException($"Blob '{key}': {body} bytes after the URB2 header are no whole segments (cut or damaged).");
        }

        var segments = (body / stored) + (rest > 0 ? 1 : 0);
        return body - (segments * TagSize);
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends; the number of bytes read.</summary>
    public static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, bool useAsync, CancellationToken cancellationToken) =>
        useAsync
            ? await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            : stream.ReadAtLeast(buffer.Span, buffer.Length, throwOnEndOfStream: false);

    private static byte[] Ascii(string value, string what)
    {
        if (value.Length is 0 or > byte.MaxValue || !IsPrintableAscii(value))
        {
            throw new ArgumentException($"The {what} '{value}' must be 1-255 printable ASCII characters.", nameof(value));
        }

        return Encoding.ASCII.GetBytes(value);
    }

    private static bool IsPrintableAscii(string value) => value.All(c => c is > ' ' and < (char)0x7f);

    private static async Task<string> ReadStringAsync(Stream blob, MemoryStream header, BlobKey key, CancellationToken cancellationToken)
    {
        var length = (await ReadAsync(blob, header, 1, key, cancellationToken))[0];
        var value = Encoding.ASCII.GetString(await ReadAsync(blob, header, length, key, cancellationToken));
        return length > 0 && IsPrintableAscii(value) ? value
            : throw new BlobIntegrityException($"Blob '{key}': the URB2 header holds a malformed key name or version.");
    }

    private static async Task<byte[]> ReadAsync(Stream blob, MemoryStream header, int count, BlobKey key, CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        if (await blob.ReadAtLeastAsync(bytes, count, throwOnEndOfStream: false, cancellationToken) < count)
        {
            throw new BlobIntegrityException($"Blob '{key}' ends inside its URB2 header (cut or not envelope-encrypted).");
        }

        header.Write(bytes);
        return bytes;
    }
}
