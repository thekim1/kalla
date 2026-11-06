using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Retrieval;

/// <summary>
/// A collection's resolved <c>RetrievalSettings</c> (knowledge design §12.1): the collection's values over the
/// platform defaults in <c>Settings.Retrieval</c>. <see cref="Recency"/> is the collection's age decay, applied after
/// fusion by <see cref="RecencyAdjuster"/> (P1b item 3).
/// </summary>
public sealed record RetrievalSettings
{
    public double VectorWeight { get; init; } = 1.0;

    public double KeywordWeight { get; init; } = 1.0;

    /// <summary>Candidate depth per list, 20–200.</summary>
    public int Candidates { get; init; } = 100;

    /// <summary>Most chunks per document (identity group) in a result, 1–10.</summary>
    public int MaxChunksPerDocument { get; init; } = 3;

    public RecencySettings? Recency { get; init; }

    public double CoverageMaxDistance { get; init; } = 0.50;

    /// <summary>The keyword floor of the coverage signal; null until calibrated (vector side alone decides, §7).</summary>
    public double? CoverageMinKeywordScore { get; init; }

    public int ExpandMaxTokens { get; init; } = 1500;

    public static RetrievalSettings Default { get; } = new();

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value outside its range.</summary>
    public RetrievalSettings Validated()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(VectorWeight, nameof(VectorWeight));
        ArgumentOutOfRangeException.ThrowIfNegative(KeywordWeight, nameof(KeywordWeight));
        ArgumentOutOfRangeException.ThrowIfLessThan(Candidates, 20, nameof(Candidates));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Candidates, 200, nameof(Candidates));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxChunksPerDocument, 1, nameof(MaxChunksPerDocument));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxChunksPerDocument, 10, nameof(MaxChunksPerDocument));
        ArgumentOutOfRangeException.ThrowIfNegative(CoverageMaxDistance, nameof(CoverageMaxDistance));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CoverageMaxDistance, 2.0, nameof(CoverageMaxDistance));
        ArgumentOutOfRangeException.ThrowIfLessThan(ExpandMaxTokens, 1, nameof(ExpandMaxTokens));
        return this;
    }
}

/// <summary>Age decay of a collection (knowledge design §6.5, weakness review §3). Not applied in P1a.</summary>
public sealed record RecencySettings(double HalfLifeDays, double Weight, IReadOnlyList<string> Kinds);

/// <summary>
/// Platform-only retrieval values of <c>Settings.Retrieval</c> (knowledge design §6.4, §12.1) with the defaults for
/// every collection.
/// </summary>
public sealed record RetrievalPlatformSettings
{
    public RetrievalSettings Defaults { get; init; } = RetrievalSettings.Default;

    /// <summary>Scopes of at most this many chunks are searched exactly (§6.4).</summary>
    public int ExactScanMaxChunks { get; init; } = 10_000;

    /// <summary>Below this share of the profile's index a large scope is still searched exactly (§6.4): 0.2 %.</summary>
    public double MinHnswSelectivity { get; init; } = 0.002;

    public int ExpandTotalMaxTokens { get; init; } = 8000;

    /// <summary>
    /// How many fused results the reranker scores (§6.7), 1–50: 20, the top 20 of Decisions "Rerank pulled forward" (the
    /// <see cref="ChatModelReranker"/> scores them in one chat call).
    /// </summary>
    public int RerankCandidates { get; init; } = 20;

    /// <summary>The rerank floor that replaces the vector side of coverage when a search reranks (§6.7, §7); null: off.</summary>
    public double? CoverageMinRerankScore { get; init; }

    /// <summary>
    /// The chat model of the gateway's mirror that <see cref="ChatModelReranker"/> calls (<c>Settings.Retrieval.rerankModel</c>,
    /// checked by V32); null until chosen. Rerank is optional: while it is null an assistant that reranks with the chat model
    /// gets the plain fused order, with no call and nothing degraded.
    /// </summary>
    public string? RerankModel { get; init; }

    /// <summary>
    /// The rerank model (gateway kind <c>Rerank</c>) that <see cref="GatewayReranker"/> calls on <c>/v1/rerank</c>
    /// (<c>Settings.Retrieval.gatewayRerankModel</c>, checked by V32; B-16); null until the gateway has one (most
    /// deployments), and an assistant that reranks with the gateway then gets the plain fused order, as above.
    /// </summary>
    public string? GatewayRerankModel { get; init; }

    /// <summary>The platform's model for <paramref name="kind"/>; null when unset (no rerank call is made).</summary>
    public string? RerankModelFor(RerankerKind kind) => kind == RerankerKind.Gateway ? GatewayRerankModel : RerankModel;

    public static RetrievalPlatformSettings Default { get; } = new();

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value outside its range.</summary>
    public RetrievalPlatformSettings Validated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RerankCandidates, 1, nameof(RerankCandidates));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RerankCandidates, 50, nameof(RerankCandidates));
        if (RerankModel is { Length: > 200 })
        {
            throw new ArgumentOutOfRangeException(nameof(RerankModel), "rerankModel is at most 200 characters.");
        }

        if (GatewayRerankModel is { Length: > 200 })
        {
            throw new ArgumentOutOfRangeException(nameof(GatewayRerankModel), "gatewayRerankModel is at most 200 characters.");
        }

        return this;
    }
}

/// <summary>
/// Resolves <c>RetrievalSettings</c> (knowledge design §12.1): reads <c>Settings.Retrieval</c> (platform values and
/// defaults) and a collection's <c>RetrievalSettings</c> jsonb, collection values over platform defaults over the
/// built-in defaults. Unknown members are ignored (the admin API validates on save); a value of the wrong type or out of
/// range throws.
/// </summary>
public static class RetrievalSettingsResolver
{
    public static RetrievalPlatformSettings Platform(string? settingsRetrievalJson)
    {
        var json = Parse(settingsRetrievalJson);
        var builtIn = RetrievalPlatformSettings.Default;
        return new RetrievalPlatformSettings
        {
            Defaults = Apply(RetrievalSettings.Default, json),
            ExactScanMaxChunks = Int(json, "exactScanMaxChunks") ?? builtIn.ExactScanMaxChunks,
            MinHnswSelectivity = Double(json, "minHnswSelectivity") ?? builtIn.MinHnswSelectivity,
            ExpandTotalMaxTokens = Int(json, "expandTotalMaxTokens") ?? builtIn.ExpandTotalMaxTokens,
            RerankCandidates = Int(json, "rerankCandidates") ?? builtIn.RerankCandidates,
            CoverageMinRerankScore = json.ContainsKey("coverageMinRerankScore")
                ? Double(json, "coverageMinRerankScore")
                : builtIn.CoverageMinRerankScore,
            RerankModel = String(json, "rerankModel") is { Length: > 0 } model ? model : null,
            GatewayRerankModel = String(json, "gatewayRerankModel") is { Length: > 0 } gatewayModel ? gatewayModel : null,
        }.Validated();
    }

    /// <summary>A collection's settings: its <c>RetrievalSettings</c> jsonb over <paramref name="platform"/>'s defaults.</summary>
    public static RetrievalSettings Collection(RetrievalPlatformSettings platform, string? collectionJson)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return Apply(platform.Defaults, Parse(collectionJson));
    }

    private static RetrievalSettings Apply(RetrievalSettings defaults, JsonObject json) => new RetrievalSettings
    {
        VectorWeight = Double(json, "vectorWeight") ?? defaults.VectorWeight,
        KeywordWeight = Double(json, "keywordWeight") ?? defaults.KeywordWeight,
        Candidates = Int(json, "candidates") ?? defaults.Candidates,
        MaxChunksPerDocument = Int(json, "maxChunksPerDocument") ?? defaults.MaxChunksPerDocument,
        // An explicit null switches a default off (recency) or marks a floor as not calibrated (keyword score).
        Recency = json.ContainsKey("recency") ? Recency(json["recency"]) : defaults.Recency,
        CoverageMaxDistance = Double(json, "coverageMaxDistance") ?? defaults.CoverageMaxDistance,
        CoverageMinKeywordScore = json.ContainsKey("coverageMinKeywordScore")
            ? Double(json, "coverageMinKeywordScore")
            : defaults.CoverageMinKeywordScore,
        ExpandMaxTokens = Int(json, "expandMaxTokens") ?? defaults.ExpandMaxTokens,
    }.Validated();

    private static RecencySettings? Recency(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new RecencySettings(
            Double(o, "halfLifeDays") ?? throw new FormatException("recency.halfLifeDays is required."),
            Double(o, "weight") ?? throw new FormatException("recency.weight is required."),
            o["kinds"] is JsonArray kinds ? [.. kinds.Select(k => k?.GetValue<string>() ?? throw new FormatException("recency.kinds has a null."))] : []),
        _ => throw new FormatException("recency must be an object or null."),
    };

    private static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Retrieval settings must be a JSON object.");
    }

    private static double? Double(JsonObject json, string name) =>
        json[name] is JsonValue v ? v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : throw new FormatException($"{name} must be a number.") : null;

    private static string? String(JsonObject json, string name) => json[name] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Trim(),
        _ => throw new FormatException($"{name} must be a string or null."),
    };

    private static int? Int(JsonObject json, string name) =>
        json[name] is JsonValue v ? v.GetValueKind() == JsonValueKind.Number ? v.GetValue<int>() : throw new FormatException($"{name} must be a number.") : null;
}
