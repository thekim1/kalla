using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Connectors.Changes;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.UrlList;

/// <summary>
/// The <c>url-list</c> source's settings (connectors design §4, §12.3; schema <c>contracts/sources/v1/url-list.schema.json</c>).
/// Schedules and names are read by the scheduler and the admin, not here.
/// </summary>
public sealed record UrlListSettings
{
    /// <summary>The listed URLs (≤ 5,000), normalised on save; never followed further.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    public IReadOnlyList<string> IgnoreQueryParams { get; init; } = [];

    public IReadOnlyList<string> DirectoryIndexNames { get; init; } = [];

    /// <summary>CSS selectors removed before the content hash (§12.2).</summary>
    public IReadOnlyList<string> NoiseSelectors { get; init; } = [];

    /// <summary>Send the stored validators (§4); off sends a plain GET every time.</summary>
    public bool ConditionalGet { get; init; } = true;

    public CanonicalizerOptions Canonicalizer() => new() { IgnoreQueryParams = IgnoreQueryParams, DirectoryIndexNames = DirectoryIndexNames };
}

/// <summary>The listing position, so a resumed <c>sync</c> continues after the last committed batch (§2.3).</summary>
public sealed record UrlListCheckpoint : ConnectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public int Position { get; init; }
}

/// <summary>
/// Exactly the listed URLs, at depth 0, never following links (connectors design §4): a conditional GET with the stored
/// validators, redirects re-checked hop by hop, robots.txt honoured as everywhere (§5.1), files malware-scanned before
/// they are stored (§11), and the content hash over noise-normalised HTML or the raw bytes (§12). The raw blob is written
/// once under <c>raw/{level}/{contentHash}</c>; the runner (engine) writes frontier rows, documents and run items.
/// </summary>
public sealed class UrlListConnector(
    HttpFetcher fetcher,
    RobotsCache robots,
    IMalwareScanner scanner,
    IBlobStore blobs,
    TimeProvider time) : IFetchConnector<UrlListCheckpoint>, IIdListingConnector
{
    public const string Type = "url-list";

    /// <summary>Items between checkpoints: one committed batch of claims and run items (§2.3).</summary>
    public const int CheckpointEvery = 1000;

    /// <summary>A file waits this long when ClamAV has no verdict (§11, fail closed).</summary>
    public static readonly TimeSpan ScanRetry = TimeSpan.FromSeconds(30);

    /// <summary>When a 429 or 503 names no <c>Retry-After</c> (§6.4).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public string SourceType => Type;

    public Type SettingsType => typeof(UrlListSettings);

    public async IAsyncEnumerable<ListingEvent<UrlListCheckpoint>> ListAsync(
        ListingContext context, UrlListCheckpoint from, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(from);
        var settings = Settings(context.Settings);
        var options = settings.Canonicalizer();
        // An unknown schema version (a rolled-back worker) restarts the listing, which is always safe (§2.3).
        var start = from.SchemaVersion == UrlListCheckpoint.CurrentSchemaVersion ? Math.Clamp(from.Position, 0, settings.Urls.Count) : 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < start; i++)
        {
            if (UrlCanonicalizer.Canonicalize(settings.Urls[i], options).Value is { } earlier)
            {
                seen.Add(earlier);
            }
        }

        for (var i = start; i < settings.Urls.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = UrlCanonicalizer.Canonicalize(settings.Urls[i], options);
            if (!canonical.IsValid)
            {
                yield return new ListingEvent<UrlListCheckpoint>.Failure(settings.Urls[i], canonical.Refusal!);
            }
            else if (seen.Add(canonical.Value!))
            {
                yield return new ListingEvent<UrlListCheckpoint>.Item(
                    new WorkItem(IdentityKind.Url, canonical.Value!, canonical.ToUri(), 0, WorkHints.None));
            }

            if ((i + 1) % CheckpointEvery == 0 && i + 1 < settings.Urls.Count)
            {
                yield return new ListingEvent<UrlListCheckpoint>.Checkpoint(Checkpoint(i + 1));
            }
        }

        yield return new ListingEvent<UrlListCheckpoint>.Checkpoint(Checkpoint(settings.Urls.Count));
        yield return new ListingEvent<UrlListCheckpoint>.Done(Completeness.Full);
    }

    public async IAsyncEnumerable<string> ListIdentitiesAsync(ListingContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await foreach (var item in ListAsync(context, new UrlListCheckpoint { SchemaVersion = UrlListCheckpoint.CurrentSchemaVersion }, cancellationToken))
        {
            if (item is ListingEvent<UrlListCheckpoint>.Item listed)
            {
                yield return listed.Work.CanonicalIdentity;
            }
        }
    }

    public async ValueTask<FetchOutcome> FetchAsync(FetchContext context, WorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        var url = item.Url ?? throw new ArgumentException("A url-list item has a URL.", nameof(item));
        var settings = Settings(context.Settings);
        var validators = settings.ConditionalGet && !context.Revalidate ? item.Hints.Stored : null;
        var request = new HttpFetchRequest(url, EgressPurpose.Fetch, validators, MediaTypes.DocumentFormats, settings.Canonicalizer(),
            robots.CheckAsync);
        var result = await fetcher.FetchAsync(request, cancellationToken);
        switch (result)
        {
            case HttpFetchResult.Ok ok:
                await using (ok.Body)
                {
                    return await StoreAsync(context, settings, ok, cancellationToken);
                }

            case HttpFetchResult.NotModified notModified:
                return new FetchOutcome.NotModified(notModified.Validators);
            case HttpFetchResult.Status status:
                return MapStatus(status);
            case HttpFetchResult.Refused refused:
                return new FetchOutcome.Refused(refused.Outcome, refused.Reason);
            case HttpFetchResult.Skipped skipped:
                return new FetchOutcome.Skipped(skipped.Reason);
            case HttpFetchResult.Error { Transient: true } transient:
                return new FetchOutcome.Transient(transient.Reason, null, CountsAttempt: true);
            case HttpFetchResult.Error error:
                return new FetchOutcome.Failed(error.Reason);
            default:
                throw new InvalidOperationException($"Unexpected fetch result {result.GetType().Name}.");
        }
    }

    /// <summary>Response codes of §4: 404/410 gone, 401/403 failed, 429/503 back off without an attempt, other 5xx retry.</summary>
    internal static FetchOutcome MapStatus(HttpFetchResult.Status status)
    {
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

    private async Task<FetchOutcome> StoreAsync(FetchContext context, UrlListSettings settings, HttpFetchResult.Ok ok, CancellationToken cancellationToken)
    {
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
                        await blobs.PutAsync(quarantine, stream, body.Length, cancellationToken);
                    }

                    return new FetchOutcome.Quarantined(quarantine, body.Sha256, malware.Signature, malware.Reason);
                case ScanResult.TooLarge:
                    return new FetchOutcome.Refused(RunItemOutcome.TooLarge, "clamav.limit");
                case ScanResult.Unavailable unavailable:
                    return new FetchOutcome.Transient(unavailable.Reason, ScanRetry, unavailable.CountsAttempt);
            }
        }

        string contentHash;
        string? canonicalHint = null;
        if (MediaTypes.IsHtml(body.MediaType))
        {
            await using var stream = body.OpenRead();
            using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
            // Read before normalisation, which strips query strings from link hrefs.
            if (document.QuerySelector("link[rel~=canonical][href]")?.GetAttribute("href") is { } declared)
            {
                canonicalHint = UrlCanonicalizer.Resolve(ok.FinalUrl, declared, settings.Canonicalizer()).Value;
            }

            var fetchDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Stockholm).DateTime);
            contentHash = HtmlNoiseNormalizer.Hash(HtmlNoiseNormalizer.Normalize(document, fetchDay, settings.NoiseSelectors));
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

        return new FetchOutcome.Content(
            new RawContentRef(key, contentHash, HtmlNoiseNormalizer.RulesVersion, body.MediaType, body.Length),
            ok.Validators,
            new FetchFacts(ok.FinalUrl, ok.FinalIdentity, ok.Redirects, canonicalHint));
    }

    private static UrlListCheckpoint Checkpoint(int position) =>
        new() { SchemaVersion = UrlListCheckpoint.CurrentSchemaVersion, Position = position };

    private static UrlListSettings Settings(JsonElement settings) =>
        settings.Deserialize<UrlListSettings>(JsonSerializerOptions.Web) ?? new UrlListSettings();
}
