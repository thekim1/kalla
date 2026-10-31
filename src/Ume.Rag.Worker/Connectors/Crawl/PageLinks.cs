using System.IO.Compression;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>
/// The outlinks of one fetched content (connectors design §5.2, §5.4): absolute links in the canonical form of the default
/// rules (the source's own rules are applied at admission) and the page's robots directives as sent, decided per host
/// kind by the traversal. Stored with the raw blob as <c>raw/{level}/{contentHash}.links.json.gz</c>, so a 304 expands
/// the same links without parsing.
/// </summary>
/// <param name="Links">Distinct links in document order, at most <see cref="LinkExtractor.MaxLinks"/>.</param>
/// <param name="NoIndex">meta robots or <c>X-Robots-Tag</c> <c>noindex</c> (or <c>none</c>) for us.</param>
/// <param name="NoFollow">meta robots or <c>X-Robots-Tag</c> <c>nofollow</c> (or <c>none</c>) for us.</param>
public sealed record PageLinks(IReadOnlyList<string> Links, bool NoIndex, bool NoFollow)
{
    public const int FormatVersion = 1;

    public static PageLinks None { get; } = new([], false, false);

    public static BlobKey KeyOf(string classification, string contentHash) => new($"raw/{classification}/{contentHash}.links.json.gz");

    /// <summary>Writes the outlinks once (write-once: an existing key is left as it is).</summary>
    public async Task StoreAsync(IBlobStore blobs, string classification, string contentHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, new StoredLinks(FormatVersion, Links, NoIndex, NoFollow), JsonSerializerOptions.Web, cancellationToken);
        }

        buffer.Position = 0;
        await blobs.PutAsync(KeyOf(classification, contentHash), buffer, buffer.Length, cancellationToken);
    }

    /// <summary>The stored outlinks of a content hash, or null when absent or of an unknown format.</summary>
    public static async Task<PageLinks?> LoadAsync(IBlobStore blobs, string classification, string contentHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        await using var stream = await blobs.OpenReadAsync(KeyOf(classification, contentHash), null, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        var stored = await JsonSerializer.DeserializeAsync<StoredLinks>(gzip, JsonSerializerOptions.Web, cancellationToken);
        return stored is { Version: FormatVersion } ? new PageLinks(stored.Links, stored.NoIndex, stored.NoFollow) : null;
    }

    private sealed record StoredLinks(int Version, IReadOnlyList<string> Links, bool NoIndex, bool NoFollow);
}

/// <summary>
/// Link and robots-directive extraction from parsed HTML (connectors design §5.2): <c>a[href]</c> and <c>area[href]</c>
/// against <c>&lt;base href&gt;</c>, the declared <c>rel=canonical</c> as a discovered link (§3.3), and meta robots /
/// <c>X-Robots-Tag</c> for <c>robots</c> or our product token (also the token-prefixed header form). <c>rel="nofollow"</c>
/// on single links is ignored. Runs before noise normalisation, which rewrites link targets.
/// </summary>
public static class LinkExtractor
{
    public const int MaxLinks = 5000;

    private static readonly string[] SkippedSchemes = ["javascript:", "mailto:", "tel:", "data:", "ftp:", "sms:"];

    public static PageLinks Extract(IHtmlDocument document, Uri pageUrl, string? robotsTag)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageUrl);
        var baseUrl = pageUrl;
        if (document.QuerySelector("base[href]")?.GetAttribute("href") is { } declaredBase && Uri.TryCreate(pageUrl, declaredBase.Trim(), out var resolved))
        {
            baseUrl = resolved;
        }

        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.QuerySelectorAll("a[href], area[href], link[rel~=canonical][href]"))
        {
            var href = element.GetAttribute("href")!.Trim();
            if (href.Length == 0 || href.StartsWith('#') || SkippedSchemes.Any(s => href.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (UrlCanonicalizer.Resolve(baseUrl, href).Value is { } link && seen.Add(link))
            {
                links.Add(link);
                if (links.Count == MaxLinks)
                {
                    break;
                }
            }
        }

        var (noIndex, noFollow) = (false, false);
        foreach (var meta in document.QuerySelectorAll("meta[name][content]"))
        {
            var name = meta.GetAttribute("name")!.Trim();
            if (name.Equals("robots", StringComparison.OrdinalIgnoreCase) || name.Equals(Robots.RobotsTxt.ProductToken, StringComparison.OrdinalIgnoreCase))
            {
                Apply(meta.GetAttribute("content")!.Split(','), ref noIndex, ref noFollow);
            }
        }

        if (robotsTag is not null)
        {
            ApplyHeader(robotsTag, ref noIndex, ref noFollow);
        }

        return new PageLinks(links, noIndex, noFollow);
    }

    /// <summary>
    /// <c>X-Robots-Tag</c>: comma-separated directives, a <c>name:</c> prefix scoping the following ones to that crawler
    /// (only <c>UmeaKommunBot</c> counts besides unprefixed ones).
    /// </summary>
    private static void ApplyHeader(string header, ref bool noIndex, ref bool noFollow)
    {
        var forUs = true;
        foreach (var raw in header.Split(','))
        {
            var part = raw.Trim();
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && !part.StartsWith("unavailable_after", StringComparison.OrdinalIgnoreCase))
            {
                forUs = part[..colon].Trim().Equals(Robots.RobotsTxt.ProductToken, StringComparison.OrdinalIgnoreCase);
                part = part[(colon + 1)..];
            }

            if (forUs)
            {
                Apply([part], ref noIndex, ref noFollow);
            }
        }
    }

    private static void Apply(IEnumerable<string> directives, ref bool noIndex, ref bool noFollow)
    {
        foreach (var directive in directives.Select(d => d.Trim()))
        {
            if (directive.Equals("noindex", StringComparison.OrdinalIgnoreCase) || directive.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                noIndex = true;
            }

            if (directive.Equals("nofollow", StringComparison.OrdinalIgnoreCase) || directive.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                noFollow = true;
            }
        }
    }
}
