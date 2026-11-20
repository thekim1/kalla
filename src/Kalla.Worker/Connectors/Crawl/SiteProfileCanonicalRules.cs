using System.Collections.Concurrent;
using Kalla.Pipelines.SiteProfiles;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Connectors.Crawl;

/// <summary>What the scope hosts' applied site profiles change in a crawl, by host (<see cref="SiteProfileHost"/> keys).</summary>
/// <param name="Canonical">The canonicaliser's rules of the hosts that have any (§3.1 rules 7 and 10).</param>
/// <param name="ExcludedTypes">The profiles' <c>excludedTypes</c> (§5.2), for every host with a profile.</param>
public sealed record HostCrawlRules(IReadOnlyDictionary<string, HostCanonicalRules> Canonical, IReadOnlyDictionary<string, IReadOnlyList<string>> ExcludedTypes);

/// <summary>
/// The crawl's per-host inputs from the hosts' applied site profiles (connectors design §3.1 rules 7 and 10, §5.2, §5.6):
/// the canonicaliser's <c>ignoreQueryParams</c> (a trailing <c>*</c> is a prefix) and identity rule when <c>verified</c>,
/// and the scope's <c>excludedTypes</c>. A host's profile is kept for <see cref="CacheFor"/> per level, as
/// <see cref="LastmodReliability"/> does, so an applied profile version reaches the crawl within a minute; its rules are
/// built once per read, so the identity rule's expression is compiled once, not per fetch.
/// </summary>
public sealed class SiteProfileCanonicalRules(ISiteProfileReader profiles, TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(string, string), (SiteProfile? Profile, HostCanonicalRules? Rules, DateTimeOffset At)> _cache = new();

    /// <summary>The rules of a profile: null when it neither drops parameters nor has a verified identity rule.</summary>
    public static HostCanonicalRules? From(SiteProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = profile.IdentityRule is { Verified: true } rule ? PathIdentityRule.Create(rule.Pattern, rule.Replacement) : null;
        return identity is null && profile.IgnoreQueryParams.Count == 0 ? null : new HostCanonicalRules(profile.IgnoreQueryParams, identity);
    }

    /// <param name="classification">The level.</param>
    /// <param name="hosts">Lower-case hosts without a port (<see cref="CrawlScope.Hosts"/>, the profiles' keys).</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The hosts' canonicaliser rules and excluded types, by host.</returns>
    public ValueTask<HostCrawlRules> ForHostsAsync(string classification, IEnumerable<string> hosts, CancellationToken cancellationToken) =>
        ForHostsAsync(classification, hosts, fresh: false, cancellationToken);

    /// <summary>As <see cref="ForHostsAsync(string, IEnumerable{string}, CancellationToken)"/>; <paramref name="fresh"/> reads every host's profile again and caches it.</summary>
    public async ValueTask<HostCrawlRules> ForHostsAsync(string classification, IEnumerable<string> hosts, bool fresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(hosts);
        var now = time.GetUtcNow();
        var canonical = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var host in hosts)
        {
            var key = (classification, host);
            if (fresh || !_cache.TryGetValue(key, out var cached) || now - cached.At >= CacheFor)
            {
                var read = (await profiles.GetAppliedAsync(classification, host, cancellationToken))?.Profile;
                cached = (read, read is null ? null : From(read), now);
                _cache[key] = cached;
            }

            if (cached.Profile is not { } profile)
            {
                continue;
            }

            excluded[host] = profile.ExcludedTypes;
            if (cached.Rules is { } found)
            {
                canonical[host] = found;
            }
        }

        return new HostCrawlRules(canonical, excluded);
    }
}
