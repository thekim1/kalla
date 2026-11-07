using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ume.Rag.Pipelines.Crawl;

/// <summary>
/// One host's politeness profile as stored in <c>Settings.Crawl.HostOverrides</c> by host (connectors design §6.6): a
/// preset fills every member, and a member set here overrides it; a null member keeps the preset's value. Read by the
/// worker (<c>PolitenessProfile</c>) and checked on save by the admin API (V37, <see cref="HostPolitenessRules"/>), so both
/// read the same shape. Example:
/// <code>
/// "www.umea.se": { "preset": "fast", "requestsPerSecond": 20, "circuitBreaker": "off",
///                  "robots": { "mode": "ignore", "reason": "own site, web team consent 2026-11-10" }, "userAgent": "Mozilla/5.0 …" }
/// </code>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostPoliteness
{
    /// <summary><c>polite</c>, <c>municipal</c>, <c>fast</c> or <c>custom</c>; null: <c>municipal</c> on municipal hosts, else <c>polite</c>.</summary>
    public string? Preset { get; init; }

    public double? RequestsPerSecond { get; init; }

    public int? MaxConnections { get; init; }

    /// <summary><c>honour</c> (robots.txt <c>Crawl-delay</c> may lower the rate, capped at 60 s) or <c>ignore</c>.</summary>
    public string? CrawlDelay { get; init; }

    /// <summary>Whether the latency term of the interval applies (§6.3).</summary>
    public bool? AdaptToLatency { get; init; }

    /// <summary><c>source</c> (the source's night window, §6.5) or <c>any</c> (fetched at any time).</summary>
    public string? CrawlWindow { get; init; }

    public HostBackoff? Backoff { get; init; }

    /// <summary>The breaker's threshold and pause, or <c>"off"</c>.</summary>
    public HostCircuitBreaker? CircuitBreaker { get; init; }

    public HostRobots? Robots { get; init; }

    /// <summary>Sent on every request to the host; robots.txt groups are still matched against <c>UmeaKommunBot</c>.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Pages the renderer loads at once from this host (§8.3).</summary>
    public int? RenderPerHost { get; init; }
}

/// <summary>The caps of a 429/503 (§6.4): a <c>Retry-After</c> is waited for up to <see cref="MaxRetryAfterSeconds"/>; the factor doubles up to <see cref="MaxFactor"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostBackoff
{
    public int? MaxRetryAfterSeconds { get; init; }

    public double? MaxFactor { get; init; }
}

/// <summary>The circuit breaker (§6.4): a share of failing responses among the last 100 and the pause, or <see cref="Off"/> (JSON <c>"off"</c>).</summary>
[JsonConverter(typeof(HostCircuitBreakerConverter))]
public sealed record HostCircuitBreaker
{
    public bool Off { get; init; }

    public double? Threshold { get; init; }

    public int? PauseMinutes { get; init; }
}

/// <summary>robots.txt: <c>respect</c>, or <c>ignore</c> with a required <see cref="Reason"/> (§6.6).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostRobots
{
    public string? Mode { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The words of the profile's JSON.</summary>
public static class PolitenessValues
{
    public const string Polite = "polite";
    public const string Municipal = "municipal";
    public const string Fast = "fast";
    public const string Custom = "custom";
    public const string Honour = "honour";
    public const string Ignore = "ignore";
    public const string Source = "source";
    public const string Any = "any";
    public const string Respect = "respect";
    public const string Off = "off";

    public static IReadOnlyList<string> Presets { get; } = [Polite, Municipal, Fast, Custom];
}

/// <summary><c>"off"</c> or <c>{ "threshold": …, "pauseMinutes": … }</c>.</summary>
internal sealed class HostCircuitBreakerConverter : JsonConverter<HostCircuitBreaker>
{
    public override HostCircuitBreaker Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return string.Equals(reader.GetString(), PolitenessValues.Off, StringComparison.Ordinal)
                ? new HostCircuitBreaker { Off = true }
                : throw new JsonException("circuitBreaker is \"off\" or an object with threshold and pauseMinutes.");
        }

        var members = JsonSerializer.Deserialize<Members>(ref reader, options) ?? throw new JsonException("circuitBreaker is null.");
        return new HostCircuitBreaker { Threshold = members.Threshold, PauseMinutes = members.PauseMinutes };
    }

    public override void Write(Utf8JsonWriter writer, HostCircuitBreaker value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Off)
        {
            writer.WriteStringValue(PolitenessValues.Off);
            return;
        }

        JsonSerializer.Serialize(writer, new Members { Threshold = value.Threshold, PauseMinutes = value.PauseMinutes }, options);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Members
    {
        public double? Threshold { get; init; }

        public int? PauseMinutes { get; init; }
    }
}
