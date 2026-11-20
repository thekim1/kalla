using System.Net;
using AngleSharp.Html.Parser;
using Kalla.Domain;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.Worker.Connectors.Changes;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Connectors.Http;

/// <summary>What storing a fetched body produced; <paramref name="Links"/> only when links were asked for.</summary>
public sealed record StoredContent(FetchOutcome Outcome, PageLinks? Links);

/// <summary>
/// The store step of <see cref="PageFetcher"/>, which every HTTP connector shares (connectors design §11, §12): files are
/// malware-scanned before they are stored (fail closed; a finding goes to <c>quarantine/{sourceId}/{sha256}</c>; with
/// <c>ClamAv:Mode = off</c> they are stored unscanned), the
/// content hash is taken over noise-normalised HTML or the raw bytes, and the raw blob is written once under
/// <c>raw/{level}/{contentHash}</c>. A crawl also gets the page's links, extracted before normalisation (with the links a
/// rendered page discovered besides its <c>&lt;a href&gt;</c>, §8.3) and stored next to the raw blob (§5.4), and an HTML
/// page whose main text matches the host's soft-404 fingerprint (§5.3) is <c>failed</c> <c>http.soft_404</c> before
/// anything is stored.
/// </summary>
public sealed class RawContentWriter(IMalwareScanner scanner, TimeProvider time)
{
    /// <summary>A file waits this long when ClamAV has no verdict (§11, fail closed).</summary>
    public static readonly TimeSpan ScanRetry = TimeSpan.FromSeconds(30);

    /// <param name="context">The fetch's source, level and run.</param>
    /// <param name="blobs">Where the raw blob, its links and a quarantined file go (a preview's own prefix for previews).</param>
    /// <param name="ok">The fetched body.</param>
    /// <param name="noiseSelectors">The source's <c>noiseSelectors</c> (§12.2).</param>
    /// <param name="canonicalizer">The rules for the declared canonical.</param>
    /// <param name="extractLinks">Extract and store the page's links (a crawl).</param>
    /// <param name="productToken">The crawler's product token for meta robots and <c>X-Robots-Tag</c> (<see cref="Hosts.CrawlPolicy.ProductToken"/>).</param>
    /// <param name="softNotFound">The host's soft-404 fingerprint for the run (<see cref="SoftNotFound"/>); null for none.</param>
    /// <param name="discovered">Links a rendered page found besides its <c>&lt;a href&gt;</c> (§8.3), added to its links.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    public async Task<StoredContent> StoreAsync(
        FetchContext context,
        IBlobStore blobs,
        HttpFetchResult.Ok ok,
        IReadOnlyList<string> noiseSelectors,
        CanonicalizerOptions canonicalizer,
        bool extractLinks,
        string productToken,
        string? softNotFound,
        IReadOnlyList<Uri> discovered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ok);
        var body = ok.Body;
        if (MediaTypes.NeedsScan(body.MediaType))
        {
            ScanResult scan;
            await using (var stream = body.OpenRead())
            {
                scan = await scanner.ScanAsync(stream, "crawl", cancellationToken);
            }

            switch (scan)
            {
                case ScanResult.Malware malware:
                    var quarantine = new BlobKey($"quarantine/{context.SourceId:D}/{body.Sha256}");
                    await using (var stream = body.OpenRead())
                    {
                        // Encrypted by the source's level: the key names none (data-model §7, §9).
                        await blobs.ForLevel(context.Classification).PutAsync(quarantine, stream, body.Length, cancellationToken);
                    }

                    return new(new FetchOutcome.Quarantined(quarantine, body.Sha256, malware.Signature, malware.Reason), null);
                case ScanResult.TooLarge:
                    return new(new FetchOutcome.Refused(RunItemOutcome.TooLarge, "clamav.limit"), null);
                case ScanResult.Unavailable unavailable:
                    return new(new FetchOutcome.Transient(unavailable.Reason, ScanRetry, unavailable.CountsAttempt), null);
            }
        }

        string contentHash;
        string? canonicalHint = null;
        PageLinks? links = extractLinks ? PageLinks.None : null;
        if (MediaTypes.IsHtml(body.MediaType))
        {
            await using var stream = body.OpenRead();
            using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
            // Read before normalisation, which strips query strings from link hrefs.
            if (document.QuerySelector("link[rel~=canonical][href]")?.GetAttribute("href") is { } declared)
            {
                canonicalHint = UrlCanonicalizer.Resolve(ok.FinalUrl, declared, canonicalizer).Value;
            }

            if (extractLinks)
            {
                links = LinkExtractor.Extract(document, ok.FinalUrl, ok.RobotsTag, productToken).With(discovered);
            }

            contentHash = HtmlNoiseNormalizer.Hash(HtmlNoiseNormalizer.Normalize(document, FetchDay(), noiseSelectors));
            if (softNotFound is not null && string.Equals(SoftNotFound.Fingerprint(document), softNotFound, StringComparison.Ordinal))
            {
                // A "page not found" served with 200: never stored, so it never replaces a document's good content.
                return new(new FetchOutcome.Failed(SoftNotFound.Reason), null);
            }
        }
        else
        {
            contentHash = body.Sha256;
        }

        var key = new BlobKey($"raw/{context.Classification}/{contentHash}");
        await using (var stream = body.OpenRead())
        {
            // Write-once: an existing key (the same content fetched before) is left as it is.
            await blobs.PutAsync(key, stream, body.Length, cancellationToken);
        }

        if (links is not null)
        {
            await links.StoreAsync(blobs, context.Classification, contentHash, cancellationToken);
        }

        var outcome = new FetchOutcome.Content(
            new RawContentRef(key, contentHash, HtmlNoiseNormalizer.RulesVersion, body.MediaType, body.Length),
            ok.Validators,
            new FetchFacts(ok.FinalUrl, ok.FinalIdentity, ok.Redirects, canonicalHint));
        return new(outcome, links);
    }

    /// <summary>The soft-404 fingerprint of a fetched HTML body (§5.3, <see cref="SoftNotFound.Fingerprint"/>); null for other types or no main text.</summary>
    public async Task<string?> FingerprintAsync(FetchedBody body, IReadOnlyList<string> noiseSelectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!MediaTypes.IsHtml(body.MediaType))
        {
            return null;
        }

        await using var stream = body.OpenRead();
        using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
        HtmlNoiseNormalizer.Normalize(document, FetchDay(), noiseSelectors);
        return SoftNotFound.Fingerprint(document);
    }

    /// <summary>The fetch day in Europe/Stockholm, for the date rule of the noise normalisation (§12.2).</summary>
    private DateOnly FetchDay() => StockholmTime.Today(time);
}

/// <summary>The fetch results other than a body, as run item outcomes (connectors design §4, §15).</summary>
public static class FetchOutcomes
{
    /// <summary>When a 429 or 503 names no <c>Retry-After</c> (§6.4).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>A bot challenge or WAF block (§5.3): transient, the host backs off as on a 429.</summary>
    public const string Challenge = "http.challenge";

    /// <summary>Response codes of §4: 404/410 gone, 401/403 failed, 429/503 back off without an attempt, other 5xx retry.</summary>
    public static FetchOutcome FromStatus(HttpFetchResult.Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var code = (int)status.Code;
        return status.Code switch
        {
            HttpStatusCode.NotFound => new FetchOutcome.Gone("http.404"),
            HttpStatusCode.Gone => new FetchOutcome.Gone("http.410"),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                new FetchOutcome.Transient("rate_limited", status.RetryAfter ?? DefaultRetryAfter, CountsAttempt: false),
            _ when code >= 500 => new FetchOutcome.Transient("http.5xx", null, CountsAttempt: true),
            _ => new FetchOutcome.Failed("http." + code.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>
    /// Every result but <see cref="HttpFetchResult.Ok"/>: a permanent redirect out of scope is <c>http.moved_out_of_scope</c>
    /// (a deletion candidate for a known document), a temporary one <c>redirect.out_of_scope</c> (a sign-in wall, never a
    /// deletion, §3.2); a challenge page is transient <c>http.challenge</c> (§5.3).
    /// </summary>
    public static FetchOutcome From(HttpFetchResult result) => result switch
    {
        HttpFetchResult.NotModified notModified => new FetchOutcome.NotModified(notModified.Validators, notModified.FinalUrl),
        HttpFetchResult.Status status => FromStatus(status),
        HttpFetchResult.Refused refused => new FetchOutcome.Refused(refused.Outcome, refused.Reason),
        HttpFetchResult.Skipped skipped => new FetchOutcome.Skipped(skipped.Reason),
        HttpFetchResult.OutOfScope { Permanent: true } => new FetchOutcome.Gone("http.moved_out_of_scope"),
        HttpFetchResult.OutOfScope => new FetchOutcome.Skipped("redirect.out_of_scope"),
        HttpFetchResult.Error { Transient: true } transient => new FetchOutcome.Transient(transient.Reason, null, CountsAttempt: true),
        HttpFetchResult.Error error => new FetchOutcome.Failed(error.Reason),
        // Counts an attempt, unlike a 429: a challenge that never lifts ends the item failed instead of requeuing it for ever.
        HttpFetchResult.Challenge challenge => new FetchOutcome.Transient(Challenge, challenge.RetryAfter ?? DefaultRetryAfter, CountsAttempt: true),
        _ => throw new InvalidOperationException($"Unexpected fetch result {result.GetType().Name}."),
    };
}
