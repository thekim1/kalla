using System.Buffers;
using System.Security.Cryptography;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Infrastructure.Blobs;

/// <summary>A read-only, forward-only stream; the base of the two <see cref="Urb2"/> streams.</summary>
internal abstract class ForwardReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public abstract override int Read(Span<byte> buffer);

    public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Reads as the <see cref="Urb2"/> form of a plaintext stream: the header, then one sealed segment at a time. The
/// plaintext stream stays open (its owner disposes it). Content of another length than the declared one fails with
/// <see cref="InvalidDataException"/> at its end, so the blob store's write fails and leaves no key.
/// </summary>
internal sealed class EncryptingStream : ForwardReadStream
{
    private readonly Stream _plaintext;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _noncePrefix;
    private readonly long? _declared;
    private readonly BlobKey _key;
    private readonly byte[] _segment = ArrayPool<byte>.Shared.Rent(Urb2.SegmentSize);
    private readonly byte[] _sealed = ArrayPool<byte>.Shared.Rent(Urb2.SegmentSize + Urb2.TagSize);
    private readonly byte[] _peek = new byte[1];
    private int _position;
    private int _length;
    private bool _carry;
    private uint _counter;
    private long _read;
    private bool _final;
    private bool _disposed;

    public EncryptingStream(Stream plaintext, ReadOnlySpan<byte> dataKey, byte[] header, byte[] noncePrefix, long? declared, BlobKey key)
    {
        _plaintext = plaintext;
        _aes = new AesGcm(dataKey, Urb2.TagSize);
        _header = header;
        _noncePrefix = noncePrefix;
        _declared = declared;
        _key = key;
        header.CopyTo(_sealed, 0);
        _length = header.Length;
    }

    public override int Read(Span<byte> buffer)
    {
        while (_position == _length && !_final)
        {
            SealNextAsync(useAsync: false, CancellationToken.None).GetAwaiter().GetResult();
        }

        return CopyOut(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_position == _length && !_final)
        {
            await SealNextAsync(useAsync: true, cancellationToken);
        }

        return CopyOut(buffer.Span);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_segment);
            ArrayPool<byte>.Shared.Return(_segment);
            ArrayPool<byte>.Shared.Return(_sealed);
        }

        base.Dispose(disposing);
    }

    private int CopyOut(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _length - _position);
        _sealed.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    /// <summary>Seals the next segment into the output buffer; one byte of look-ahead tells whether it is the final one.</summary>
    private async Task SealNextAsync(bool useAsync, CancellationToken cancellationToken)
    {
        var start = _carry ? 1 : 0;
        if (_carry)
        {
            _segment[0] = _peek[0];
            _carry = false;
        }

        var read = await Urb2.FillAsync(_plaintext, _segment.AsMemory(start, Urb2.SegmentSize - start), useAsync, cancellationToken);
        var count = start + read;
        var final = count < Urb2.SegmentSize || await Urb2.FillAsync(_plaintext, _peek, useAsync, cancellationToken) == 0;
        _carry = !final;
        _read += read + (_carry ? 1 : 0);
        if (_declared is { } declared && (_read > declared || (final && _read != declared)))
        {
            throw new InvalidDataException($"Blob '{_key}': more or fewer than the {declared} declared bytes read.");
        }

        if (!final && _counter == uint.MaxValue)
        {
            throw new InvalidDataException($"Blob '{_key}' has more segments than the URB2 counter holds.");
        }

        Span<byte> nonce = stackalloc byte[Urb2.NonceSize];
        Urb2.Nonce(_noncePrefix, _counter, final, nonce);
        _aes.Encrypt(nonce, _segment.AsSpan(0, count), _sealed.AsSpan(0, count), _sealed.AsSpan(count, Urb2.TagSize), _header);
        _position = 0;
        _length = count + Urb2.TagSize;
        _final = final;
        _counter++;
    }
}

/// <summary>
/// Reads the plaintext of a <see cref="Urb2"/> blob positioned after its header: a segment's bytes are returned only after
/// its tag verified, and a failed segment, a cut blob (no final segment) or reordered segments throw
/// <see cref="BlobIntegrityException"/>, then and on every later read. Owns and disposes the blob stream.
/// </summary>
internal sealed class DecryptingStream : ForwardReadStream
{
    private readonly Stream _blob;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _noncePrefix;
    private readonly BlobKey _key;
    private readonly byte[] _sealed = ArrayPool<byte>.Shared.Rent(Urb2.SegmentSize + Urb2.TagSize);
    private readonly byte[] _segment = ArrayPool<byte>.Shared.Rent(Urb2.SegmentSize);
    private readonly byte[] _peek = new byte[1];
    private int _position;
    private int _length;
    private bool _carry;
    private uint _counter;
    private bool _final;
    private BlobIntegrityException? _failure;
    private bool _disposed;

    private DecryptingStream(Stream blob, byte[] dataKey, byte[] header, byte[] noncePrefix, BlobKey key)
    {
        _blob = blob;
        _aes = new AesGcm(dataKey, Urb2.TagSize);
        _header = header;
        _noncePrefix = noncePrefix;
        _key = key;
    }

    /// <summary>The header's length, for the plaintext length of a blob of known length.</summary>
    public int HeaderLength => _header.Length;

    /// <summary>
    /// Reads the header of <paramref name="blob"/> and unwraps its data key. On failure the blob stream is disposed and
    /// <see cref="BlobIntegrityException"/> (header) or <see cref="KeyProviderException"/> (key) is thrown.
    /// </summary>
    public static async Task<DecryptingStream> OpenAsync(Stream blob, BlobKey key, IKeyProvider keys, CancellationToken cancellationToken)
    {
        try
        {
            var (header, wrapped, prefix) = await Urb2.ReadHeaderAsync(blob, key, cancellationToken);
            var dataKey = await keys.UnwrapAsync(wrapped, cancellationToken);
            try
            {
                return dataKey.Length == Urb2.DataKeySize ? new DecryptingStream(blob, dataKey, header, prefix, key)
                    : throw new BlobIntegrityException($"Blob '{key}': the unwrapped data key is {dataKey.Length} bytes, not {Urb2.DataKeySize}.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        catch
        {
            await blob.DisposeAsync();
            throw;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        while (_position == _length && !_final)
        {
            OpenNextAsync(useAsync: false, CancellationToken.None).GetAwaiter().GetResult();
        }

        return CopyOut(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_position == _length && !_final)
        {
            await OpenNextAsync(useAsync: true, cancellationToken);
        }

        return CopyOut(buffer.Span);
    }

    /// <summary>Skips <paramref name="count"/> plaintext bytes (fewer at the end), verifying every segment on the way.</summary>
    public async Task SkipAsync(long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (_position == _length)
            {
                if (_final)
                {
                    return;
                }

                await OpenNextAsync(useAsync: true, cancellationToken);
                continue;
            }

            var skipped = (int)Math.Min(count, _length - _position);
            _position += skipped;
            count -= skipped;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _blob.Dispose();
            Release();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _blob.DisposeAsync();
            Release();
        }

        await base.DisposeAsync();
    }

    private void Release()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_segment);
        ArrayPool<byte>.Shared.Return(_segment);
        ArrayPool<byte>.Shared.Return(_sealed);
    }

    private int CopyOut(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _length - _position);
        _segment.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    /// <summary>Reads, verifies and decrypts the next segment; one byte of look-ahead tells whether it is the final one.</summary>
    private async Task OpenNextAsync(bool useAsync, CancellationToken cancellationToken)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        const int stored = Urb2.SegmentSize + Urb2.TagSize;
        var start = _carry ? 1 : 0;
        if (_carry)
        {
            _sealed[0] = _peek[0];
            _carry = false;
        }

        var count = start + await Urb2.FillAsync(_blob, _sealed.AsMemory(start, stored - start), useAsync, cancellationToken);
        var final = count < stored || await Urb2.FillAsync(_blob, _peek, useAsync, cancellationToken) == 0;
        _carry = !final;
        if (count < Urb2.TagSize)
        {
            throw Fail(count == 0 ? $"Blob '{_key}' has no final segment (cut)." : $"Blob '{_key}': segment {_counter} is cut.", null);
        }

        var length = count - Urb2.TagSize;
        Span<byte> nonce = stackalloc byte[Urb2.NonceSize];
        Urb2.Nonce(_noncePrefix, _counter, final, nonce);
        try
        {
            _aes.Decrypt(nonce, _sealed.AsSpan(0, length), _sealed.AsSpan(length, Urb2.TagSize), _segment.AsSpan(0, length), _header);
        }
        catch (CryptographicException e)
        {
            throw Fail($"Blob '{_key}': segment {_counter} failed verification (changed, reordered, or the blob was cut).", e);
        }

        _position = 0;
        _length = length;
        _final = final;
        _counter++;
    }

    private BlobIntegrityException Fail(string message, Exception? inner)
    {
        CryptographicOperations.ZeroMemory(_segment);
        _position = _length = 0;
        _failure = new BlobIntegrityException(message, inner);
        return _failure;
    }
}
