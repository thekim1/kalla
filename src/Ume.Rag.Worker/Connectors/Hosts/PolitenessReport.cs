using Ume.Rag.Pipelines.Crawl;

namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary>
/// One scope host with a non-default politeness profile in the crawl report (connectors design §5.5, §6.6): its preset
/// and only the members that differ from what a host of its kind gets by default (null otherwise), so the source's run
/// page can say "crawled at 10 req/s, robots.txt ignored: own site, web team consent".
/// </summary>
public sealed record PolitenessReportEntry(string Host, string Preset)
{
    public double? RequestsPerSecond { get; init; }

    public int? MaxConnections { get; init; }

    /// <summary><c>honour</c> or <c>ignore</c>.</summary>
    public string? CrawlDelay { get; init; }

    public bool? AdaptToLatency { get; init; }

    /// <summary><c>source</c> or <c>any</c>.</summary>
    public string? CrawlWindow { get; init; }

    public int? MaxRetryAfterSeconds { get; init; }

    public double? MaxBackoffFactor { get; init; }

    /// <summary>True when the circuit breaker is off.</summary>
    public bool? BreakerOff { get; init; }

    public double? BreakerThreshold { get; init; }

    public int? BreakerPauseMinutes { get; init; }

    /// <summary>The platform admin's reason when robots.txt is ignored.</summary>
    public string? RobotsIgnoredReason { get; init; }

    public string? UserAgent { get; init; }

    public int? RenderPerHost { get; init; }

    /// <summary>The scope hosts whose profile is not their kind's default, by host name.</summary>
    public static IReadOnlyList<PolitenessReportEntry> For(IEnumerable<string> hosts, CrawlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(policy);
        var entries = new List<PolitenessReportEntry>();
        foreach (var host in hosts.Order(StringComparer.Ordinal))
        {
            var kind = policy.KindOf(host);
            var profile = policy.ProfileOf(host);
            if (profile.IsDefaultFor(kind))
            {
                continue;
            }

            var basis = PolitenessProfile.DefaultOf(kind);
            entries.Add(new PolitenessReportEntry(host, profile.Preset)
            {
                RequestsPerSecond = Differs(profile.RequestsPerSecond, basis.RequestsPerSecond),
                MaxConnections = Differs(profile.MaxConnections, basis.MaxConnections),
                CrawlDelay = profile.HonourCrawlDelay == basis.HonourCrawlDelay ? null : profile.HonourCrawlDelay ? PolitenessValues.Honour : PolitenessValues.Ignore,
                AdaptToLatency = Differs(profile.AdaptToLatency, basis.AdaptToLatency),
                CrawlWindow = profile.AnyTime == basis.AnyTime ? null : profile.AnyTime ? PolitenessValues.Any : PolitenessValues.Source,
                MaxRetryAfterSeconds = Differs((int)profile.Backoff.MaxRetryAfter.TotalSeconds, (int)basis.Backoff.MaxRetryAfter.TotalSeconds),
                MaxBackoffFactor = Differs(profile.Backoff.MaxFactor, basis.Backoff.MaxFactor),
                BreakerOff = profile.Breaker is null ? true : null,
                BreakerThreshold = profile.Breaker is { } breaker ? Differs(breaker.Threshold, BreakerRule.Default.Threshold) : null,
                BreakerPauseMinutes = profile.Breaker is { } paused ? Differs((int)paused.Pause.TotalMinutes, (int)BreakerRule.Default.Pause.TotalMinutes) : null,
                RobotsIgnoredReason = profile.RobotsIgnoredReason,
                UserAgent = string.Equals(profile.UserAgent, basis.UserAgent, StringComparison.Ordinal) ? null : profile.UserAgent,
                RenderPerHost = Differs(profile.RenderPerHost, basis.RenderPerHost),
            });
        }

        return entries;
    }

    private static T? Differs<T>(T value, T defaultValue)
        where T : struct => EqualityComparer<T>.Default.Equals(value, defaultValue) ? null : value;
}
