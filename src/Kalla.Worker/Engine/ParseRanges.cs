using System.Globalization;
using System.Text.Json;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Definitions;
using Kalla.Worker.Parsing;
using Kalla.Worker.Processing.Parse;

namespace Kalla.Worker.Engine;

/// <summary>
/// The per-range parser results of one parse output (engine design §8; data-model §9): write-once blobs under the parse
/// key's prefix, <c>parse/{level}/{key}/ranges/{from}-{to}.{strategy}.result.json</c>, each the parser's
/// <c>result.json</c> of that range. The strategy is part of the name because the post-conversion checks request the same
/// pages again with another strategy.
/// </summary>
public sealed class BlobParseRanges(IBlobStore blobs, string classification, byte[] parseKey) : IParseRangeStore
{
    private static readonly JsonSerializerOptions Json = ParserJson.Create();

    private readonly string _prefix = $"parse/{classification}/{StageKeys.ToHex(parseKey)}/ranges";

    public async Task<ParserConvertResult?> GetAsync(ParseRange range, CancellationToken cancellationToken)
    {
        await using var stream = await blobs.OpenReadAsync(Key(range), null, cancellationToken);
        return stream is null ? null : await JsonSerializer.DeserializeAsync<ParserConvertResult>(stream, Json, cancellationToken);
    }

    public async Task PutAsync(ParseRange range, ParserConvertResult result, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, result, Json, cancellationToken);
        buffer.Position = 0;
        await blobs.PutAsync(Key(range), buffer, buffer.Length, cancellationToken);
    }

    private BlobKey Key(ParseRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return new BlobKey(string.Create(CultureInfo.InvariantCulture, $"{_prefix}/{range.From}-{range.To}.{range.Strategy.Replace('@', '-')}.result.json"));
    }
}
