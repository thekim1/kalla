using System.IO.Compression;
using System.Text.Json;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Definitions;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Preview;

/// <summary>A stage output as the admin streams it: the stage, whether it was cut at the 2 MB limit, and the value.</summary>
public sealed record PreviewOutput<T>(string Stage, bool Truncated, T Value);

/// <summary>
/// The outputs of one preview job (engine design §8, §11). Stage values are looked up by their stage key in this job's
/// copies (a requeued preview), then in the live output cache <b>read-only</b> (the <c>NodeOutputs</c> row and its blob;
/// <c>LastUsedAt</c> is never touched), then in the base preview's copies (<c>basePreviewJobId</c>), which are copied into
/// this job's prefix. Computed values are stored only here: <c>previews/{jobId}/keys/{stage}/{key}.json.gz</c>. What the
/// admin streams is <c>previews/{jobId}/{n}/{name}.json</c>, cut to 2 MB with a marker.
/// </summary>
public sealed class PreviewStore(PreviewBlobs blobs, INodeOutputStore live, Guid? baseJobId)
{
    /// <summary>The largest output shown per stage (engine design §11).</summary>
    public const int MaxOutputBytes = 2 * 1024 * 1024;

    public PreviewBlobs Blobs => blobs;

    /// <summary>The preview whose outputs this one reuses (<c>basePreviewJobId</c>).</summary>
    public Guid? BaseJobId => baseJobId;

    /// <summary>The value under <paramref name="key"/> with its output hash, or null on a miss.</summary>
    public async Task<(T Value, byte[] OutputHash)?> GetAsync<T>(string stage, byte[] key, CancellationToken cancellationToken)
    {
        if (await ReadAsync<T>(KeyOf(blobs.Prefix, stage, key), gzip: true, cancellationToken) is { } own)
        {
            return (own, ProcessingJson.OutputHash(own));
        }

        if (await live.FindAsync(key, cancellationToken) is { } row &&
            await ReadAsync<T>(new BlobKey(row.IsParseOutput ? $"{row.BlobPath}/{StageOutputs.ResultFile}" : row.BlobPath), !row.IsParseOutput, cancellationToken) is { } cached)
        {
            return (cached, row.OutputHash);
        }

        if (baseJobId is { } previous && await ReadAsync<T>(KeyOf(PreviewBlobs.PrefixOf(previous), stage, key), gzip: true, cancellationToken) is { } reused)
        {
            return (reused, await PutAsync(stage, key, reused, cancellationToken));
        }

        return null;
    }

    /// <summary>Stores a computed value under this job's prefix; returns its output hash (the next stage's input).</summary>
    public async Task<byte[]> PutAsync<T>(string stage, byte[] key, T value, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, value, ProcessingJson.Options, cancellationToken);
        }

        buffer.Position = 0;
        await blobs.PutAsync(KeyOf(blobs.Prefix, stage, key), buffer, buffer.Length, cancellationToken);
        return ProcessingJson.OutputHash(value);
    }

    /// <summary>
    /// Writes what the admin streams for document <paramref name="index"/>; a value over <see cref="MaxOutputBytes"/> is
    /// cut by <paramref name="cut"/> (the value with its first n items) to the most items that fit, and marked truncated.
    /// </summary>
    public async Task<(string Path, bool Truncated)> ShowAsync<T>(int index, string name, string stage, T value, int items, Func<T, int, T>? cut,
        CancellationToken cancellationToken)
    {
        var bytes = Serialize(new PreviewOutput<T>(stage, false, value));
        var truncated = false;
        if (bytes.Length > MaxOutputBytes)
        {
            truncated = true;
            if (cut is null)
            {
                bytes = Serialize(new PreviewOutput<T?>(stage, true, default));
            }
            else
            {
                var (low, high) = (0, items);
                while (low < high)
                {
                    var mid = (low + high + 1) / 2;
                    if (Serialize(new PreviewOutput<T>(stage, true, cut(value, mid))).Length <= MaxOutputBytes)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                bytes = Serialize(new PreviewOutput<T>(stage, true, cut(value, low)));
            }
        }

        var key = new BlobKey($"{blobs.Prefix.Value}/{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{name}.json");
        using var stream = new MemoryStream(bytes, writable: false);
        await blobs.PutAsync(key, stream, bytes.Length, cancellationToken);
        return (key.Value, truncated);
    }

    private static BlobKey KeyOf(BlobKey prefix, string stage, byte[] key) => new($"{prefix.Value}/keys/{stage}/{StageKeys.ToHex(key)}.json.gz");

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProcessingJson.Options);

    private async Task<T?> ReadAsync<T>(BlobKey key, bool gzip, CancellationToken cancellationToken)
    {
        await using var stream = await blobs.OpenReadAsync(key, null, cancellationToken);
        if (stream is null)
        {
            return default;
        }

        if (!gzip)
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, ProcessingJson.Options, cancellationToken);
        }

        await using var decompressed = new GZipStream(stream, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<T>(decompressed, ProcessingJson.Options, cancellationToken);
    }
}
