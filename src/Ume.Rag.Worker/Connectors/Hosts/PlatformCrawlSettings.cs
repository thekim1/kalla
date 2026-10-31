using System.Text.Json;
using Ume.Rag.Worker.Connectors.Egress;

namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary>
/// Reads the <c>config."Settings"."Crawl"</c> <c>jsonb</c> group (data-model §2.1; connectors design §5.2, §6.3, §7): the
/// members the fetch path uses are <c>allowedPorts</c> (int[]), <c>blockedHosts</c> (string[]), <c>municipalHosts</c>
/// (string[]) and <c>hostOverrides</c> (an object by host of <c>{ requestsPerSecond, maxConnections }</c>), and <c>incompleteRunsAlert</c> (int, the alert of §5.4). Other members
/// belong to other readers and are ignored; absent members keep the defaults.
/// </summary>
public static class PlatformCrawlSettings
{
    public static (EgressPolicy Egress, CrawlPolicy Crawl) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (EgressPolicy.Default, CrawlPolicy.Default);
        }

        var group = JsonSerializer.Deserialize<CrawlGroup>(json, JsonSerializerOptions.Web) ?? new CrawlGroup();
        var egress = new EgressPolicy(
            new HashSet<int>(group.AllowedPorts ?? []),
            new HashSet<string>(group.BlockedHosts ?? [], StringComparer.OrdinalIgnoreCase));
        var overrides = new Dictionary<string, HostOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, value) in group.HostOverrides ?? [])
        {
            overrides[host] = value;
        }

        var crawl = new CrawlPolicy(group.MunicipalHosts ?? [], overrides);
        return (egress, group.IncompleteRunsAlert is int alert and > 0 ? crawl with { IncompleteRunsAlert = alert } : crawl);
    }

    private sealed record CrawlGroup
    {
        public int[]? AllowedPorts { get; init; }

        public string[]? BlockedHosts { get; init; }

        public string[]? MunicipalHosts { get; init; }

        public Dictionary<string, HostOverride>? HostOverrides { get; init; }

        public int? IncompleteRunsAlert { get; init; }
    }
}
