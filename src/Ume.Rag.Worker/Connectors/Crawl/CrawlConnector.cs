using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Sitemaps;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>The listing position (§2.3): start URLs done, then the sitemap root and the entries already listed from it.</summary>
public sealed record CrawlCheckpoint : ConnectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>0: start URLs; 1: sitemaps.</summary>
    public int Phase { get; init; }

    /// <summary>
    /// The next sitemap root (settings, then per origin the robots.txt <c>Sitemap:</c> lines, <c>/sitemap.xml</c> and
    /// <c>/sitemap_index.xml</c>).
    /// </summary>
    public int Root { get; init; }

    /// <summary>Entries of <see cref="Root"/> already listed (a resumed listing re-reads the file and skips them).</summary>
    public int Skip { get; init; }
}

/// <summary>A crawl source's bound settings with their scope and canonicaliser options.</summary>
public sealed record CrawlSource(CrawlSettings Settings, CrawlScope Scope, CanonicalizerOptions Canonicalizer);

/// <summary>A fetched page: the outcome and, for content and 304s, its links and robots directives (§5.4).</summary>
public sealed record CrawlPage(FetchOutcome Outcome, PageLinks? Links);

/// <summary>
/// The <c>crawl</c> source (connectors design §5): listing yields the seeds at depth 0 (start URLs and every sitemap URL
/// in scope, from the source's <c>sitemaps</c>, each scope origin's robots.txt <c>Sitemap:</c> lines and its
/// <c>/sitemap.xml</c> and <c>/sitemap_index.xml</c>; XML or plain text; sitemap index depth ≤ 2; at most
/// <c>maxPages</c>; a transient sitemap failure retried twice, §5.1), and fetching returns the page with its links.
/// Every URL is requested (conditionally when validators are stored) unless the traversal skips it by <c>lastmod</c>
/// (<see cref="CrawlTraversal"/>, only on hosts whose site profile marks <c>lastmod</c> reliable); a URL that left the
/// sitemap is probed by <see cref="ProbeAsync"/> (the removal check, §5.4).
/// A listing that missed seeds (an unreachable robots.txt, a failed or truncated sitemap, the page limit) ends
/// <c>Partial</c> after a <see cref="ListingEvent{T}.Failure"/> per cause, so the traversal cannot be complete.
/// Discovery, frontier and host budget are <see cref="CrawlTraversal"/>'s. The canonicaliser takes the source's settings
/// and, per scope host, its applied site profile's rules, and the scope takes the profile's <c>excludedTypes</c>
/// (<see cref="SiteProfileCanonicalRules"/>; none without <paramref name="hostRules"/>, so every host keeps the default).
/// </summary>
public sealed class CrawlConnector(
    HttpFetcher fetcher, RobotsCache robots, IMalwareScanner scanner, IBlobStore blobs, TimeProvider time, SiteProfileCanonicalRules? hostRules = null)
    : IFetchConnector<CrawlCheckpoint>, IRetryFailedConnector
{
    public const string Type = SourceTypes.Crawl;

    public const int CheckpointEvery = 1000;

    /// <summary>An index nested deeper than this is not read (§5.1: index depth ≤ 2).</summary>
    public const int MaxSitemapDepth = 2;

    public const string RobotsUnreachable = "robots.unreachable";

    /// <summary>Every listing failure of a sitemap itself (unavailable, truncated, too deep, refused by the parser) starts with this.</summary>
    public const string SitemapReasonPrefix = "sitemap.";
    public const string SitemapUnavailable = "sitemap.unavailable";
    public const string SitemapTruncated = "sitemap.truncated";
    public const string SitemapTooDeep = "sitemap.depth";
    public const string LimitPages = "crawl.limit_pages";

    /// <summary>A sitemap that fails transiently is asked this many more times before it is <c>sitemap.unavailable</c> (§5.1).</summary>
    public const int SitemapRetries = 2;

    /// <summary>The wait before the first retry of a 5xx or network failure; the second waits twice as long.</summary>
    public static readonly TimeSpan SitemapRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>A 429 or 503 is retried only when its <c>Retry-After</c> is at most this (§5.1).</summary>
    public static readonly TimeSpan MaxSitemapRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The probed sitemap paths of every scope origin, in order; their absence is no failure (§5.1).</summary>
    private static readonly string[] GuessedSitemaps = ["/sitemap.xml", "/sitemap_index.xml"];

    private readonly RawContentWriter _writer = new(scanner, blobs, time);
    private readonly ConcurrentDictionary<Guid, (string Raw, CrawlSource Source)> _sources = new();

    public string SourceType => Type;

    public Type SettingsType => typeof(CrawlSettings);

    /// <summary>The failed URL, fetched under the URL it was last found or fetched under (<see cref="FailedItem.FoundUrl"/>) when known.</summary>
    public WorkItem? FromIdentity(string canonicalIdentity, FailedItem last)
    {
        ArgumentNullException.ThrowIfNull(last);
        return RetryWork.FromUrl(canonicalIdentity, last.FoundUrl);
    }

    /// <summary>
    /// The source's settings and scope, with the canonicaliser of the source's settings and of the scope hosts' applied
    /// site profiles at <paramref name="classification"/>, and the scope with those profiles' <c>excludedTypes</c>.
    /// </summary>
    public ValueTask<CrawlSource> SourceAsync(Guid sourceId, string classification, JsonElement settings, CancellationToken cancellationToken) =>
        SourceAsync(sourceId, classification, settings, fresh: false, cancellationToken);

    /// <summary>
    /// As <see cref="SourceAsync(Guid, string, JsonElement, CancellationToken)"/>; <paramref name="fresh"/> reads the scope
    /// hosts' applied site profiles again instead of from the cache (a run's start and its reconcile, so a profile applied
    /// just before is seen by this instance at once).
    /// </summary>
    public async ValueTask<CrawlSource> SourceAsync(Guid sourceId, string classification, JsonElement settings, bool fresh, CancellationToken cancellationToken)
    {
        var source = Source(sourceId, settings);
        if (hostRules is null)
        {
            return source;
        }

        var rules = await hostRules.ForHostsAsync(classification, source.Scope.Hosts, fresh, cancellationToken);
        if (rules.Canonical.Count > 0)
        {
            source = source with { Canonicalizer = source.Canonicalizer with { Hosts = rules.Canonical } };
        }

        return rules.ExcludedTypes.Count == 0 ? source : source with { Scope = source.Scope.WithExcludedTypes(rules.ExcludedTypes) };
    }

    /// <summary>The source's settings and scope, parsed once per settings version.</summary>
    private CrawlSource Source(Guid sourceId, JsonElement settings)
    {
        var raw = settings.GetRawText();
        if (_sources.TryGetValue(sourceId, out var cached) && string.Equals(cached.Raw, raw, StringComparison.Ordinal))
        {
            return cached.Source;
        }

        var bound = ContractJson.Deserialize<CrawlSettings>(settings);
        var source = new CrawlSource(bound, CrawlScope.Create(bound),
            new CanonicalizerOptions { IgnoreQueryParams = bound.IgnoreQueryParams, DirectoryIndexNames = bound.DirectoryIndexNames });
        _sources[sourceId] = (raw, source);
        return source;
    }

    public async IAsyncEnumerable<ListingEvent<CrawlCheckpoint>> ListAsync(
        ListingContext context, CrawlCheckpoint from, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(from);
        var source = await SourceAsync(context.SourceId, context.Classification, context.Settings, cancellationToken);
        var settings = source.Settings;
        // An unknown schema version (a rolled-back worker) restarts the listing, which is always safe (§2.3).
        var start = from.SchemaVersion == CrawlCheckpoint.CurrentSchemaVersion ? from : new CrawlCheckpoint { SchemaVersion = CrawlCheckpoint.CurrentSchemaVersion };
        var listed = new HashSet<string>(StringComparer.Ordinal);
        string? partial = null;

        if (start.Phase == 0)
        {
            foreach (var url in settings.StartUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Seed(source, url, null, listed) is { } seed)
                {
                    yield return seed;
                }
            }

            yield return new ListingEvent<CrawlCheckpoint>.Checkpoint(Checkpoint(1, 0, 0));
        }

        var roots = new List<SitemapRoot>();
        await foreach (var root in RootsAsync(source, cancellationToken))
        {
            if (root.Failure is { } failure)
            {
                partial ??= failure.Reason;
                yield return failure;
            }
            else
            {
                roots.Add(root);
            }
        }

        for (var i = start.Phase == 0 ? 0 : start.Root; i < roots.Count && listed.Count < settings.MaxPages; i++)
        {
            var skip = start.Phase == 1 && i == start.Root ? start.Skip : 0;
            var read = 0;
            await foreach (var entry in ReadAsync(roots[i].Url, roots[i].Optional, 0, cancellationToken))
            {
                if (entry.Failure is { } failure)
                {
                    partial ??= failure.Reason;
                    yield return failure;
                    continue;
                }

                if (++read <= skip)
                {
                    continue;
                }

                if (listed.Count >= settings.MaxPages)
                {
                    break;
                }

                if (Seed(source, entry.Entry!.Location, entry.Entry.LastModified, listed, roots[i].Url) is ListingEvent<CrawlCheckpoint>.Item item)
                {
                    yield return item;
                }

                if (read % CheckpointEvery == 0)
                {
                    yield return new ListingEvent<CrawlCheckpoint>.Checkpoint(Checkpoint(1, i, read));
                }
            }

            yield return new ListingEvent<CrawlCheckpoint>.Checkpoint(Checkpoint(1, i + 1, 0));
        }

        if (listed.Count >= settings.MaxPages)
        {
            partial ??= LimitPages;
        }

        yield return new ListingEvent<CrawlCheckpoint>.Checkpoint(Checkpoint(1, roots.Count, 0));
        yield return new ListingEvent<CrawlCheckpoint>.Done(partial is null ? Completeness.Full : Completeness.Partial(partial));
    }

    public async ValueTask<FetchOutcome> FetchAsync(FetchContext context, WorkItem item, CancellationToken cancellationToken) =>
        (await FetchPageAsync(context, item, null, cancellationToken)).Outcome;

    /// <summary>
    /// One GET (conditional when validators are stored and the run does not revalidate), redirects re-checked against the
    /// scope (§3.2), the content stored with its outlinks; a 304 returns the stored outlinks of the last content. An HTML
    /// page whose main text matches <paramref name="softNotFound"/> (the host's fingerprint, §5.3) ends <c>http.soft_404</c>.
    /// </summary>
    public async Task<CrawlPage> FetchPageAsync(FetchContext context, WorkItem item, string? softNotFound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        var url = item.Url ?? throw new ArgumentException("A crawl item has a URL.", nameof(item));
        var source = await SourceAsync(context.SourceId, context.Classification, context.Settings, cancellationToken);
        var validators = source.Settings.ConditionalGet && !context.Revalidate ? item.Hints.Stored : null;
        var request = new HttpFetchRequest(url, EgressPurpose.Fetch, validators, source.Scope.AcceptedMediaTypes(url), source.Canonicalizer, robots.CheckAsync,
            InScope: target => InScope(source, target));
        var result = await fetcher.FetchAsync(request, cancellationToken);
        switch (result)
        {
            case HttpFetchResult.Ok ok:
                await using (ok.Body)
                {
                    var stored = await _writer.StoreAsync(context, ok, source.Settings.NoiseSelectors, source.Canonicalizer, extractLinks: true, softNotFound,
                        cancellationToken);
                    return new CrawlPage(stored.Outcome, stored.Links);
                }

            case HttpFetchResult.NotModified when item.Hints.ContentHash is { } hash:
                var links = await PageLinks.LoadAsync(blobs, context.Classification, hash, cancellationToken);
                return new CrawlPage(FetchOutcomes.From(result), links ?? PageLinks.None);
            default:
                return new CrawlPage(FetchOutcomes.From(result), null);
        }
    }

    /// <summary>
    /// The removal check of a URL that left the sitemap (§5.4, weakness review §3): <c>HEAD</c>, and the full
    /// <see cref="FetchPageAsync"/> when the server refuses <c>HEAD</c> (405, 501). Null when the <c>HEAD</c> answered 2xx
    /// (the URL is still there); otherwise the page whose outcome follows §4 (404/410 <c>Gone</c>, a permanent redirect out
    /// of scope <c>http.moved_out_of_scope</c>). <paramref name="softNotFound"/> is passed to the GET.
    /// </summary>
    public async Task<CrawlPage?> ProbeAsync(FetchContext context, WorkItem item, string? softNotFound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        var url = item.Url ?? throw new ArgumentException("A crawl item has a URL.", nameof(item));
        var source = await SourceAsync(context.SourceId, context.Classification, context.Settings, cancellationToken);
        var request = new HttpFetchRequest(url, EgressPurpose.Fetch, null, null, source.Canonicalizer, robots.CheckAsync,
            InScope: target => InScope(source, target),
            Head: true);
        var result = await fetcher.FetchAsync(request, cancellationToken);
        return result switch
        {
            HttpFetchResult.Status { Code: HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented } => await FetchPageAsync(context, item, softNotFound, cancellationToken),
            HttpFetchResult.Status { Code: var code } when (int)code is >= 200 and <= 299 => null,
            _ => new CrawlPage(FetchOutcomes.From(result), null),
        };
    }

    /// <summary>
    /// The soft-404 probe of the host of <paramref name="url"/> (§5.3): a GET of <see cref="SoftNotFound.ProbeUrl"/> through
    /// the guard and robots.txt, redirects not followed. Returns the host's fingerprint when the answer is an HTML 200
    /// without redirects (a redirect target may be a real page, such as the start page), and the outcome of any other
    /// answer for the host budget (null for a 200).
    /// </summary>
    public async Task<(string? Fingerprint, FetchOutcome? Outcome)> ProbeNotFoundAsync(FetchContext context, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(url);
        var source = await SourceAsync(context.SourceId, context.Classification, context.Settings, cancellationToken);
        var request = new HttpFetchRequest(SoftNotFound.ProbeUrl(url), EgressPurpose.Fetch, null, MediaTypes.Html, source.Canonicalizer, robots.CheckAsync,
            InScope: _ => false);
        var result = await fetcher.FetchAsync(request, cancellationToken);
        if (result is not HttpFetchResult.Ok ok)
        {
            return (null, FetchOutcomes.From(result));
        }

        await using (ok.Body)
        {
            return (await _writer.FingerprintAsync(ok.Body, source.Settings.NoiseSelectors, cancellationToken), null);
        }
    }

    /// <summary>A redirect target, in its path form (<see cref="HttpFetchRequest.InScope"/>), against the source scope (§3.2).</summary>
    private static bool InScope(CrawlSource source, Uri target) =>
        UrlCanonicalizer.PathForm(target.AbsoluteUri, source.Canonicalizer).Value is { } path && source.Scope.Check(path).Admitted;

    private static CrawlCheckpoint Checkpoint(int phase, int root, int skip) =>
        new() { SchemaVersion = CrawlCheckpoint.CurrentSchemaVersion, Phase = phase, Root = root, Skip = skip };

    /// <summary>A seed item, a failure for an invalid start URL, or null for a duplicate or an out-of-scope sitemap entry.</summary>
    private static ListingEvent<CrawlCheckpoint>? Seed(CrawlSource source, string url, DateTimeOffset? lastModified, HashSet<string> listed, Uri? sitemap = null)
    {
        var canonical = sitemap is null ? UrlCanonicalizer.Canonicalize(url, source.Canonicalizer) : UrlCanonicalizer.Resolve(sitemap, url, source.Canonicalizer);
        if (!canonical.IsValid)
        {
            return sitemap is null ? new ListingEvent<CrawlCheckpoint>.Failure(url, canonical.Refusal!) : null;
        }

        // The patterns match the path the sitemap or the start URL names, the seed keeps its identity (§3.1 rule 10) and is
        // fetched under that path (#228).
        var path = sitemap is null ? UrlCanonicalizer.PathForm(url, source.Canonicalizer) : UrlCanonicalizer.ResolvePathForm(sitemap, url, source.Canonicalizer);
        var scope = source.Scope.Check(path.Value!);
        if (!scope.Admitted)
        {
            // Sitemap entries out of scope or of an excluded type are dropped (and counted by the report from the run items).
            return sitemap is null ? new ListingEvent<CrawlCheckpoint>.Failure(canonical.Value!, scope.Reason!) : null;
        }

        return listed.Add(canonical.Value!)
            ? new ListingEvent<CrawlCheckpoint>.Item(new WorkItem(IdentityKind.Url, canonical.Value!, path.ToUri(), 0, new WorkHints(SitemapLastMod: lastModified)))
            : null;
    }

    /// <summary>The sitemap roots in a fixed order (resumable), or a failure per origin whose robots.txt is unreachable.</summary>
    private async IAsyncEnumerable<SitemapRoot> RootsAsync(CrawlSource source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declared in source.Settings.Sitemaps)
        {
            if (UrlCanonicalizer.Canonicalize(declared).Value is { } url && seen.Add(url))
            {
                yield return new SitemapRoot(new Uri(url), false, null);
            }
        }

        var origins = new List<string>();
        foreach (var start in source.Settings.StartUrls)
        {
            if (UrlCanonicalizer.Canonicalize(start).Value is { } url && new Uri(url).GetLeftPart(UriPartial.Authority) is var origin && !origins.Contains(origin))
            {
                origins.Add(origin);
            }
        }

        foreach (var host in source.Scope.Hosts.Order(StringComparer.Ordinal))
        {
            if (!origins.Any(o => string.Equals(new Uri(o).Host, host, StringComparison.Ordinal)))
            {
                origins.Add("https://" + host);
            }
        }

        foreach (var origin in origins)
        {
            var robotsUrl = new Uri(origin + "/robots.txt");
            var entry = await robots.GetAsync(robotsUrl, cancellationToken);
            if (entry.Status == RobotsStatus.Unreachable)
            {
                yield return new SitemapRoot(robotsUrl, false, new ListingEvent<CrawlCheckpoint>.Failure(robotsUrl.AbsoluteUri, RobotsUnreachable));
                continue;
            }

            foreach (var line in entry.Rules.Sitemaps)
            {
                if (UrlCanonicalizer.Resolve(robotsUrl, line).Value is { } url && seen.Add(url))
                {
                    yield return new SitemapRoot(new Uri(url), false, null);
                }
            }

            foreach (var guess in GuessedSitemaps)
            {
                if (UrlCanonicalizer.Canonicalize(origin + guess).Value is { } fallback && seen.Add(fallback))
                {
                    yield return new SitemapRoot(new Uri(fallback), true, null);
                }
            }
        }
    }

    /// <summary>The URL entries of a sitemap, children of an index read in order; failures as events.</summary>
    private async IAsyncEnumerable<SitemapRead> ReadAsync(Uri url, bool optional, int depth, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new HttpFetchRequest(url, EgressPurpose.Sitemap, null, null, CanonicalizerOptions.Default, robots.CheckAsync, SitemapParser.MaxUncompressedBytes);
        var result = await FetchSitemapAsync(request, cancellationToken);
        if (result is not HttpFetchResult.Ok ok)
        {
            // /sitemap.xml and /sitemap_index.xml are guesses: their absence is no failure; a declared sitemap that does not answer is.
            if (!(optional && result is HttpFetchResult.Status { Code: var code } && (int)code is >= 400 and < 500 && code != HttpStatusCode.TooManyRequests))
            {
                yield return SitemapRead.Failed(url, SitemapUnavailable);
            }

            yield break;
        }

        SitemapDocument? document = null;
        await using (ok.Body)
        {
            // A guess answered with a web page (a host that serves every path, §5.3) has no sitemap there either.
            if (!(optional && MediaTypes.IsHtml(ok.Body.MediaType)))
            {
                await using var stream = ok.Body.OpenRead();
                document = await SitemapParser.ParseAsync(stream, cancellationToken);
            }
        }

        if (document is null)
        {
            yield break;
        }

        if (document.Error is { } error)
        {
            yield return SitemapRead.Failed(url, error);
        }
        else if (document.Truncated)
        {
            yield return SitemapRead.Failed(url, SitemapTruncated);
        }

        if (!document.IsIndex)
        {
            foreach (var entry in document.Entries)
            {
                yield return new SitemapRead(entry, null);
            }

            yield break;
        }

        foreach (var child in document.Entries)
        {
            if (depth + 1 > MaxSitemapDepth)
            {
                yield return SitemapRead.Failed(url, SitemapTooDeep);
                yield break;
            }

            if (UrlCanonicalizer.Resolve(url, child.Location).Value is not { } childUrl)
            {
                yield return SitemapRead.Failed(url, SitemapUnavailable);
                continue;
            }

            await foreach (var read in ReadAsync(new Uri(childUrl), false, depth + 1, cancellationToken))
            {
                yield return read;
            }
        }
    }

    /// <summary>
    /// A sitemap fetch with up to <see cref="SitemapRetries"/> retries of a transient failure (§5.1): a 5xx, a timeout or
    /// another network failure after <see cref="SitemapRetryDelay"/> (then twice that), a 429 or 503 after its
    /// <c>Retry-After</c> when that is at most <see cref="MaxSitemapRetryAfter"/>.
    /// </summary>
    private async Task<HttpFetchResult> FetchSitemapAsync(HttpFetchRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fetcher.FetchAsync(request, cancellationToken);
            if (attempt == SitemapRetries || RetryDelay(result, attempt) is not { } delay)
            {
                return result;
            }

            await Task.Delay(delay, time, cancellationToken);
        }
    }

    private static TimeSpan? RetryDelay(HttpFetchResult result, int attempt) => result switch
    {
        HttpFetchResult.Status { Code: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable, RetryAfter: { } retryAfter } =>
            retryAfter <= MaxSitemapRetryAfter ? retryAfter : null,
        HttpFetchResult.Status { Code: HttpStatusCode.TooManyRequests } => null,
        HttpFetchResult.Status { Code: var code } when (int)code >= 500 => SitemapRetryDelay * (attempt + 1),
        HttpFetchResult.Error { Transient: true } => SitemapRetryDelay * (attempt + 1),
        _ => null,
    };

    private sealed record SitemapRoot(Uri Url, bool Optional, ListingEvent<CrawlCheckpoint>.Failure? Failure);

    private sealed record SitemapRead(SitemapEntry? Entry, ListingEvent<CrawlCheckpoint>.Failure? Failure)
    {
        public static SitemapRead Failed(Uri url, string reason) => new(null, new ListingEvent<CrawlCheckpoint>.Failure(url.AbsoluteUri, reason));
    }
}
