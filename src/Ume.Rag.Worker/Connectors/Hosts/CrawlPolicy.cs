namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary><c>CrawlHosts.Kind</c>: decides the host's budget (§6.3) and its <c>noindex</c>/<c>nofollow</c> handling (§5.2).</summary>
public enum HostKind
{
    External,
    Municipal,
}

/// <summary>A platform admin's budget for one host (<c>Settings.Crawl.HostOverrides</c>); null members keep the default.</summary>
public sealed record HostOverride(double? RequestsPerSecond = null, int? MaxConnections = null);

/// <summary>
/// The run-time crawl values the host budget reads from <c>Settings.Crawl</c> (connectors design §5.2, §6.3):
/// <c>MunicipalHosts</c> (exact names or <c>*.</c> suffixes, e.g. <c>umea.se</c>, <c>*.umea.se</c>) and
/// <c>HostOverrides</c>. Blocked hosts and ports are the egress policy's (<see cref="Egress.EgressPolicy"/>).
/// </summary>
public sealed record CrawlPolicy(IReadOnlyList<string> MunicipalHosts, IReadOnlyDictionary<string, HostOverride> HostOverrides)
{
    public static CrawlPolicy Default { get; } = new([], new Dictionary<string, HostOverride>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="host"/> (lower-case A-label, a <c>:port</c> ignored) is a municipal host.</summary>
    public HostKind KindOf(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith(']'))
        {
            host = host[..colon];
        }

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

    public HostOverride? OverrideOf(string host) => HostOverrides.TryGetValue(host, out var value) ? value : null;
}

/// <summary>Where the crawl reads <see cref="CrawlPolicy"/> (<c>Settings.Crawl</c>, <c>PlatformCrawlSettingsSource</c>).</summary>
public interface ICrawlPolicySource
{
    CrawlPolicy Current { get; }
}
