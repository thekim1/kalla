using System.Collections.Concurrent;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>A host's applied site profile (<c>content."SiteProfiles"</c> with its applied <c>SiteProfileVersions</c> row).</summary>
/// <param name="SiteProfileId">The profile row.</param>
/// <param name="Number">The applied version's number (<c>edit-site-profile</c>'s <c>baseVersion</c>).</param>
/// <param name="PresetId">The built-in preset the profile starts from.</param>
/// <param name="Profile">The applied profile.</param>
public sealed record AppliedSiteProfile(Guid SiteProfileId, int Number, string? PresetId, SiteProfile Profile);

/// <summary>Reads a host's applied site profile (connectors design §5.6).</summary>
public interface ISiteProfileReader
{
    /// <param name="classification">The level.</param>
    /// <param name="host">The profile's key (<see cref="SiteProfileHost"/>: the canonical host without a port), as <c>SiteProfiles.Host</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AppliedSiteProfile?> GetAppliedAsync(string classification, string host, CancellationToken cancellationToken);
}

/// <summary>
/// Whether a host's applied site profile marks <c>lastmod</c> reliable (§5.4, §5.6): only then does a traversal skip URLs
/// by their sitemap <c>lastmod</c> and does an incremental run stay incremental. A host without a profile is not reliable.
/// Answers are kept for <see cref="CacheFor"/> per level and host, so a crawl asks the database once a minute, not once per URL.
/// </summary>
public sealed class LastmodReliability(ISiteProfileReader profiles, TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(string, string), (bool Reliable, DateTimeOffset At)> _cache = new();

    /// <param name="classification">The level.</param>
    /// <param name="authority">The host, with or without <c>:port</c> (a frontier row's <c>Host</c>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<bool> IsReliableAsync(string classification, string authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(authority);
        var host = SiteProfileHost.OfAuthority(authority);
        var key = (classification, host);
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && now - cached.At < CacheFor)
        {
            return cached.Reliable;
        }

        var reliable = (await profiles.GetAppliedAsync(classification, host, cancellationToken))?.Profile.LastmodReliable == true;
        _cache[key] = (reliable, now);
        return reliable;
    }
}
