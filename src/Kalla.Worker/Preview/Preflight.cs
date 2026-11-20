using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Render;
using Kalla.Worker.Connectors.Robots;
using Kalla.Worker.Connectors.Urls;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Html;

namespace Kalla.Worker.Preview;

/// <summary>
/// <c>preflight</c> (connectors design §2.2; admin design §4.7, §5.4 step 4) of an unsaved URL-list or crawl configuration,
/// within the 2-minute preview deadline. Each request goes through the production fetch path (egress guard, robots.txt,
/// redirects) after a slot of the host's budget; results are <c>&lt;area&gt;.&lt;code&gt;</c> codes only.
/// <list type="bullet">
/// <item>URL list: the first 20 URLs, one entry per URL that is not reachable (<c>reachability</c>, <c>robots</c> or
/// <c>egress</c>, a <c>warn</c>: the rest of the list still syncs), the robots.txt status per host, and a <c>fail</c>
/// when no sampled URL answers.</item>
/// <item>Crawl: the start URLs (any failure is a <c>fail</c>: the crawl would have no seeds), robots.txt per start host
/// (unreachable is a <c>fail</c>: the host is paused for the run, connectors design §5.1), the sitemaps and the pages they
/// list in scope (the crawl's own listing) against <c>maxPages</c>.</item>
/// <item>Both: the CMS preset the first HTML page's fingerprint selects.</item>
/// <item>Both, last (connectors design §8.1, a hint only): the first <see cref="MaxRenders"/> start URLs or listed URLs that
/// answered HTML are fetched again and rendered through the production path (<see cref="PageFetcher"/> in <c>browser</c>
/// mode with the form's <c>readySelector</c> and <c>scrollToLoad</c>, after a render slot waited for up to
/// <see cref="RenderSlotWait"/>, else <c>render.busy</c>), and the main-text words of the raw and the rendered page are
/// counted by the HTML extraction with the host's site profile (its <c>mainRoot</c>, else the preset's landmarks, else
/// <c>body</c>). Each page is a <c>render</c> entry with facts <c>rawWords</c> and <c>renderedWords</c>: code
/// <c>render.suggested</c> when the raw count is under half the rendered one and the rendered one is at least
/// <see cref="MinRenderedWords"/> (a <c>warn</c> while the form fetches with <c>http</c>); <c>render.empty</c>, a <c>warn</c>,
/// when the rendered one is under <see cref="MinRenderedWords"/> and either none or fewer than the raw one (the page was
/// probably ready before its content loaded: name the loaded content in <c>readySelector</c>); else <c>render.compared</c>.
/// Without a renderer one <c>render.not_configured</c> entry and no counts; a failed render is its reason
/// (<c>render.timeout</c>, <c>render.unavailable</c>, and for the form's <c>readySelector</c> <c>render.bad_selector</c>
/// or <c>render.selector_not_found</c>). These entries are information and never <c>fail</c>.</item>
/// </list>
/// </summary>
public sealed partial class Preflight(
    PreviewConfig config,
    HttpFetcher fetcher,
    PageFetcher pages,
    RobotsCache robots,
    IBlobStore blobs,
    HostBudget budget,
    RenderSlots renders,
    SiteProfiles siteProfiles,
    HtmlExtractor extractor,
    TimeProvider time,
    ILogger<Preflight> logger)
{
    /// <summary>The URLs a URL-list preflight samples (admin design §4.7).</summary>
    public const int MaxUrls = 20;

    /// <summary>The listing cap of the page estimate: the sources schema's <c>maxPages</c> maximum.</summary>
    public const int EstimateCap = 1_000_000;

    public const string Reachability = "reachability";
    public const string Robots = "robots";
    public const string Egress = "egress";
    public const string Sitemaps = "sitemaps";
    public const string Pages = "pages";
    public const string Cms = "cms";
    public const string Render = "render";

    /// <summary>Pages rendered for the raw-vs-rendered comparison, at most, to keep the preflight fast.</summary>
    public const int MaxRenders = 3;

    /// <summary>A rendered page with fewer main-text words than this never suggests the browser.</summary>
    public const int MinRenderedWords = 50;

    /// <summary>How long a comparison waits for a free render slot before it reports <c>render.busy</c>.</summary>
    public static readonly TimeSpan RenderSlotWait = TimeSpan.FromSeconds(20);

    /// <summary>The comparison stops this long before the preview deadline, so it never turns the preflight into a deadline failure.</summary>
    public static readonly TimeSpan RenderMargin = TimeSpan.FromSeconds(5);

    /// <summary>The raw page holds under half the rendered page's main text: the site seems to need JavaScript.</summary>
    public const string RenderSuggested = "render.suggested";

    /// <summary>The raw page holds at least half the rendered page's main text.</summary>
    public const string RenderCompared = "render.compared";

    /// <summary>The rendered page holds under <see cref="MinRenderedWords"/> main-text words, none or fewer than the raw page: it was probably ready before its content loaded.</summary>
    public const string RenderEmpty = "render.empty";

    public const string NoneReachable = "preflight.none_reachable";
    public const string DeadlineReached = "preflight.deadline";

    public async Task<JobResult> RunAsync(LeasedJob job, PreflightPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payload);
        if (await config.CollectionAsync(payload.CollectionId, cancellationToken) is not { } collection)
        {
            return new JobResult.FatalError(StagePreview.CollectionUnknown);
        }

        var checks = new List<PreflightCheckResult>();
        var found = new Findings();
        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(StagePreview.Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            switch (payload.SourceType)
            {
                case SourceTypes.UrlList:
                    if (Bind<UrlListSettings>(payload.Settings) is not { } list)
                    {
                        return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
                    }

                    await UrlListAsync(collection.Classification, list, checks, found, linked.Token);
                    await CompareAsync(job.Id, collection.Classification, Plan(list.FetchMode, list.ReadySelector, list.ScrollToLoad, list.NoiseSelectors,
                        new CanonicalizerOptions { IgnoreQueryParams = list.IgnoreQueryParams, DirectoryIndexNames = list.DirectoryIndexNames }, payload.Settings),
                        found.Samples, checks, started, linked.Token);
                    break;
                case SourceTypes.Crawl:
                    if (Bind<CrawlSettings>(payload.Settings) is not { } crawl)
                    {
                        return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
                    }

                    await CrawlAsync(job.Id, collection.Classification, crawl, checks, found, linked.Token);
                    await CompareAsync(job.Id, collection.Classification, Plan(crawl.FetchMode, crawl.ReadySelector, crawl.ScrollToLoad, crawl.NoiseSelectors,
                        new CanonicalizerOptions { IgnoreQueryParams = crawl.IgnoreQueryParams, DirectoryIndexNames = crawl.DirectoryIndexNames }, payload.Settings),
                        found.Samples, checks, started, linked.Token);
                    break;
                default:
                    return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            checks.Add(Check(Reachability, PreflightState.Fail, DeadlineReached, null));
        }

        checks.Add(found.Cms is { } cms ? Check(Cms, PreflightState.Pass, "cms." + cms, found.CmsHost) : Check(Cms, PreflightState.Pass, "cms.unknown", null));
        var result = new PreflightResult
        {
            State = checks.Max(c => c.State),
            Checks = [.. checks.Take(100)],
            EstimatedPages = found.EstimatedPages,
            Cms = found.Cms,
        };
        LogPreflight(logger, job.Id, payload.SourceType, result.State.ToString());
        return new JobResult.Success(ContractJson.Serialize(result));
    }

    private async Task UrlListAsync(string level, UrlListSettings settings, List<PreflightCheckResult> checks, Findings found, CancellationToken cancellationToken)
    {
        var canonicalizer = new CanonicalizerOptions { IgnoreQueryParams = settings.IgnoreQueryParams, DirectoryIndexNames = settings.DirectoryIndexNames };
        var reachable = 0;
        var origins = new List<Uri>();
        foreach (var url in settings.Urls.Take(MaxUrls))
        {
            var (check, code, asked) = await ProbeAsync(url, level, canonicalizer, found, cancellationToken);
            if (check is null)
            {
                reachable++;
            }
            else
            {
                checks.Add(Check(check, PreflightState.Warn, code!, url));
            }

            AddOrigin(origins, asked);
        }

        foreach (var origin in origins)
        {
            checks.Add(await RobotsAsync(origin, unreachable: PreflightState.Warn, cancellationToken));
        }

        checks.Add(reachable == 0
            ? Check(Reachability, PreflightState.Fail, NoneReachable, null)
            : Check(Reachability, PreflightState.Pass, "http.200", null, new { reachable, sampled = Math.Min(settings.Urls.Count, MaxUrls) }));
    }

    private async Task CrawlAsync(Guid jobId, string level, CrawlSettings settings, List<PreflightCheckResult> checks, Findings found,
        CancellationToken cancellationToken)
    {
        var canonicalizer = new CanonicalizerOptions { IgnoreQueryParams = settings.IgnoreQueryParams, DirectoryIndexNames = settings.DirectoryIndexNames };
        var origins = new List<Uri>();
        foreach (var url in settings.StartUrls.Take(MaxUrls))
        {
            var (check, code, asked) = await ProbeAsync(url, level, canonicalizer, found, cancellationToken);
            checks.Add(check is null ? Check(Reachability, PreflightState.Pass, "http.200", url) : Check(check, PreflightState.Fail, code!, url));
            AddOrigin(origins, asked);
        }

        foreach (var origin in origins)
        {
            checks.Add(await RobotsAsync(origin, unreachable: PreflightState.Fail, cancellationToken));
        }

        // The crawl's own listing: start URLs, then every sitemap's URLs in scope (index depth ≤ 2), up to the estimate cap.
        var connector = new CrawlConnector(fetcher, pages, robots, new PreviewBlobs(blobs.ForLevel(level), jobId), time);
        var estimate = ContractJson.ToElement(settings with { MaxPages = EstimateCap });
        var listed = 0;
        await foreach (var listing in connector.ListAsync(new ListingContext(jobId, level, jobId, estimate), new CrawlCheckpoint(), cancellationToken))
        {
            switch (listing)
            {
                case ListingEvent<CrawlCheckpoint>.Item:
                    listed++;
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure { Reason: var reason, Identity: var identity }
                    when reason.StartsWith(CrawlConnector.SitemapReasonPrefix, StringComparison.Ordinal):
                    checks.Add(Check(Sitemaps, PreflightState.Warn, reason, identity));
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure { Reason: var reason, Identity: var identity }
                    when reason != CrawlConnector.RobotsUnreachable && reason != CrawlConnector.LimitPages:
                    checks.Add(Check(Reachability, PreflightState.Warn, reason, identity));
                    break;
            }
        }

        var fromSitemaps = Math.Max(0, listed - settings.StartUrls.Count);
        checks.Add(fromSitemaps > 0
            ? Check(Sitemaps, PreflightState.Pass, "sitemap.found", null, new { urls = fromSitemaps })
            : Check(Sitemaps, PreflightState.Warn, "sitemap.none", null));
        found.EstimatedPages = listed;
        checks.Add(listed > settings.MaxPages
            ? Check(Pages, PreflightState.Warn, CrawlConnector.LimitPages, null, new { estimatedPages = listed, settings.MaxPages })
            : Check(Pages, PreflightState.Pass, "pages.within_limit", null, new { estimatedPages = listed, settings.MaxPages }));
    }

    /// <summary>
    /// One GET of <paramref name="url"/>: the check and code it failed (both null when it answered 2xx), and the URL whose
    /// robots.txt was asked (null when the guard refused it first). While fewer than <see cref="MaxRenders"/> pages are
    /// sampled, an HTML answer's main-text words are counted and the page becomes a sample of the comparison.
    /// </summary>
    private async Task<(string? Check, string? Code, Uri? Asked)> ProbeAsync(string url, string level, CanonicalizerOptions canonicalizer, Findings found,
        CancellationToken cancellationToken)
    {
        var canonical = UrlCanonicalizer.Canonicalize(url, canonicalizer);
        if (!canonical.IsValid)
        {
            return (Reachability, canonical.Refusal, null);
        }

        var target = canonical.ToUri();
        var request = new HttpFetchRequest(target, EgressPurpose.Preflight, null, MediaTypes.DocumentFormats, canonicalizer, robots.CheckAsync);
        HttpFetchResult result;
        var (lease, refusal) = await PreviewHosts.SlotAsync(budget, target.Authority, time, cancellationToken);
        if (lease is null)
        {
            return (Reachability, refusal, null);
        }

        using (lease)
        {
            var started = time.GetTimestamp();
            result = await fetcher.FetchAsync(request, cancellationToken);
            var outcome = result is HttpFetchResult.Ok ? null : FetchOutcomes.From(result);
            await lease.CompleteAsync(outcome is null
                ? new HostResponse(HostResponseKind.Success, time.GetElapsedTime(started))
                : PreviewHosts.Response(outcome, time.GetElapsedTime(started)), cancellationToken);
        }

        switch (result)
        {
            case HttpFetchResult.Ok ok:
                await using (ok.Body)
                {
                    var sample = found.Samples.Count < MaxRenders;
                    if ((found.Cms is null || sample) && MediaTypes.IsHtml(ok.Body.MediaType))
                    {
                        await using var stream = ok.Body.OpenRead();
                        using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
                        if (found.Cms is null && SitevisionPreset.Recognises(document))
                        {
                            (found.Cms, found.CmsHost) = (SitevisionPreset.Name, target.IdnHost);
                        }

                        if (sample)
                        {
                            found.Samples.Add(new RenderSample(url, target, await WordsAsync(document, level, ok.FinalUrl, cancellationToken)));
                        }
                    }
                }

                return (null, null, target);
            case HttpFetchResult.Skipped skipped:
                return (Robots, skipped.Reason, target);
            case HttpFetchResult.Refused refused when refused.Reason.StartsWith("egress.", StringComparison.Ordinal):
                return (Egress, refused.Reason, null);
            case HttpFetchResult.Refused refused when refused.Reason.StartsWith("robots.", StringComparison.Ordinal):
                return (Robots, refused.Reason, target);
            case HttpFetchResult.Status status:
                return (Reachability, "http." + ((int)status.Code).ToString(CultureInfo.InvariantCulture), target);
            default:
                return (Reachability, FetchOutcomes.From(result) switch
                {
                    FetchOutcome.Refused refused => refused.Reason,
                    FetchOutcome.Failed failed => failed.Reason,
                    FetchOutcome.Transient transient => transient.Reason.Contains('.', StringComparison.Ordinal) ? transient.Reason : "http." + transient.Reason,
                    FetchOutcome.Gone gone => gone.Reason,
                    FetchOutcome.Skipped skipped => skipped.Reason,
                    _ => "http.unexpected",
                }, target);
        }
    }

    /// <summary>
    /// The raw-vs-rendered comparison of the samples (connectors design §8.1), within what the preview deadline leaves less
    /// <see cref="RenderMargin"/>: past that, one <c>preflight.deadline</c> entry for the comparison and the checks stand.
    /// </summary>
    private async Task CompareAsync(Guid jobId, string level, RenderPlan plan, IReadOnlyList<RenderSample> samples, List<PreflightCheckResult> checks,
        long started, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var left = StagePreview.Deadline - RenderMargin - time.GetElapsedTime(started);
        if (left <= TimeSpan.Zero)
        {
            checks.Add(Check(Render, PreflightState.Pass, DeadlineReached, null));
            return;
        }

        using var remaining = new CancellationTokenSource(left, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, remaining.Token);
        var store = new PreviewBlobs(blobs.ForLevel(level), jobId);
        try
        {
            foreach (var sample in samples)
            {
                if (!await CompareAsync(jobId, level, plan, store, sample, checks, linked.Token))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (remaining.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            checks.Add(Check(Render, PreflightState.Pass, DeadlineReached, null));
        }
    }

    /// <summary>One sample rendered and compared; false when there is no renderer (one <c>render.not_configured</c> entry, no more samples).</summary>
    private async Task<bool> CompareAsync(Guid jobId, string level, RenderPlan plan, PreviewBlobs store, RenderSample sample, List<PreflightCheckResult> checks,
        CancellationToken cancellationToken)
    {
        var host = sample.Url.Authority;
        RenderLease? render;
        string? refusal;
        using (var wait = new CancellationTokenSource(RenderSlotWait, time))
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Token))
        {
            try
            {
                (render, refusal) = await PreviewHosts.RenderSlotAsync(renders, host, time, waiting.Token);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                checks.Add(Check(Render, PreflightState.Pass, RenderReasons.Busy, sample.Target));
                return true;
            }
        }

        if (render is null)
        {
            checks.Add(Check(Render, PreflightState.Pass, refusal!, null));
            return false;
        }

        using (render)
        {
            var (lease, _) = await PreviewHosts.SlotAsync(budget, host, time, cancellationToken);
            if (lease is null)
            {
                return true; // the probe already reported the host's refusal
            }

            PageResult page;
            using (lease)
            {
                var request = new HttpFetchRequest(sample.Url, EgressPurpose.Preflight, null, MediaTypes.DocumentFormats, plan.Canonicalizer, robots.CheckAsync);
                var context = new FetchContext(Guid.Empty, level, jobId, plan.Settings, Revalidate: true);
                var fetchStarted = time.GetTimestamp();
                page = await pages.FetchAsync(new PageRequest(context, request, store, plan.Browser), cancellationToken);
                await lease.CompleteAsync(PreviewHosts.Response(page.Outcome, PageLatency.Of(page.Outcome, time.GetElapsedTime(fetchStarted))), cancellationToken);
            }

            if (RenderReasons.Of(page.Outcome) is { } reason)
            {
                checks.Add(Check(Render, PreflightState.Pass, reason, sample.Target));
            }
            else if (page.Outcome is FetchOutcome.Content { Facts.FetchMode: Domain.Entities.FetchModes.Browser } content
                     && await RenderedWordsAsync(store, content, level, cancellationToken) is { } rendered)
            {
                var (state, code) = rendered >= MinRenderedWords && sample.RawWords * 2 < rendered
                    ? (plan.Mode == FetchMode.Http ? PreflightState.Warn : PreflightState.Pass, RenderSuggested)
                    : rendered < MinRenderedWords && (rendered == 0 || rendered < sample.RawWords)
                        ? (PreflightState.Warn, RenderEmpty)
                        : (PreflightState.Pass, RenderCompared);
                checks.Add(Check(Render, state, code, sample.Target, new { rawWords = sample.RawWords, renderedWords = rendered }));
            }
        }

        return true;
    }

    /// <summary>The main-text words of the rendered DOM the fetch stored in the preview's blobs; null when it is gone.</summary>
    private async Task<int?> RenderedWordsAsync(PreviewBlobs store, FetchOutcome.Content content, string level, CancellationToken cancellationToken)
    {
        await using var stream = await store.OpenReadAsync(content.Raw.Key, null, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
        return await WordsAsync(document, level, content.Facts.FinalUrl!, cancellationToken);
    }

    /// <summary>The words of a page's main text as the HTML extraction keeps it with the host's applied site profile (else a preset).</summary>
    private async Task<int> WordsAsync(IHtmlDocument document, string level, Uri url, CancellationToken cancellationToken)
    {
        var profile = await siteProfiles.AppliedAsync(level, url.AbsoluteUri, cancellationToken);
        return Words(extractor.Extract(document, profile?.Profile).Document.Blocks);
    }

    /// <summary>Runs of letters and digits in the blocks' text.</summary>
    private static int Words(IEnumerable<DocBlock> blocks)
    {
        var count = 0;
        foreach (var block in blocks)
        {
            var inWord = false;
            foreach (var c in block.Text)
            {
                var letter = char.IsLetterOrDigit(c);
                if (letter && !inWord)
                {
                    count++;
                }

                inWord = letter;
            }
        }

        return count;
    }

    /// <summary>How the comparison fetches: the form's settings with <c>fetchMode</c> <c>browser</c>; <paramref name="mode"/> is the form's own.</summary>
    private static RenderPlan Plan(FetchMode mode, string? readySelector, bool scrollToLoad, IReadOnlyList<string> noiseSelectors,
        CanonicalizerOptions canonicalizer, JsonElement settings) =>
        new(new PageSettings(FetchMode.Browser, readySelector, scrollToLoad, noiseSelectors), mode, canonicalizer, settings);

    private static void AddOrigin(List<Uri> origins, Uri? asked)
    {
        if (asked is not null && !origins.Any(o => o.Scheme == asked.Scheme && o.Authority == asked.Authority))
        {
            origins.Add(asked);
        }
    }

    /// <summary>The robots.txt status of <paramref name="origin"/>'s host (the entry the fetches above cached).</summary>
    private async Task<PreflightCheckResult> RobotsAsync(Uri origin, PreflightState unreachable, CancellationToken cancellationToken)
    {
        var entry = await robots.GetAsync(origin, cancellationToken);
        var target = origin.GetLeftPart(UriPartial.Authority);
        return entry switch
        {
            // The host's politeness profile ignores robots.txt (connectors design §6.6): nothing it says refuses a page.
            { Ignored: true, EgressRefusal: null } => Check(Robots, PreflightState.Pass, "robots.ignored", target),
            { Status: RobotsStatus.Ok } => Check(Robots, PreflightState.Pass, "robots.ok", target),
            { Status: RobotsStatus.Missing } => Check(Robots, PreflightState.Pass, "robots.missing", target),
            _ => Check(entry.EgressRefusal is null ? Robots : Egress, unreachable, entry.EgressRefusal ?? CrawlConnector.RobotsUnreachable, target),
        };
    }

    private static PreflightCheckResult Check(string check, PreflightState state, string code, string? target, object? facts = null) => new()
    {
        Check = check,
        State = state,
        Code = code,
        Target = target,
        Facts = facts is null ? null : JsonSerializer.SerializeToElement(facts, ContractJson.Options),
    };

    private static T? Bind<T>(JsonElement settings) where T : class
    {
        try
        {
            return ContractJson.Deserialize<T>(settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the checks learned on the way: the CMS of the first recognised HTML page and the page estimate.</summary>
    private sealed class Findings
    {
        public string? Cms { get; set; }

        public string? CmsHost { get; set; }

        public int? EstimatedPages { get; set; }

        /// <summary>The HTML pages the comparison renders, with their raw main-text words.</summary>
        public List<RenderSample> Samples { get; } = [];
    }

    /// <param name="Target">The URL as the form gave it (the entry's target).</param>
    /// <param name="Url">The canonical URL fetched.</param>
    /// <param name="RawWords">The raw page's main-text words.</param>
    private sealed record RenderSample(string Target, Uri Url, int RawWords);

    private sealed record RenderPlan(PageSettings Browser, FetchMode Mode, CanonicalizerOptions Canonicalizer, JsonElement Settings);

    [LoggerMessage(LogLevel.Information, "Preflight {JobId} ({SourceType}): {State}")]
    private static partial void LogPreflight(ILogger logger, Guid jobId, string sourceType, string state);
}
