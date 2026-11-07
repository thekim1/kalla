using System.Text;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Render;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Http;

/// <summary>How a URL-list or crawl source fetches its pages (connectors design §8.1, §12.2).</summary>
/// <param name="Mode">The source's <c>fetchMode</c>.</param>
/// <param name="ReadySelector">The source's <c>readySelector</c> (browser only).</param>
/// <param name="ScrollToLoad">The source's <c>scrollToLoad</c> (browser only).</param>
/// <param name="NoiseSelectors">The source's <c>noiseSelectors</c>.</param>
public sealed record PageSettings(FetchMode Mode, string? ReadySelector, bool ScrollToLoad, IReadOnlyList<string> NoiseSelectors);

/// <summary>One page to fetch and store.</summary>
/// <param name="Context">The fetch's source, level and run.</param>
/// <param name="Http">The request; for a <c>browser</c> source its validators are never sent (§8.5).</param>
/// <param name="Blobs">Where the raw content goes (a preview's own prefix for previews).</param>
/// <param name="Settings">The source's fetch settings.</param>
public sealed record PageRequest(FetchContext Context, HttpFetchRequest Http, IBlobStore Blobs, PageSettings Settings)
{
    /// <summary>Extract and store the page's links (a crawl).</summary>
    public bool ExtractLinks { get; init; }

    /// <summary>The host's soft-404 fingerprint for the run (§5.3); never compared with a rendered page.</summary>
    public string? SoftNotFound { get; init; }
}

/// <summary>The outcome of a page and, for stored HTML of a crawl, its links.</summary>
public sealed record PageResult(FetchOutcome Outcome, PageLinks? Links);

/// <summary>
/// The one fetch path of every HTTP source (connectors design §8.2): the crawl and URL-list connectors, the preflight and
/// the stage preview fetch → (for a <c>browser</c> source) render → store through it. The <see cref="HttpFetcher"/> GET
/// comes first, with its guard, robots.txt, redirects, meta refresh, type and size rules; a <c>browser</c> source sends no
/// validators (§8.5: an unchanged shell proves nothing). An HTML 200 of a <c>browser</c> source is then rendered
/// (<see cref="IRenderer"/>, every browser request through <see cref="RenderFetcher"/>): a navigation before ready is a
/// redirect hop the worker fetches (guard, scope and robots.txt checked, at most 5 hops in all) and renders instead; a
/// page that is not ready is <c>render.timeout</c> and an unreachable renderer <c>render.unavailable</c>, both transient
/// (§8.4) and never a fallback to the raw shell, so the document keeps its last good content. The rendered DOM is stored
/// as the document's raw content (<c>text/html</c>, <see cref="FetchFacts.FetchMode"/> <c>browser</c>) with the links
/// the page discovered. The caller holds the host's slot (and, for a <c>browser</c> source, a <see cref="RenderSlots"/>
/// lease) for the whole call. Each render is logged with its facts (fetch mode, duration, requests, truncation; no URL).
/// </summary>
public sealed partial class PageFetcher(
    HttpFetcher fetcher,
    IMalwareScanner scanner,
    IRenderer renderer,
    RenderFetchers renderFetchers,
    ICrawlPolicySource policy,
    RenderMetrics metrics,
    IOptions<RenderOptions> options,
    TimeProvider time,
    ILogger<PageFetcher> logger)
{
    private readonly RawContentWriter _writer = new(scanner, time);

    public async Task<PageResult> FetchAsync(PageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var browser = request.Settings.Mode == FetchMode.Browser;
        var result = await fetcher.FetchAsync(browser ? request.Http with { Validators = null } : request.Http, cancellationToken);
        if (result is not HttpFetchResult.Ok ok)
        {
            return new PageResult(FetchOutcomes.From(result), null);
        }

        await using (ok.Body)
        {
            return browser && MediaTypes.IsHtml(ok.Body.MediaType)
                ? await RenderAsync(request, ok, cancellationToken)
                : await StoreAsync(request, ok, request.SoftNotFound, [], FetchModes.Http, cancellationToken);
        }
    }

    /// <summary>The soft-404 fingerprint of a fetched HTML body (§5.3; <see cref="RawContentWriter.FingerprintAsync"/>).</summary>
    public Task<string?> FingerprintAsync(FetchedBody body, IReadOnlyList<string> noiseSelectors, CancellationToken cancellationToken) =>
        _writer.FingerprintAsync(body, noiseSelectors, cancellationToken);

    private async Task<PageResult> RenderAsync(PageRequest request, HttpFetchResult.Ok first, CancellationToken cancellationToken)
    {
        var context = request.Context;
        var limits = options.Value.Limits();
        var current = first;
        var redirects = new List<Uri>(first.Redirects);
        var hops = new List<FetchedBody>();
        try
        {
            while (true)
            {
                var page = renderFetchers.For(new RenderPage(current.FinalUrl, context.Classification, context.RunId, request.Http.Robots, limits));
                var render = new RenderRequest(current.FinalUrl, current.Body, request.Settings.ReadySelector, limits)
                {
                    ScrollToLoad = request.Settings.ScrollToLoad,
                    UserAgent = policy.Current.ProfileOf(current.FinalUrl.Authority).UserAgent,
                    SetCookies = current.SetCookies,
                };
                var started = time.GetTimestamp();
                RenderResult rendered;
                try
                {
                    rendered = await renderer.RenderAsync(render, page, cancellationToken);
                }
                catch (RenderUnavailableException)
                {
                    metrics.Rendered("unavailable", time.GetElapsedTime(started));
                    LogRenderFailed(logger, context.SourceId, context.RunId, RenderReasons.Unavailable);
                    return Transient(RenderReasons.Unavailable);
                }

                metrics.Requests(rendered.Stats);
                LogRendered(logger, context.SourceId, context.RunId, FetchModes.Browser, (int)rendered.Stats.Duration.TotalMilliseconds, rendered.Stats.Requests,
                    rendered.Stats.Truncated > 0, rendered.Ready);
                if (rendered.Navigation is { } target)
                {
                    metrics.Rendered("navigated", rendered.Stats.Duration);
                    var hop = await HopAsync(request, current, target, redirects, page, cancellationToken);
                    if (hop.Result is { } done)
                    {
                        return done;
                    }

                    hops.Add(hop.Next!.Body);
                    current = hop.Next;
                    continue;
                }

                if (!rendered.Ready)
                {
                    metrics.Rendered("timeout", rendered.Stats.Duration);
                    return Transient(RenderReasons.Timeout);
                }

                metrics.Rendered("ready", rendered.Stats.Duration);
                using var html = new MemoryStream(Encoding.UTF8.GetBytes(rendered.Html));
                await using var body = await FetchedBody.CaptureAsync(html, MediaTypes.BodyLimit("text/html"), truncate: false, "text/html", "utf-8", cancellationToken);
                if (body is null)
                {
                    return new PageResult(new FetchOutcome.Refused(RunItemOutcome.TooLarge, "http.body_limit"), null);
                }

                return await StoreAsync(request, current with { Body = body, Redirects = [.. redirects] }, null, rendered.Discovered, FetchModes.Browser,
                    cancellationToken);
            }
        }
        finally
        {
            foreach (var hop in hops)
            {
                await hop.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A navigation by script or meta refresh before ready (§3.2, §8.3): a redirect hop from <paramref name="current"/>,
    /// fetched by the worker in the host budget's slot with the guard, scope and robots.txt checks, counted in the 5 hops.
    /// Returns the next HTML page to render, or the page's final result (a refusal, or content that is not HTML).
    /// </summary>
    private async Task<(PageResult? Result, HttpFetchResult.Ok? Next)> HopAsync(
        PageRequest request, HttpFetchResult.Ok current, Uri target, List<Uri> redirects, RenderFetcher page, CancellationToken cancellationToken)
    {
        var canonicalizer = request.Http.Canonicalizer;
        redirects.Add(current.FinalUrl);
        var resolved = UrlCanonicalizer.Resolve(current.FinalUrl, target.AbsoluteUri, canonicalizer);
        if (!resolved.IsValid)
        {
            return (new PageResult(new FetchOutcome.Failed("redirect.invalid"), null), null);
        }

        var next = resolved.ToUri();
        if (redirects.Count > HttpFetcher.MaxRedirects || redirects.Any(r => r.AbsoluteUri == next.AbsoluteUri))
        {
            return (new PageResult(new FetchOutcome.Failed(HttpFetcher.RedirectLimit), null), null);
        }

        if (current.FinalUrl.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
        {
            return (new PageResult(new FetchOutcome.Refused(RunItemOutcome.Blocked, "redirect.downgrade"), null), null);
        }

        if (request.Http.InScope is { } inScope && !inScope(UrlCanonicalizer.ResolvePathForm(current.FinalUrl, target.AbsoluteUri, canonicalizer).ToUri()))
        {
            // A script redirect is never permanent: a skip, as a temporary redirect out of scope (§3.2).
            return (new PageResult(new FetchOutcome.Skipped("redirect.out_of_scope"), null), null);
        }

        using var lease = await page.SlotAsync(next, cancellationToken);
        if (lease is null)
        {
            return (Transient(HostBudget.Paused), null);
        }

        var started = time.GetTimestamp();
        var result = await fetcher.FetchAsync(request.Http with { Url = next, Validators = null }, cancellationToken);
        var outcome = result is HttpFetchResult.Ok ? null : FetchOutcomes.From(result);
        await lease.CompleteAsync(HostResponseOf(outcome, time.GetElapsedTime(started)), cancellationToken);
        if (result is not HttpFetchResult.Ok ok)
        {
            return (new PageResult(outcome!, null), null);
        }

        redirects.AddRange(ok.Redirects);
        if (MediaTypes.IsHtml(ok.Body.MediaType))
        {
            return (null, ok);
        }

        await using (ok.Body)
        {
            return (await StoreAsync(request, ok with { Redirects = [.. redirects] }, null, [], FetchModes.Http, cancellationToken), null);
        }
    }

    private async Task<PageResult> StoreAsync(PageRequest request, HttpFetchResult.Ok ok, string? softNotFound, IReadOnlyList<Uri> discovered, string fetchMode,
        CancellationToken cancellationToken)
    {
        var settings = request.Settings;
        var stored = await _writer.StoreAsync(request.Context, request.Blobs, ok, settings.NoiseSelectors, request.Http.Canonicalizer, request.ExtractLinks,
            softNotFound, discovered, cancellationToken);
        var outcome = stored.Outcome is FetchOutcome.Content content ? content with { Facts = content.Facts with { FetchMode = fetchMode } } : stored.Outcome;
        return new PageResult(outcome, stored.Links);
    }

    /// <summary>A failed render or hop (§8.4): transient, tried again after 5 minutes with an attempt; the document keeps its content.</summary>
    private static PageResult Transient(string reason) => new(new FetchOutcome.Transient(reason, RenderReasons.RetryAfter, CountsAttempt: true), null);

    private static HostResponse HostResponseOf(FetchOutcome? outcome, TimeSpan latency) => outcome switch
    {
        null or FetchOutcome.NotModified => new HostResponse(HostResponseKind.Success, latency),
        FetchOutcome.Transient { Reason: "rate_limited" or FetchOutcomes.Challenge, RetryAfter: var retryAfter } => new HostResponse(HostResponseKind.RateLimited, latency, retryAfter),
        FetchOutcome.Transient { Reason: "http.5xx" or "net.timeout" } or FetchOutcome.Gone { Reason: "http.404" } => new HostResponse(HostResponseKind.Failure, latency),
        _ => new HostResponse(HostResponseKind.Neutral, latency),
    };

    [LoggerMessage(LogLevel.Information,
        "Rendered a page of source {SourceId} run {RunId}: fetchMode {FetchMode}, renderMs {RenderMs}, requests {Requests}, truncated {Truncated}, ready {Ready}")]
    private static partial void LogRendered(ILogger logger, Guid sourceId, Guid runId, string fetchMode, int renderMs, int requests, bool truncated, bool ready);

    [LoggerMessage(LogLevel.Warning, "Render of a page of source {SourceId} run {RunId} failed: {Reason}")]
    private static partial void LogRenderFailed(ILogger logger, Guid sourceId, Guid runId, string reason);
}
