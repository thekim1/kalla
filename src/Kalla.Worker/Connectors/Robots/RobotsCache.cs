using System.Collections.Concurrent;
using System.Net;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Connectors.Robots;

/// <summary><c>CrawlHosts.RobotsStatus</c>: fetched and parsed, absent (4xx), or unreachable (5xx, network).</summary>
public enum RobotsStatus
{
    Ok,
    Missing,
    Unreachable,
}

/// <param name="Rules">The rules that apply.</param>
/// <param name="Status">How the fetch ended.</param>
/// <param name="FetchedAt">When.</param>
/// <param name="EgressRefusal">Set when the guard refused robots.txt itself (<c>egress.*</c>): the host's pages are refused for the same reason.</param>
public sealed record RobotsEntry(RobotsTxt Rules, RobotsStatus Status, DateTimeOffset FetchedAt, string? EgressRefusal = null)
{
    /// <summary>
    /// The host's politeness profile ignores robots.txt (§6.6): nothing is disallowed and an unreachable robots.txt pauses
    /// nothing, but its <c>Sitemap:</c> lines and <c>Crawl-delay</c> are still read. Set per answer, never cached.
    /// </summary>
    public bool Ignored { get; init; }
}

/// <summary>
/// robots.txt per origin (scheme, host, port), fetched through the egress guard and kept for 24 h (connectors design §5.1):
/// 2xx parsed (the first 500 KiB), 4xx allow all, 5xx, 429 and network failures disallow all; an unreachable answer is
/// kept for 10 minutes only, so the next run asks again. A robots.txt the egress guard refused is not kept at all: the
/// guard's rules follow <c>Settings.Crawl</c>, so a port allowed or a host unblocked there counts from the next request (#360).
/// In memory per worker instance: the <c>CrawlHosts</c> row and the <c>crawl/{host}/robots.txt</c> blob take over with the
/// database (schema part 1). A host whose politeness profile ignores robots.txt (§6.6) is never refused by it, for
/// seeds and redirect hops alike; its robots.txt is still fetched for the <c>Sitemap:</c> lines.
/// </summary>
public sealed class RobotsCache(HttpFetcher fetcher, ICrawlPolicySource policy, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan UnreachableLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsEntry>>> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Null when robots.txt of <paramref name="url"/>'s origin allows it, or when the host's profile ignores robots.txt
    /// (robots.txt is then not asked); else <c>robots.disallow</c>, or the guard's <c>egress.*</c> reason when robots.txt
    /// itself was refused (<see cref="HttpFetchRequest.Robots"/>).
    /// </summary>
    public async ValueTask<string?> CheckAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (policy.Current.ProfileOf(url.Authority).IgnoresRobots)
        {
            return null;
        }

        var entry = await GetAsync(url, cancellationToken);
        return entry.EgressRefusal ?? (entry.Rules.IsAllowed(url) ? null : "robots.disallow");
    }

    public async Task<RobotsEntry> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var entry = await CachedAsync(url, cancellationToken);
        return policy.Current.ProfileOf(url.Authority).IgnoresRobots ? entry with { Ignored = true } : entry;
    }

    private async Task<RobotsEntry> CachedAsync(Uri url, CancellationToken cancellationToken)
    {
        var origin = url.GetLeftPart(UriPartial.Authority);
        while (true)
        {
            var lazy = _entries.GetOrAdd(origin, o => new Lazy<Task<RobotsEntry>>(() => FetchAsync(o, CancellationToken.None)));
            RobotsEntry entry;
            try
            {
                entry = await lazy.Value.WaitAsync(cancellationToken);
            }
            catch when (lazy.Value.IsFaulted)
            {
                // Not cached: the next caller fetches again.
                _entries.TryRemove(new KeyValuePair<string, Lazy<Task<RobotsEntry>>>(origin, lazy));
                throw;
            }

            if (entry.EgressRefusal is not null)
            {
                // Not cached either: the guard's rules follow Settings.Crawl, so the next caller asks the guard again.
                _entries.TryRemove(new KeyValuePair<string, Lazy<Task<RobotsEntry>>>(origin, lazy));
                return entry;
            }

            var lifetime = entry.Status == RobotsStatus.Unreachable ? UnreachableLifetime : Lifetime;
            if (time.GetUtcNow() - entry.FetchedAt < lifetime)
            {
                return entry;
            }

            _entries.TryRemove(new KeyValuePair<string, Lazy<Task<RobotsEntry>>>(origin, lazy));
        }
    }

    private async Task<RobotsEntry> FetchAsync(string origin, CancellationToken cancellationToken)
    {
        var request = new HttpFetchRequest(new Uri(origin + "/robots.txt"), EgressPurpose.Robots, null, null,
            CanonicalizerOptions.Default, null, RobotsTxt.MaxBytes, Truncate: true);
        var result = await fetcher.FetchAsync(request, cancellationToken);
        var now = time.GetUtcNow();
        switch (result)
        {
            case HttpFetchResult.Ok ok:
                await using (ok.Body)
                {
                    await using var stream = ok.Body.OpenRead();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken);
                    return new(RobotsTxt.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), policy.Current.ProductToken), RobotsStatus.Ok, now);
                }

            case HttpFetchResult.Refused { Outcome: RunItemOutcome.Blocked, Reason: var reason } when reason.StartsWith("egress.", StringComparison.Ordinal):
                return new(RobotsTxt.DisallowAll, RobotsStatus.Unreachable, now, reason);
            case HttpFetchResult.Status { Code: var code } when (int)code is >= 400 and < 500 && code != HttpStatusCode.TooManyRequests:
                return new(RobotsTxt.AllowAll, RobotsStatus.Missing, now);
            default:
                return new(RobotsTxt.DisallowAll, RobotsStatus.Unreachable, now);
        }
    }
}
