using System.Text.Json;
using Kalla.Pipelines.Crawl;
using Kalla.Worker.Connectors.Egress;

namespace Kalla.Worker.Connectors.Hosts;

/// <summary>
/// Reads the <c>config."Settings"."Crawl"</c> <c>jsonb</c> group (data-model §2.1; connectors design §5.2, §6.3, §7): the
/// members the fetch path uses are <c>allowedPorts</c> (int[]), <c>blockedHosts</c> (string[]), <c>municipalHosts</c>
/// (string[]) and <c>hostOverrides</c> (the politeness profile by host, <see cref="HostPoliteness"/>, §6.6), and
/// <c>incompleteRunsAlert</c> (int, the alert of §5.4). Other members belong to other readers and are ignored; absent
/// members keep the defaults. The profiles are clamped to the deployment's ceilings when they are resolved.
/// </summary>
public static class PlatformCrawlSettings
{
    public static (EgressPolicy Egress, CrawlPolicy Crawl) Parse(string? json, PolitenessCeilings? ceilings = null)
    {
        ceilings ??= PolitenessCeilings.Default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return (EgressPolicy.Default, CrawlPolicy.Default with { Ceilings = ceilings });
        }

        var group = JsonSerializer.Deserialize<CrawlGroup>(json, JsonSerializerOptions.Web) ?? new CrawlGroup();
        var egress = new EgressPolicy(
            new HashSet<int>(group.AllowedPorts ?? []),
            new HashSet<string>(group.BlockedHosts ?? [], StringComparer.OrdinalIgnoreCase));
        var overrides = new Dictionary<string, HostPoliteness>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, value) in group.HostOverrides ?? [])
        {
            overrides[host] = value;
        }

        var crawl = new CrawlPolicy(group.MunicipalHosts ?? [], overrides) { Ceilings = ceilings };
        return (egress, group.IncompleteRunsAlert is int alert and > 0 ? crawl with { IncompleteRunsAlert = alert } : crawl);
    }

    private sealed record CrawlGroup
    {
        public int[]? AllowedPorts { get; init; }

        public string[]? BlockedHosts { get; init; }

        public string[]? MunicipalHosts { get; init; }

        public Dictionary<string, HostPoliteness>? HostOverrides { get; init; }

        public int? IncompleteRunsAlert { get; init; }
    }
}
