using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Pipelines.Json;

namespace Kalla.Pipelines.Definitions;

/// <summary>
/// Per-stage cache keys (engine design §3.3, §8). All hashes are SHA-256 (32 bytes); <c>‖</c> is concatenation, with
/// each variable-length text part followed by a 0x00 byte so no two inputs concatenate alike:
/// <code>
/// settingsHash(s) = SHA-256(RFC 8785 of {"inputs": {…}, "params": {the resolved parameters tagged for s and the route}})
/// key(s)          = SHA-256(inputHash ‖ s 0x00 ‖ stageVersion (int32 big-endian) ‖ settingsHash(s))
/// key(parse)      = SHA-256(contentHash ‖ "parser" 0x00 ‖ parserBuild 0x00 ‖ parserSettingsHash)   parser-bound documents
/// </code>
/// <c>inputHash</c> is the previous stage's <c>OutputHash</c> (the raw content hash for <c>detect</c> and
/// <c>parse</c>). The chain is linear, so a changed parameter changes its stage's settings hash and key, and the later
/// keys only through the outputs it changes.
/// </summary>
public static class StageKeys
{
    public const int HashLength = 32;

    /// <summary>
    /// The canonical settings of <paramref name="stage"/>: the resolved parameters whose effect tag is the stage and
    /// whose <c>appliesTo</c> (if any) contains <paramref name="route"/>, plus the inputs the stage reads that are not
    /// parameters (<c>parseRule</c> = <c>id@version</c>, <c>siteProfileVersion</c> for HTML).
    /// </summary>
    public static byte[] Settings(ResolvedPipeline pipeline, string stage, string? route, IReadOnlyDictionary<string, string>? inputs = null)
    {
        var parameters = new JsonObject();
        foreach (var (name, parameter) in pipeline.Template.Template.Parameters)
        {
            if (!string.Equals(parameter.Stage, stage, StringComparison.Ordinal))
            {
                continue;
            }

            if (parameter.AppliesTo is { } routes)
            {
                if (route is null)
                {
                    throw new ArgumentException($"Parameter {name} of {stage} depends on the route; pass one.", nameof(route));
                }

                if (!routes.Contains(route, StringComparer.Ordinal))
                {
                    continue;
                }
            }

            parameters[name] = pipeline.Params[name]?.DeepClone();
        }

        var settings = new JsonObject
        {
            ["inputs"] = new JsonObject([.. (inputs ?? new Dictionary<string, string>()).Select(i => KeyValuePair.Create(i.Key, (JsonNode?)i.Value))]),
            ["params"] = parameters,
        };
        return CanonicalJson.Serialize(JsonSerializer.SerializeToElement(settings));
    }

    public static byte[] SettingsHash(ResolvedPipeline pipeline, string stage, string? route, IReadOnlyDictionary<string, string>? inputs = null) =>
        SHA256.HashData(Settings(pipeline, stage, route, inputs));

    public static byte[] Key(ReadOnlySpan<byte> inputHash, string stage, int stageVersion, ReadOnlySpan<byte> settingsHash)
    {
        RequireHash(inputHash, nameof(inputHash));
        RequireHash(settingsHash, nameof(settingsHash));
        var buffer = new List<byte>(128);
        buffer.AddRange(inputHash);
        AddText(buffer, stage);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(version, stageVersion);
        buffer.AddRange(version);
        buffer.AddRange(settingsHash);
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>The key of <paramref name="stage"/> with the stage version of the pipeline's template.</summary>
    public static byte[] Key(ResolvedPipeline pipeline, string stage, ReadOnlySpan<byte> inputHash, string? route, IReadOnlyDictionary<string, string>? inputs = null)
    {
        if (!pipeline.Template.Template.Stages.TryGetValue(stage, out var version))
        {
            throw new ArgumentException($"Template {pipeline.Template.Template.Name} has no stage {stage}.", nameof(stage));
        }

        return Key(inputHash, stage, version, SettingsHash(pipeline, stage, route, inputs));
    }

    /// <summary>The parse output of a parser-bound document, shared by every document with the same bytes and settings.</summary>
    public static byte[] ParserKey(ReadOnlySpan<byte> contentHash, string parserBuild, ReadOnlySpan<byte> parserSettingsHash)
    {
        RequireHash(contentHash, nameof(contentHash));
        RequireHash(parserSettingsHash, nameof(parserSettingsHash));
        var buffer = new List<byte>(160);
        buffer.AddRange(contentHash);
        AddText(buffer, "parser");
        AddText(buffer, parserBuild);
        buffer.AddRange(parserSettingsHash);
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>The lower-case hex form job payloads carry (<c>inputKey</c>).</summary>
    public static string ToHex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    private static void AddText(List<byte> buffer, string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A key part contains a NUL character.", nameof(text));
        }

        buffer.AddRange(Encoding.UTF8.GetBytes(text));
        buffer.Add(0);
    }

    private static void RequireHash(ReadOnlySpan<byte> hash, string name)
    {
        if (hash.Length != HashLength)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{name} must be {HashLength} bytes, not {hash.Length}."), name);
        }
    }
}
