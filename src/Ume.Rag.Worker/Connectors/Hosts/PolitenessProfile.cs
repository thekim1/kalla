using Ume.Rag.Pipelines.Crawl;
using Ume.Rag.Worker.Connectors.Http;

namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary>The circuit breaker of one host (§6.4): <see cref="Threshold"/> of the last 100 responses failing pauses the host for <see cref="Pause"/>.</summary>
public sealed record BreakerRule(double Threshold, TimeSpan Pause)
{
    public static BreakerRule Default { get; } = new(0.5, TimeSpan.FromMinutes(30));

    /// <summary>Failures among the last <paramref name="window"/> responses that trip the breaker (50 of 100 by default).</summary>
    public int FailuresOf(int window) => Math.Max(1, (int)Math.Ceiling(Math.Round(Threshold * window, 6))); // 0.28 × 100 is 28.000000000000004
}

/// <summary>The caps of a 429/503 (§6.4): the longest <c>Retry-After</c> waited for, and the highest back-off factor.</summary>
public sealed record BackoffLimits(TimeSpan MaxRetryAfter, double MaxFactor)
{
    public static BackoffLimits Default { get; } = new(HostBudgetPolicy.MaxRetryAfter, HostBudgetPolicy.MaxBackoff);
}

/// <summary>
/// One host's effective politeness (connectors design §6.6): its preset's values with the platform admin's members from
/// <c>Settings.Crawl.HostOverrides</c> on top, clamped to the deployment's ceilings. The defaults are the presets
/// <c>polite</c> (external hosts) and <c>municipal</c> (hosts in <c>MunicipalHosts</c>); robots.txt and the User-Agent are
/// never part of a preset.
/// </summary>
public sealed record PolitenessProfile
{
    public required string Preset { get; init; }

    public required double RequestsPerSecond { get; init; }

    public required int MaxConnections { get; init; }

    /// <summary>robots.txt <c>Crawl-delay</c> may lower the rate (capped at 60 s); false: ignored.</summary>
    public bool HonourCrawlDelay { get; init; } = true;

    /// <summary>The latency term of the interval (§6.3); false: the rate alone paces the host.</summary>
    public bool AdaptToLatency { get; init; } = true;

    /// <summary><c>crawlWindow: any</c>: the host's fetches are released outside the source's night window too (§6.5).</summary>
    public bool AnyTime { get; init; }

    public BackoffLimits Backoff { get; init; } = BackoffLimits.Default;

    /// <summary>Null: the breaker is off.</summary>
    public BreakerRule? Breaker { get; init; } = BreakerRule.Default;

    /// <summary>Set when robots.txt is ignored for the host: the platform admin's reason. Its <c>Sitemap:</c> lines are still read.</summary>
    public string? RobotsIgnoredReason { get; init; }

    public string UserAgent { get; init; } = HttpFetcher.UserAgent;

    public int RenderPerHost { get; init; } = 1;

    public bool IgnoresRobots => RobotsIgnoredReason is not null;

    public static PolitenessProfile Polite { get; } = new()
    {
        Preset = PolitenessValues.Polite,
        RequestsPerSecond = HostBudgetPolicy.ExternalRequestsPerSecond,
        MaxConnections = HostBudgetPolicy.ExternalConnections,
    };

    public static PolitenessProfile Municipal { get; } = new()
    {
        Preset = PolitenessValues.Municipal,
        RequestsPerSecond = HostBudgetPolicy.MunicipalRequestsPerSecond,
        MaxConnections = HostBudgetPolicy.MunicipalConnections,
    };

    /// <summary>10 req/s, 4 connections, <c>Crawl-delay</c> and latency ignored, any time; back-off and breaker unchanged.</summary>
    public static PolitenessProfile Fast { get; } = new()
    {
        Preset = PolitenessValues.Fast,
        RequestsPerSecond = HostBudgetPolicy.MunicipalRequestsPerSecond,
        MaxConnections = HostBudgetPolicy.MunicipalConnections,
        HonourCrawlDelay = false,
        AdaptToLatency = false,
        AnyTime = true,
    };

    /// <summary>The profile a host of <paramref name="kind"/> has without an override.</summary>
    public static PolitenessProfile DefaultOf(HostKind kind) => kind == HostKind.Municipal ? Municipal : Polite;

    /// <summary>Whether this profile differs from what a host of <paramref name="kind"/> gets without an override.</summary>
    public bool IsDefaultFor(HostKind kind) => this == DefaultOf(kind);

    /// <summary>
    /// The preset (<c>custom</c> and an absent or unknown preset start from the host kind's defaults), then every member
    /// the admin set, clamped to the ranges of <see cref="HostPolitenessRules"/> and the ceilings: the admin refuses
    /// anything outside them (V37), so a clamp only meets a row written past the admin or a lowered ceiling. A robots
    /// <c>ignore</c> without a reason and an invalid User-Agent keep the defaults.
    /// </summary>
    public static PolitenessProfile Resolve(HostKind kind, HostPoliteness? stored, PolitenessCeilings ceilings)
    {
        ArgumentNullException.ThrowIfNull(ceilings);
        var basis = stored?.Preset switch
        {
            PolitenessValues.Polite => Polite,
            PolitenessValues.Municipal => Municipal,
            PolitenessValues.Fast => Fast,
            _ => DefaultOf(kind),
        };
        var profile = basis with { Preset = stored?.Preset is PolitenessValues.Custom ? PolitenessValues.Custom : basis.Preset };
        if (stored is not null)
        {
            profile = profile with
            {
                RequestsPerSecond = stored.RequestsPerSecond ?? profile.RequestsPerSecond,
                MaxConnections = stored.MaxConnections ?? profile.MaxConnections,
                HonourCrawlDelay = stored.CrawlDelay switch
                {
                    PolitenessValues.Honour => true,
                    PolitenessValues.Ignore => false,
                    _ => profile.HonourCrawlDelay,
                },
                AdaptToLatency = stored.AdaptToLatency ?? profile.AdaptToLatency,
                AnyTime = stored.CrawlWindow switch
                {
                    PolitenessValues.Any => true,
                    PolitenessValues.Source => false,
                    _ => profile.AnyTime,
                },
                Backoff = new BackoffLimits(
                    stored.Backoff?.MaxRetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, HostPolitenessRules.MaxRetryAfterSeconds)) : profile.Backoff.MaxRetryAfter,
                    stored.Backoff?.MaxFactor is { } factor ? Math.Clamp(factor, 1, HostPolitenessRules.MaxBackoffFactor) : profile.Backoff.MaxFactor),
                Breaker = stored.CircuitBreaker switch
                {
                    null => profile.Breaker,
                    { Off: true } => null,
                    { } breaker => new BreakerRule(
                        Math.Clamp(breaker.Threshold ?? BreakerRule.Default.Threshold, HostPolitenessRules.MinBreakerThreshold, HostPolitenessRules.MaxBreakerThreshold),
                        breaker.PauseMinutes is { } minutes ? TimeSpan.FromMinutes(Math.Clamp(minutes, 1, HostPolitenessRules.MaxPauseMinutes)) : BreakerRule.Default.Pause),
                },
                RobotsIgnoredReason = stored.Robots is { Mode: PolitenessValues.Ignore, Reason: { } reason } && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : null,
                UserAgent = HostPolitenessRules.IsValidUserAgent(stored.UserAgent) ? stored.UserAgent! : HttpFetcher.UserAgent,
                RenderPerHost = stored.RenderPerHost ?? profile.RenderPerHost,
            };
        }

        return profile with
        {
            RequestsPerSecond = Math.Clamp(profile.RequestsPerSecond, HostPolitenessRules.MinRequestsPerSecond, ceilings.MaxHostRate),
            MaxConnections = Math.Clamp(profile.MaxConnections, 1, ceilings.MaxHostConnections),
            RenderPerHost = Math.Clamp(profile.RenderPerHost, 1, ceilings.MaxRenderPerHost),
        };
    }
}
