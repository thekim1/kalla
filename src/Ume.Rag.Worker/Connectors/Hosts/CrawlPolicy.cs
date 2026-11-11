using Ume.Rag.Pipelines.Crawl;

namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary><c>CrawlHosts.Kind</c>: decides the host's budget (§6.3) and its <c>noindex</c>/<c>nofollow</c> handling (§5.2).</summary>
public enum HostKind
{
    External,
    Municipal,
}

/// <summary>
/// The run-time crawl values the host budget reads from <c>Settings.Crawl</c> (connectors design §5.2, §6.3):
/// <c>MunicipalHosts</c> (exact names or <c>*.</c> suffixes, e.g. <c>umea.se</c>, <c>*.umea.se</c>) and
/// <c>HostOverrides</c> (the politeness profile per host, §6.6), and <c>IncompleteRunsAlert</c> for the incomplete-traversal
/// alert. Blocked hosts and ports are the egress policy's (<see cref="Egress.EgressPolicy"/>).
/// </summary>
public sealed record CrawlPolicy(IReadOnlyList<string> MunicipalHosts, IReadOnlyDictionary<string, HostPoliteness> HostOverrides)
{
    public static CrawlPolicy Default { get; } = new([], new Dictionary<string, HostPoliteness>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The deployment's ceilings the profiles are clamped to (<c>Crawl:MaxHostRate</c>, <c>Crawl:MaxHostConnections</c>).</summary>
    public PolitenessCeilings Ceilings { get; init; } = PolitenessCeilings.Default;

    /// <summary><c>Settings.Crawl.IncompleteRunsAlert</c>: incomplete traversals in a row that raise the alert (§5.4).</summary>
    public int IncompleteRunsAlert { get; init; } = Deletion.TraversalCompleteness.DefaultAlertThreshold;

    /// <summary><c>Crawl:UserAgent</c>: sent to every host whose profile sets none (§4, §6.6).</summary>
    public string UserAgent { get; init; } = Http.CrawlerIdentity.DefaultUserAgent;

    /// <summary>What robots.txt groups, meta robots and <c>X-Robots-Tag</c> prefixes are matched against (§5.1), whatever a host sends.</summary>
    public string ProductToken => Http.CrawlerIdentity.ProductTokenOf(UserAgent);

    /// <summary>The User-Agent sent to <paramref name="host"/>: its profile's, else the crawler's own.</summary>
    public string UserAgentOf(string host) => ProfileOf(host).UserAgent ?? UserAgent;

    /// <summary>Whether <paramref name="host"/> (lower-case A-label, a <c>:port</c> ignored) is a municipal host.</summary>
    public HostKind KindOf(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host = WithoutPort(host);
        foreach (var pattern in MunicipalHosts)
        {
            var matches = pattern.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                return HostKind.Municipal;
            }
        }

        return HostKind.External;
    }

    /// <summary>
    /// The effective politeness of <paramref name="host"/> (a <c>:port</c> ignored: overrides are keyed by host name, V37):
    /// its kind's defaults with the admin's override, within the ceilings.
    /// </summary>
    public PolitenessProfile ProfileOf(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = WithoutPort(host);
        return PolitenessProfile.Resolve(KindOf(name), HostOverrides.TryGetValue(name, out var stored) ? stored : null, Ceilings);
    }

    private static string WithoutPort(string host)
    {
        var colon = host.LastIndexOf(':');
        return colon > 0 && !host.EndsWith(']') ? host[..colon] : host;
    }
}

/// <summary>Where the crawl reads <see cref="CrawlPolicy"/> (<c>Settings.Crawl</c>, <c>PlatformCrawlSettingsSource</c>).</summary>
public interface ICrawlPolicySource
{
    CrawlPolicy Current { get; }
}
