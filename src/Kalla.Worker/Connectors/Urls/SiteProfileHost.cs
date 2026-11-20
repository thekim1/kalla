namespace Kalla.Worker.Connectors.Urls;

/// <summary>
/// The key of a site profile (connectors design §5.6; data-model §2.2 <c>SiteProfiles.Host varchar(253)</c>): the canonical
/// host of §3.1 rule 2 (lower-case A-label, no trailing dot, IPv4 as a dotted quad) <b>without a port</b>, as the only
/// writer, <c>edit-site-profile</c>, takes it (its <c>host</c> pattern allows no <c>:port</c>). One profile serves its host
/// on every scheme and port, so a URL with the default port, an explicit default port or another port finds the same row.
/// Every reader and writer of <c>SiteProfiles.Host</c> derives the key here.
/// </summary>
public static class SiteProfileHost
{
    /// <summary>The key of an absolute URL, or null when its host is not a valid one.</summary>
    public static string? Of(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsAbsoluteUri && url.Host.Length > 0 ? UrlCanonicalizer.CanonicalHost(url.Host) : null;
    }

    /// <summary>The key of an absolute URL string (a document's <c>Url</c>), or null for none or one that does not parse.</summary>
    public static string? Of(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Of(uri) : null;

    /// <summary>The key of an authority (<c>host[:port]</c>, a frontier row's or <c>CrawlHosts</c>' <c>Host</c>); IPv6 literals keep their brackets.</summary>
    public static string OfAuthority(string authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var colon = authority.LastIndexOf(':');
        var host = colon > 0 && !authority.EndsWith(']') ? authority[..colon] : authority;
        return UrlCanonicalizer.CanonicalHost(host) ?? host;
    }
}
