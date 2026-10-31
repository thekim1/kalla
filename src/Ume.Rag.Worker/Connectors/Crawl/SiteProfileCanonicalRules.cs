using System.Collections.Concurrent;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>
/// The canonicaliser's per-host inputs from the hosts' applied site profiles (connectors design §3.1 rules 7 and 10, §5.6):
/// the profile's <c>ignoreQueryParams</c> (a trailing <c>*</c> is a prefix) and its identity rule when <c>verified</c>. A
/// host's answer is kept for <see cref="CacheFor"/> per level, as <see cref="LastmodReliability"/> does, so an applied
/// profile version reaches the crawl within a minute.
/// </summary>
public sealed class SiteProfileCanonicalRules(ISiteProfileReader profiles, TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(string, string), (HostCanonicalRules? Rules, DateTimeOffset At)> _cache = new();

    /// <summary>The rules of a profile: null when it neither drops parameters nor has a verified identity rule.</summary>
    public static HostCanonicalRules? From(SiteProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = profile.IdentityRule is { Verified: true } rule ? PathIdentityRule.Create(rule.Pattern, rule.Replacement) : null;
        return identity is null && profile.IgnoreQueryParams.Count == 0 ? null : new HostCanonicalRules(profile.IgnoreQueryParams, identity);
    }

    /// <param name="classification">The level.</param>
    /// <param name="hosts">Lower-case hosts without a port (<see cref="CrawlScope.Hosts"/>).</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The rules of the hosts that have any, by host.</returns>
    public async ValueTask<IReadOnlyDictionary<string, HostCanonicalRules>> ForHostsAsync(string classification, IEnumerable<string> hosts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(hosts);
        var now = time.GetUtcNow();
        var rules = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal);
        foreach (var host in hosts)
        {
            var key = (classification, host);
            if (!_cache.TryGetValue(key, out var cached) || now - cached.At >= CacheFor)
            {
                var applied = await profiles.GetAppliedAsync(classification, host, cancellationToken);
                cached = (applied is null ? null : From(applied.Profile), now);
                _cache[key] = cached;
            }

            if (cached.Rules is { } found)
            {
                rules[host] = found;
            }
        }

        return rules;
    }
}
