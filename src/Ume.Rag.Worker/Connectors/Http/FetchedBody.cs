using System.Security.Cryptography;

namespace Ume.Rag.Worker.Connectors.Http;

/// <summary>
/// A response body in a local temp file (<c>TMPDIR</c>: in Compose a spool directory on the worker's disk volume, since a
/// tmpfs counts against the container's memory and 32 fetches of 50 MB bodies would pass it), never in memory
/// (connectors design §11): written once while hashing, then read as often as needed (malware scan, noise hash, blob
/// write). The file is deleted on dispose; <see cref="PrepareSpool"/> removes what a killed process left.
/// </summary>
public sealed class FetchedBody : IAsyncDisposable
{
    private const int BufferSize = 81920;
    private const string Prefix = "ume-rag-fetch-";

    /// <summary>A spool file this old belongs to no running fetch (a fetch job's lease is 2 minutes).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// At start: creates the temp directory if it is missing (a spool directory on a volume) and deletes this worker's spool
    /// files older than <see cref="StaleAfter"/>, which a killed process left (<c>DeleteOnClose</c> unlinks on close only).
    /// Returns the number deleted.
    /// </summary>
    public static int PrepareSpool(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var directory = Directory.CreateDirectory(Path.GetTempPath());
        var cutoff = time.GetUtcNow().UtcDateTime - StaleAfter;
        var deleted = 0;
        foreach (var file in directory.EnumerateFiles(Prefix + "*"))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    file.Delete();
                    deleted++;
                }
            }
            catch (IOException)
            {
                // Gone or in use: the next start tries again.
            }
            catch (UnauthorizedAccessException)
            {
                // Not this worker's file.
            }
        }

        return deleted;
    }

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
    public static Task<FetchedBody?> CaptureAsync(
        Stream source, long maxBytes, bool truncate, string mediaType, string? charset, CancellationToken cancellationToken) =>
        CaptureAsync(ReadOnlyMemory<byte>.Empty, source, maxBytes, truncate, mediaType, charset, cancellationToken);

    /// <summary>
    /// As <see cref="CaptureAsync(Stream, long, bool, string, string?, CancellationToken)"/>, the body starting with
    /// <paramref name="head"/>: bytes already read from <paramref name="source"/> (to sniff the type, §4).
    /// </summary>
    public static async Task<FetchedBody?> CaptureAsync(
        ReadOnlyMemory<byte> head, Stream source, long maxBytes, bool truncate, string mediaType, string? charset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            var pending = head;
            long total = 0;
            while (true)
            {
                ReadOnlyMemory<byte> chunk;
                if (!pending.IsEmpty)
                {
                    (chunk, pending) = (pending, ReadOnlyMemory<byte>.Empty);
                }
                else
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    chunk = buffer.AsMemory(0, read);
                }

                if (total + chunk.Length > maxBytes)
                {
                    if (!truncate)
                    {
                        await file.DisposeAsync();
                        return null;
                    }

                    chunk = chunk[..(int)(maxBytes - total)];
                }

                total += chunk.Length;
                hash.AppendData(chunk.Span);
                await file.WriteAsync(chunk, cancellationToken);
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
