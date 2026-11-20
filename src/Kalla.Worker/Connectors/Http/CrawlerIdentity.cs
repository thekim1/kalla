using Kalla.Pipelines.Crawl;

namespace Kalla.Worker.Connectors.Http;

/// <summary>
/// Who the crawler says it is (connectors design §4, §5.1): the User-Agent it sends (<c>Crawl:UserAgent</c>, a host's
/// politeness profile may send another, §6.6) and the product token robots.txt groups, meta robots and
/// <c>X-Robots-Tag</c> prefixes are matched against: the User-Agent up to its first <c>/</c> or space.
/// </summary>
public static class CrawlerIdentity
{
    public const string DefaultUserAgent = "UmeaKommunBot/1.0";

    public static string ProductTokenOf(string userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        var end = userAgent.IndexOfAny(['/', ' ']);
        return end < 0 ? userAgent : userAgent[..end];
    }

    /// <summary>
    /// A host profile's printable ASCII rule, and a product token of RFC 9309 §2.2.1 characters (letters, <c>_</c>,
    /// <c>-</c>), so a robots.txt can name it.
    /// </summary>
    public static bool IsValid(string? userAgent) =>
        HostPolitenessRules.IsValidUserAgent(userAgent) && ProductTokenOf(userAgent!) is { Length: > 0 } token &&
        token.All(c => char.IsAsciiLetter(c) || c is '_' or '-');
}
