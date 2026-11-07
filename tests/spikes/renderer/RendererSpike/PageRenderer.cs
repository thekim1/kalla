using System.Collections.Concurrent;
using System.Diagnostics;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;

namespace RendererSpike;

public enum ReadyMode
{
    /// <summary>load fired and no routed request open for the quiet period (connectors design §8.3).</summary>
    Quiet,

    /// <summary>The ready selector holds text.</summary>
    Selector,

    /// <summary>Playwright's networkidle (500 ms without network activity), for comparison.</summary>
    NetworkIdle,
}

public sealed record RenderOptions
{
    public ReadyMode Ready { get; init; } = ReadyMode.Quiet;

    public string? ReadySelector { get; init; }

    public int QuietMs { get; init; } = 100;

    public int TimeoutMs { get; init; } = 20_000;

    /// <summary>Images, fonts, media and the tracker hosts answered with 204.</summary>
    public bool Block { get; init; } = true;

    public bool UseCache { get; init; } = true;

    /// <summary>Iframe documents answered with an empty page (the output drops iframes anyway).</summary>
    public bool BlockIframes { get; init; } = true;

    public bool RouteWebSockets { get; init; } = true;

    public ServiceWorkerPolicy ServiceWorkers { get; init; } = ServiceWorkerPolicy.Block;

    /// <summary>Answer a script/meta-refresh/popup navigation with 204 (the document stays) instead of aborting it.</summary>
    public bool NavigationNoContent { get; init; } = true;

    /// <summary>Scroll to the bottom up to this many times (until the height stops growing), the quiet rule after each.</summary>
    public int ScrollSteps { get; init; }

    public int ViewportHeight { get; init; } = 720;

    public int MaxRequests { get; init; } = 50;

    public long MaxBytes { get; init; } = 20 * 1024 * 1024;

    public static IReadOnlySet<string> BlockedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tracker.test" };
}

public sealed class RenderStats
{
    public int Routed;
    public int Fetched;
    public int Cached;
    public int Blocked;
    public int Refused;
    public int Truncated;
    public int WebSocketsClosed;
    public int NavigationsAborted;
    public long BytesFetched;
    public long FetchedFromFixtureMs;
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>Every request the context reports (routed or not) and every failure, to find requests that skip the route.</summary>
    public ConcurrentQueue<string> Seen { get; } = new();

    /// <summary>Aborted main-frame navigations (script, meta refresh, popups, download links): the URLs to follow worker-side.</summary>
    public ConcurrentQueue<string> Navigations { get; } = new();
}

public sealed record RenderResult(
    string Html,
    string Text,
    bool Ready,
    double RenderMs,
    double ReadyMs,
    double ScrollMs,
    int ScrollRequests,
    double SerializeMs,
    RenderStats Stats,
    string? Error);

/// <summary>
/// One page in a fresh context on a shared browser: every request routed to the worker-side fetch, the main document
/// fulfilled from the body the worker already has, ready by open-request tracking, the DOM serialised without scripts.
/// </summary>
public sealed class PageRenderer(RenderFetcher fetcher, AssetCache cache)
{
    private static readonly HtmlParser Parser = new();

    public async Task<RenderResult> RenderAsync(IBrowser browser, Uri url, SubResponse main, RenderOptions options, bool newContext = true)
    {
        var stats = new RenderStats();
        var total = Stopwatch.StartNew();
        IBrowserContext context = newContext
            ? await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ServiceWorkers = options.ServiceWorkers,
                AcceptDownloads = false,
                ViewportSize = new ViewportSize { Width = 1280, Height = options.ViewportHeight },
                UserAgent = RenderFetcher.UserAgent,
            })
            : browser.Contexts[0];
        var open = 0;
        var lastActivity = Stopwatch.GetTimestamp();
        var bytes = 0L;
        IPage? page = null;
        try
        {
            await context.RouteAsync("**/*", route => HandleAsync(route));
            if (options.RouteWebSockets)
            {
                await context.RouteWebSocketAsync("**/*", ws =>
                {
                    Interlocked.Increment(ref stats.WebSocketsClosed);
                    stats.Log.Enqueue("ws-closed " + ws.Url);
                    _ = ws.CloseAsync(new WebSocketRouteCloseOptions { Code = 1008, Reason = "not allowed" });
                });
            }

            context.Page += (_, p) => stats.Log.Enqueue("context.Page event " + p.Url);
            context.Request += (_, r) => stats.Seen.Enqueue($"{r.ResourceType} {r.Method} {r.Url}");
            context.RequestFailed += (_, r) => stats.Seen.Enqueue($"FAILED {r.Url}: {r.Failure}");
            page = await context.NewPageAsync();
            page.Download += (_, d) => stats.Log.Enqueue("download event " + d.Url);
            var deadline = Stopwatch.StartNew();
            await page.GotoAsync(url.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = options.Ready == ReadyMode.NetworkIdle ? WaitUntilState.NetworkIdle : WaitUntilState.Load,
                Timeout = options.TimeoutMs,
            });
            var ready = options.Ready switch
            {
                ReadyMode.Quiet => await QuietAsync(),
                ReadyMode.Selector => await SelectorAsync(page, options, deadline),
                _ => true,
            };
            var readyMs = deadline.Elapsed.TotalMilliseconds;

            var scroll = Stopwatch.StartNew();
            var before = stats.Routed;
            for (var step = 0; step < options.ScrollSteps && deadline.ElapsedMilliseconds < options.TimeoutMs; step++)
            {
                var routedBefore = stats.Routed;
                var height = await page.EvaluateAsync<double>("() => { const h = document.documentElement.scrollHeight; window.scrollTo(0, h); return h; }");
                await Task.Delay(50); // IntersectionObserver callbacks run on the next frames.
                await QuietAsync();
                var after = await page.EvaluateAsync<double>("() => document.documentElement.scrollHeight");
                if (after <= height && stats.Routed == routedBefore)
                {
                    break;
                }
            }

            var scrollMs = options.ScrollSteps > 0 ? scroll.Elapsed.TotalMilliseconds : 0;
            var scrollRequests = stats.Routed - before;

            var serialize = Stopwatch.StartNew();
            var content = await page.ContentAsync();
            var (html, text) = Clean(content);
            var serializeMs = serialize.Elapsed.TotalMilliseconds;
            return new RenderResult(html, text, ready, total.Elapsed.TotalMilliseconds, readyMs, scrollMs, scrollRequests, serializeMs, stats, null);

            async Task<bool> QuietAsync()
            {
                while (deadline.ElapsedMilliseconds < options.TimeoutMs)
                {
                    if (Volatile.Read(ref open) == 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)).TotalMilliseconds >= options.QuietMs)
                    {
                        return true;
                    }

                    await Task.Delay(10);
                }

                return false;
            }
        }
        catch (Exception e)
        {
            return new RenderResult("", "", false, total.Elapsed.TotalMilliseconds, 0, 0, 0, 0, stats, e.GetType().Name + ": " + e.Message.Split('\n')[0]);
        }
        finally
        {
            if (newContext)
            {
                await context.CloseAsync();
            }
            else if (page is not null)
            {
                await page.CloseAsync();
                await context.UnrouteAllAsync(new BrowserContextUnrouteAllOptions { Behavior = UnrouteBehavior.IgnoreErrors });
            }
        }

        async Task HandleAsync(IRoute route)
        {
            Interlocked.Increment(ref open);
            Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
            var n = Interlocked.Increment(ref stats.Routed);
            var request = route.Request;
            var target = new Uri(request.Url);
            try
            {
                var isMainFrame = false;
                try
                {
                    isMainFrame = request.IsNavigationRequest && request.Frame == request.Frame.Page.MainFrame;
                }
                catch (PlaywrightException)
                {
                    // Worker and service-worker requests have no frame.
                }

                stats.Log.Enqueue($"{request.ResourceType} {request.Method} {request.Url}");
                if (isMainFrame)
                {
                    if (target.AbsoluteUri == url.AbsoluteUri)
                    {
                        await FulfillAsync(route, main);
                    }
                    else
                    {
                        Interlocked.Increment(ref stats.NavigationsAborted);
                        var popup = page is not null && request.Frame.Page != page;
                        stats.Navigations.Enqueue((popup ? "popup " : "main ") + request.Url);
                        if (options.NavigationNoContent)
                        {
                            // A 204 answer to a navigation leaves the current document in place (HTML navigation rules).
                            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
                        }
                        else
                        {
                            await route.AbortAsync("blockedbyclient");
                        }
                    }

                    return;
                }

                if (request.IsNavigationRequest && options.BlockIframes)
                {
                    Interlocked.Increment(ref stats.Blocked);
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html", Body = "" });
                    return;
                }

                if (options.Block && (request.ResourceType is "image" or "font" or "media" || RenderOptions.BlockedHosts.Contains(target.Host)))
                {
                    Interlocked.Increment(ref stats.Blocked);
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
                    return;
                }

                if (n > options.MaxRequests || Interlocked.Read(ref bytes) > options.MaxBytes)
                {
                    Interlocked.Increment(ref stats.Truncated);
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
                    return;
                }

                var headerArray = await request.HeadersArrayAsync();
                if (headerArray.FirstOrDefault(h => h.Name.Equals("cookie", StringComparison.OrdinalIgnoreCase)) is { } cookie)
                {
                    var all = await request.AllHeadersAsync();
                    stats.Log.Enqueue($"cookie on {request.Url}: HeadersArrayAsync '{cookie.Value}', AllHeadersAsync {(all.ContainsKey("cookie") ? "has it" : "lacks it")}, Headers {(request.Headers.ContainsKey("cookie") ? "has it" : "lacks it")}");
                }

                var sub = new SubRequest(request.Method, target, [.. headerArray.Select(h => KeyValuePair.Create(h.Name, h.Value))], request.PostDataBuffer, request.ResourceType);
                if (options.UseCache && AssetCache.Cacheable(sub) && cache.TryGet(target, out var hit))
                {
                    Interlocked.Increment(ref stats.Cached);
                    await FulfillAsync(route, hit);
                    return;
                }

                var fetchClock = Stopwatch.StartNew();
                var response = await fetcher.FetchAsync(sub, CancellationToken.None);
                Interlocked.Add(ref stats.FetchedFromFixtureMs, fetchClock.ElapsedMilliseconds);
                if (response.Refused is not null)
                {
                    Interlocked.Increment(ref stats.Refused);
                    stats.Log.Enqueue($"refused {response.Refused} {request.Url}");
                }
                else
                {
                    Interlocked.Increment(ref stats.Fetched);
                    Interlocked.Add(ref stats.BytesFetched, response.Body.Length);
                    Interlocked.Add(ref bytes, response.Body.Length);
                    if (options.UseCache && AssetCache.Cacheable(sub))
                    {
                        cache.Put(target, response);
                    }
                }

                await FulfillAsync(route, response);
            }
            catch (Exception e)
            {
                stats.Log.Enqueue($"route-error {e.GetType().Name} {e.Message.Split('\n')[0]} ({request.Url})");
            }
            finally
            {
                Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                Interlocked.Decrement(ref open);
            }
        }
    }

    private static Task FulfillAsync(IRoute route, SubResponse response) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = response.Status,
            Headers = response.Headers,
            BodyBytes = response.Body,
        });

    /// <summary>
    /// The rendered DOM without <c>script</c>, <c>noscript</c>, <c>style</c>, <c>template</c>, <c>iframe</c> and inline
    /// <c>on*</c> handlers, cleaned in the worker with AngleSharp from <see cref="IPage.ContentAsync"/> (serialised by
    /// Playwright in its isolated utility world, so page script cannot tamper with the serialiser).
    /// </summary>
    public static (string Html, string Text) Clean(string content)
    {
        using var document = Parser.ParseDocument(content);
        foreach (var element in document.QuerySelectorAll("script, noscript, style, template, iframe").ToList())
        {
            element.Remove();
        }

        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes.Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                element.RemoveAttribute(attribute.Name);
            }
        }

        var html = "<!DOCTYPE html>" + document.DocumentElement.OuterHtml;
        var text = string.Join(' ', (document.Body?.TextContent ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (html, text);
    }

    private static async Task<bool> SelectorAsync(IPage page, RenderOptions options, Stopwatch deadline)
    {
        var remaining = Math.Max(1, options.TimeoutMs - (int)deadline.ElapsedMilliseconds);
        try
        {
            // Playwright selectors (CSS plus :has-text) wait in the isolated world; then the element must hold text.
            await page.WaitForSelectorAsync(options.ReadySelector!, new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = remaining });
            while (deadline.ElapsedMilliseconds < options.TimeoutMs)
            {
                if ((await page.Locator(options.ReadySelector!).First.InnerTextAsync()).Trim().Length > 0)
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
