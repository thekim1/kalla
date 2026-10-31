using System.Security.Cryptography;

namespace Ume.Rag.Worker.Connectors.Http;

/// <summary>
/// A response body in a local temp file (<c>/tmp</c> tmpfs or a scratch volume), never in memory (connectors design §11):
/// written once while hashing, then read as often as needed (malware scan, noise hash, blob write). The file is deleted
/// on dispose.
/// </summary>
public sealed class FetchedBody : IAsyncDisposable
{
    private const int BufferSize = 81920;

    private readonly FileStream _owner;

    private FetchedBody(FileStream owner, long length, string sha256, string mediaType, string? charset)
    {
        _owner = owner;
        Length = length;
        Sha256 = sha256;
        MediaType = mediaType;
        Charset = charset;
    }

    /// <summary>Bytes after decompression.</summary>
    public long Length { get; }

    /// <summary>SHA-256 of the raw bytes, lower-case hex.</summary>
    public string Sha256 { get; }

    /// <summary>The media type without parameters, lower-case.</summary>
    public string MediaType { get; }

    public string? Charset { get; }

    /// <summary>A new read stream from the start; dispose it before the body.</summary>
    public Stream OpenRead() =>
        new FileStream(_owner.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous);

    /// <summary>
    /// Copies <paramref name="source"/> to a new temp file, at most <paramref name="maxBytes"/>. A longer body returns null
    /// (the file is removed), or with <paramref name="truncate"/> its first <paramref name="maxBytes"/> bytes.
    /// </summary>
    public static async Task<FetchedBody?> CaptureAsync(
        Stream source, long maxBytes, bool truncate, string mediaType, string? charset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = Path.Combine(Path.GetTempPath(), "ume-rag-fetch-" + Guid.NewGuid().ToString("N"));
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (total + read > maxBytes)
                {
                    if (!truncate)
                    {
                        await file.DisposeAsync();
                        return null;
                    }

                    read = (int)(maxBytes - total);
                }

                total += read;
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (total == maxBytes && truncate)
                {
                    break;
                }
            }

            await file.FlushAsync(cancellationToken);
            return new FetchedBody(file, total, Convert.ToHexStringLower(hash.GetHashAndReset()), mediaType, charset);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
