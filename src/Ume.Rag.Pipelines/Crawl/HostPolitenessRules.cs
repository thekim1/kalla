using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.Rag.Pipelines.Crawl;

/// <summary>
/// The deployment's ceilings of a host's politeness profile (connectors design §6.6): <c>Crawl:MaxHostRate</c>,
/// <c>Crawl:MaxHostConnections</c> and <c>Render:MaxConcurrent</c> (the most <c>renderPerHost</c> may be). The admin
/// refuses a profile above them (V37); the worker clamps to them.
/// </summary>
public sealed record PolitenessCeilings(double MaxHostRate, int MaxHostConnections, int MaxRenderPerHost)
{
    public const double DefaultMaxHostRate = 50;
    public const int DefaultMaxHostConnections = 16;
    public const int DefaultMaxRenderPerHost = 4;

    public static PolitenessCeilings Default { get; } = new(DefaultMaxHostRate, DefaultMaxHostConnections, DefaultMaxRenderPerHost);
}

/// <param name="Host">The host key (empty when <c>hostOverrides</c> itself is malformed).</param>
/// <param name="Field">The member, e.g. <c>robots.reason</c>; <c>host</c> for the key itself, empty for the whole profile.</param>
/// <param name="Problem">What is wrong, in English.</param>
public sealed record PolitenessProblem(string Host, string Field, string Problem);

/// <summary>
/// V37 (admin design §4.6; connectors design §6.6): every host's profile in <c>Settings.Crawl.HostOverrides</c> has known
/// members within their ranges and the deployment's ceilings; <c>robots</c> <c>ignore</c> has a non-empty reason; the
/// User-Agent is 1–256 printable ASCII characters; the key is a canonical host (lower-case A-label, no scheme, port or
/// path). The ranges are the worker's clamps too.
/// </summary>
public static class HostPolitenessRules
{
    public const double MinRequestsPerSecond = 0.1;
    public const int MaxRetryAfterSeconds = 3600;
    public const double MaxBackoffFactor = 16;
    public const double MinBreakerThreshold = 0.2;
    public const double MaxBreakerThreshold = 1.0;
    public const int MaxPauseMinutes = 120;
    public const int MaxUserAgentLength = 256;
    public const int MaxReasonLength = 500;

    private static readonly string[] ProfileMembers =
        ["preset", "requestsPerSecond", "maxConnections", "crawlDelay", "adaptToLatency", "crawlWindow", "backoff", "circuitBreaker", "robots", "userAgent", "renderPerHost"];

    private static readonly string[] BackoffMembers = ["maxRetryAfterSeconds", "maxFactor"];
    private static readonly string[] BreakerMembers = ["threshold", "pauseMinutes"];
    private static readonly string[] RobotsMembers = ["mode", "reason"];

    /// <summary>The first problem of <c>hostOverrides</c> (absent or null is fine), or null.</summary>
    public static PolitenessProblem? Check(JsonNode? hostOverrides, PolitenessCeilings ceilings)
    {
        ArgumentNullException.ThrowIfNull(ceilings);
        if (hostOverrides is null)
        {
            return null;
        }

        if (hostOverrides is not JsonObject hosts)
        {
            return new PolitenessProblem("", "hostOverrides", "must be an object with one profile per host");
        }

        foreach (var (host, profile) in hosts)
        {
            if (!IsCanonicalHost(host))
            {
                return new PolitenessProblem(host, "host", "must be a host name in lower case without scheme, port or path, such as www.umea.se");
            }

            if (Check(host, profile, ceilings) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>The first problem of one host's profile, or null.</summary>
    public static PolitenessProblem? Check(string host, JsonNode? profile, PolitenessCeilings ceilings)
    {
        ArgumentNullException.ThrowIfNull(ceilings);
        if (profile is not JsonObject members)
        {
            return new PolitenessProblem(host, "", "must be an object");
        }

        var unknown = Unknown(members, ProfileMembers, "")
            ?? (members["backoff"] is JsonObject backoff ? Unknown(backoff, BackoffMembers, "backoff.") : null)
            ?? (members["circuitBreaker"] is JsonObject breaker ? Unknown(breaker, BreakerMembers, "circuitBreaker.") : null)
            ?? (members["robots"] is JsonObject robots ? Unknown(robots, RobotsMembers, "robots.") : null);
        if (unknown is not null)
        {
            return new PolitenessProblem(host, unknown, "is not a politeness setting");
        }

        HostPoliteness value;
        try
        {
            value = members.Deserialize<HostPoliteness>(JsonSerializerOptions.Web) ?? new HostPoliteness();
        }
        catch (JsonException e)
        {
            var field = e.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : "";
            return new PolitenessProblem(host, field, "has the wrong type");
        }

        return Ranges(host, value, ceilings);
    }

    /// <summary>A lower-case A-label host name or IPv4 address, without scheme, port, path or trailing dot.</summary>
    public static bool IsCanonicalHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.EndsWith('.')
            || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return false;
        }

        try
        {
            return string.Equals(new IdnMapping().GetAscii(host), host, StringComparison.Ordinal)
                && string.Equals(host.ToLowerInvariant(), host, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>1–256 printable ASCII characters (space to tilde), not only spaces.</summary>
    public static bool IsValidUserAgent(string? userAgent) =>
        userAgent is { Length: > 0 and <= MaxUserAgentLength } && !string.IsNullOrWhiteSpace(userAgent) && userAgent.All(c => c is >= ' ' and <= '~');

    private static string? Unknown(JsonObject members, string[] known, string prefix) =>
        members.Select(m => m.Key).FirstOrDefault(name => !known.Contains(name, StringComparer.Ordinal)) is { } name ? prefix + name : null;

    private static PolitenessProblem? Ranges(string host, HostPoliteness value, PolitenessCeilings ceilings)
    {
        string? field = null;
        string? problem = null;
        if (value.Preset is { } preset && !PolitenessValues.Presets.Contains(preset, StringComparer.Ordinal))
        {
            (field, problem) = ("preset", "must be one of " + string.Join(", ", PolitenessValues.Presets));
        }
        else if (value.RequestsPerSecond is { } rate && !(rate >= MinRequestsPerSecond && rate <= ceilings.MaxHostRate))
        {
            (field, problem) = ("requestsPerSecond", Between(MinRequestsPerSecond, ceilings.MaxHostRate) + " (the ceiling is Crawl:MaxHostRate)");
        }
        else if (value.MaxConnections is { } connections && (connections < 1 || connections > ceilings.MaxHostConnections))
        {
            (field, problem) = ("maxConnections", Between(1, ceilings.MaxHostConnections) + " (the ceiling is Crawl:MaxHostConnections)");
        }
        else if (value.CrawlDelay is { } delay && !OneOf(delay, PolitenessValues.Honour, PolitenessValues.Ignore))
        {
            (field, problem) = ("crawlDelay", "must be honour or ignore");
        }
        else if (value.CrawlWindow is { } window && !OneOf(window, PolitenessValues.Source, PolitenessValues.Any))
        {
            (field, problem) = ("crawlWindow", "must be source or any");
        }
        else if (value.Backoff?.MaxRetryAfterSeconds is { } retryAfter && (retryAfter < 1 || retryAfter > MaxRetryAfterSeconds))
        {
            (field, problem) = ("backoff.maxRetryAfterSeconds", Between(1, MaxRetryAfterSeconds) + " seconds");
        }
        else if (value.Backoff?.MaxFactor is { } factor && !(factor >= 1 && factor <= MaxBackoffFactor))
        {
            (field, problem) = ("backoff.maxFactor", Between(1, MaxBackoffFactor));
        }
        else if (value.CircuitBreaker?.Threshold is { } threshold && !(threshold >= MinBreakerThreshold && threshold <= MaxBreakerThreshold))
        {
            (field, problem) = ("circuitBreaker.threshold", Between(MinBreakerThreshold, MaxBreakerThreshold));
        }
        else if (value.CircuitBreaker?.PauseMinutes is { } pause && (pause < 1 || pause > MaxPauseMinutes))
        {
            (field, problem) = ("circuitBreaker.pauseMinutes", Between(1, MaxPauseMinutes) + " minutes");
        }
        else if (value.Robots is { } robots && !OneOf(robots.Mode, PolitenessValues.Respect, PolitenessValues.Ignore))
        {
            (field, problem) = ("robots.mode", "must be respect or ignore");
        }
        else if (value.Robots is { Mode: PolitenessValues.Ignore } ignored && string.IsNullOrWhiteSpace(ignored.Reason))
        {
            (field, problem) = ("robots.reason", "is required when robots.txt is ignored");
        }
        else if (value.Robots?.Reason is { Length: > MaxReasonLength })
        {
            (field, problem) = ("robots.reason", $"must be at most {MaxReasonLength} characters");
        }
        else if (value.UserAgent is { } userAgent && !IsValidUserAgent(userAgent))
        {
            (field, problem) = ("userAgent", $"must be 1–{MaxUserAgentLength} printable ASCII characters");
        }
        else if (value.RenderPerHost is { } render && (render < 1 || render > ceilings.MaxRenderPerHost))
        {
            (field, problem) = ("renderPerHost", Between(1, ceilings.MaxRenderPerHost) + " (the ceiling is Render:MaxConcurrent)");
        }

        return field is null ? null : new PolitenessProblem(host, field, problem!);
    }

    private static bool OneOf(string? value, string first, string second) =>
        string.Equals(value, first, StringComparison.Ordinal) || string.Equals(value, second, StringComparison.Ordinal);

    private static string Between(double min, double max) =>
        string.Create(CultureInfo.InvariantCulture, $"must be between {min} and {max}");
}
