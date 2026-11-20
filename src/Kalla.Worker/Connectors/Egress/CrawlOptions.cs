using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using Kalla.Pipelines.Crawl;
using Kalla.Worker.Connectors.Deletion;

namespace Kalla.Worker.Connectors.Egress;

/// <summary>
/// Deployment settings of the worker's egress (<c>Crawl:*</c>, repo-and-deployment §3; connectors design §7). Run-time
/// crawl values (budgets, ports, blocked hosts) live in <c>Settings.Crawl</c>, not here.
/// </summary>
public sealed class CrawlOptions
{
    public const string SectionName = "Crawl";

    /// <summary>
    /// Comma list of CIDRs that lift the private-range block (env <c>Crawl__AllowedPrivateRanges</c>, fed from the same
    /// <c>KALLA_CRAWL_ALLOW</c> as the host firewall). Nothing else can lift it.
    /// </summary>
    public string? AllowedPrivateRanges { get; set; }

    /// <summary>
    /// <c>fetch</c> jobs a crawl keeps queued or running per host (<c>Crawl:FetchBatchPerHost</c>, connectors design §6.0):
    /// the source's traversal tops each host up to this many from its <c>pending</c> frontier rows as they finish.
    /// </summary>
    public int FetchBatchPerHost { get; set; } = 10;

    /// <summary>
    /// The seed rule of traversal completeness (<c>Crawl:CompletenessRule</c>, weakness review §3 "Phasing"):
    /// <c>Ratio</c> (P1b, ≥ 99 % of seeds resolved) or <c>Strict</c> (P1a, no failed seed).
    /// </summary>
    public CompletenessRule CompletenessRule { get; set; } = CompletenessRule.Ratio;

    /// <summary>
    /// The highest rate a host's politeness profile may set (<c>Crawl:MaxHostRate</c>, req/s, connectors design §6.6): the
    /// admin refuses more (V37) and the worker clamps to it, so a typo cannot turn the crawler into a load test.
    /// </summary>
    public double MaxHostRate { get; set; } = PolitenessCeilings.DefaultMaxHostRate;

    /// <summary>The most connections a host's politeness profile may set (<c>Crawl:MaxHostConnections</c>, §6.6).</summary>
    public int MaxHostConnections { get; set; } = PolitenessCeilings.DefaultMaxHostConnections;

    /// <summary>
    /// The most a host's <c>renderPerHost</c> may be: <c>Render:MaxConcurrent</c>, set from that key at start (as the admin
    /// API does), not from this section.
    /// </summary>
    public int MaxRenderPerHost { get; set; } = PolitenessCeilings.DefaultMaxRenderPerHost;

    /// <summary>
    /// The crawler's User-Agent (<c>Crawl:UserAgent</c>, connectors design §4), for example
    /// <c>ExampleBot/1.0 (+https://www.example.org/bot)</c>. Its product token, up to the first <c>/</c> or space, is the
    /// name robots.txt rules address (§5.1). A host's politeness profile may send another (§6.6).
    /// </summary>
    public string UserAgent { get; set; } = Http.CrawlerIdentity.DefaultUserAgent;

    /// <summary>The ceilings the worker clamps host profiles to.</summary>
    public PolitenessCeilings Ceilings() => new(MaxHostRate, MaxHostConnections, MaxRenderPerHost);

    public IReadOnlyList<IPNetwork> AllowedNetworks() =>
        [.. (AllowedPrivateRanges ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(c => IPNetwork.Parse(c))];
}

/// <summary>Every entry of <see cref="CrawlOptions.AllowedPrivateRanges"/> must be a CIDR, a typo fails at start, not open; the batch is bounded.</summary>
public sealed class CrawlOptionsValidator : IValidateOptions<CrawlOptions>
{
    public const int MaxFetchBatchPerHost = 100;

    /// <summary><c>CrawlHosts.RequestsPerSecond</c> is <c>numeric(5,2)</c>.</summary>
    public const double MaxHostRateLimit = 999;

    public const int MaxHostConnectionsLimit = 256;

    public ValidateOptionsResult Validate(string? name, CrawlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = (options.AllowedPrivateRanges ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(c => !IPNetwork.TryParse(c, out _))
            .Select(c => $"Crawl:AllowedPrivateRanges contains '{c}', which is not a CIDR such as 10.20.0.0/16.")
            .ToList();
        if (options.FetchBatchPerHost is < 1 or > MaxFetchBatchPerHost)
        {
            failures.Add($"Crawl:FetchBatchPerHost must be between 1 and {MaxFetchBatchPerHost}.");
        }

        if (!(options.MaxHostRate >= HostPolitenessRules.MinRequestsPerSecond && options.MaxHostRate <= MaxHostRateLimit))
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"Crawl:MaxHostRate must be between {HostPolitenessRules.MinRequestsPerSecond} and {MaxHostRateLimit}."));
        }

        if (!Http.CrawlerIdentity.IsValid(options.UserAgent))
        {
            failures.Add($"Crawl:UserAgent must be 1–{HostPolitenessRules.MaxUserAgentLength} printable ASCII characters and start with a product token of letters, '_' or '-', such as ExampleBot/1.0.");
        }

        if (options.MaxHostConnections is < 1 or > MaxHostConnectionsLimit)
        {
            failures.Add($"Crawl:MaxHostConnections must be between 1 and {MaxHostConnectionsLimit}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
