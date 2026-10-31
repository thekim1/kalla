using System.Net;
using Microsoft.Extensions.Options;

namespace Ume.Rag.Worker.Connectors.Egress;

/// <summary>
/// Deployment settings of the worker's egress (<c>Crawl:*</c>, repo-and-deployment §3; connectors design §7). Run-time
/// crawl values (budgets, ports, blocked hosts) live in <c>Settings.Crawl</c>, not here.
/// </summary>
public sealed class CrawlOptions
{
    public const string SectionName = "Crawl";

    /// <summary>
    /// Comma list of CIDRs that lift the private-range block (env <c>Crawl__AllowedPrivateRanges</c>, fed from the same
    /// <c>UME_RAG_CRAWL_ALLOW</c> as the host firewall). Nothing else can lift it.
    /// </summary>
    public string? AllowedPrivateRanges { get; set; }

    /// <summary>
    /// <c>fetch</c> jobs a crawl keeps queued or running per host (<c>Crawl:FetchBatchPerHost</c>, connectors design §6.0):
    /// the source's traversal tops each host up to this many from its <c>pending</c> frontier rows as they finish.
    /// </summary>
    public int FetchBatchPerHost { get; set; } = 10;

    public IReadOnlyList<IPNetwork> AllowedNetworks() =>
        [.. (AllowedPrivateRanges ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(c => IPNetwork.Parse(c))];
}

/// <summary>Every entry of <see cref="CrawlOptions.AllowedPrivateRanges"/> must be a CIDR, a typo fails at start, not open; the batch is bounded.</summary>
public sealed class CrawlOptionsValidator : IValidateOptions<CrawlOptions>
{
    public const int MaxFetchBatchPerHost = 100;

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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
