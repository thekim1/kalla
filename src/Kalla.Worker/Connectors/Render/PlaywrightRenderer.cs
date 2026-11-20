using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Kalla.Worker.Connectors.Render;

/// <summary>
/// <see cref="IRenderer"/> on <c>kalla-renderer</c> (connectors design §8.3): Playwright .NET connects to the container's
/// <c>launch-server</c> at <c>Render:Endpoint</c> (secret path included) and drives one shared Chromium; each page gets a
/// fresh context (service workers blocked, downloads off, <c>Render:Viewport</c>, the host's User-Agent) that is closed
/// after it, cookies and all. Every request of the page is routed to the worker's <see cref="IRenderFetcher"/>; the first
/// main-frame navigation is fulfilled from the body the worker already fetched; a later one (script, meta refresh) is
/// answered 204, which keeps the document, and recorded: before ready as a redirect hop (<see cref="RenderResult.Navigation"/>),
/// after it as a discovered link. Iframe documents get an empty page, popups and downloads become discovered links (popups
/// are closed at once), and every WebSocket is closed. Ready, within <c>Render:PageTimeout</c>: <c>load</c> fired, the
/// source's <c>readySelector</c> (when set) holds text, and then no routed request has been open for
/// <c>Render:QuietPeriod</c>, so a label the app renders before its data cannot make the page ready on its own (#490).
/// Not ready is <c>render.bad_selector</c> when the browser refuses the selector (<see cref="IsBadSelector"/>),
/// <c>render.selector_not_found</c> when the selector held no text by the deadline while the page was quiet, and
/// <c>render.timeout</c> otherwise (<see cref="RenderResult.NotReadyReason"/>). Then <c>scrollToLoad</c> scrolls until the
/// page stops growing, and the DOM is serialised and cleaned
/// (<see cref="RenderedHtml"/>). Browser operations are bounded (a context or page within 15 s, a close within 5 s); a
/// failed or timed-out one drops the connection (<c>render.unavailable</c> for the page, a reconnect for the next), and
/// the connection and Playwright's Node driver are closed after 60 s without a render.
/// </summary>
public sealed partial class PlaywrightRenderer(IOptions<RenderOptions> options, TimeProvider time, ILogger<PlaywrightRenderer> logger) : IRenderer, IAsyncDisposable
{
    /// <summary>Connecting, creating a context or page, serialising.</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Closing a context or the connection.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The connection is closed after this long without a render.</summary>
    public static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The messages of Playwright's selector errors (driver 1.65.1): its CSS parser ("… while parsing css selector …"),
    /// its selector parser ("Unknown engine … while parsing selector …", "Malformed selector: …") and the browser's own
    /// <c>querySelectorAll</c> ("… is not a valid selector"). The driver's <c>InvalidSelectorError</c> keeps the name
    /// <c>Error</c>, so it reaches .NET as a plain <see cref="PlaywrightException"/>: the message is all there is.
    /// </summary>
    private static readonly string[] BadSelectorMessages = ["while parsing css selector", "while parsing selector", "Malformed selector", "is not a valid selector"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private ITimer? _idle;
    private int _active;

    /// <summary>Starts Playwright's driver; a test replaces it with a fake browser.</summary>
    internal Func<Task<IPlaywright>> CreatePlaywright { get; init; } = Playwright.CreateAsync;

    /// <summary>Whether <paramref name="exception"/> is Playwright refusing a selector, not a navigation the page interrupted.</summary>
    internal static bool IsBadSelector(PlaywrightException exception) =>
        BadSelectorMessages.Any(m => exception.Message.Contains(m, StringComparison.Ordinal));

    public async ValueTask<RenderResult> RenderAsync(RenderRequest request, IRenderFetcher fetcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fetcher);
        Interlocked.Increment(ref _active);
        try
        {
            var browser = await BrowserAsync(cancellationToken);
            var session = new PageSession(this, request, fetcher, time, options.Value.ViewportSize());
            return await session.RunAsync(browser, cancellationToken);
        }
        finally
        {
            if (Interlocked.Decrement(ref _active) == 0)
            {
                ArmIdle();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _idle?.Dispose();
        await _gate.WaitAsync();
        try
        {
            await CloseAllAsync();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<IBrowser> BrowserAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _browser) is { IsConnected: true } connected)
        {
            return connected;
        }

        if (!options.Value.Configured)
        {
            throw new RenderUnavailableException("Render:Endpoint is not set.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsConnected: true } current)
            {
                return current;
            }

            try
            {
                _playwright ??= await CreatePlaywright().WaitAsync(OperationTimeout, cancellationToken);
                var browser = await _playwright.Chromium.ConnectAsync(options.Value.Endpoint!,
                        new BrowserTypeConnectOptions { Timeout = (float)OperationTimeout.TotalMilliseconds })
                    .WaitAsync(OperationTimeout + CloseTimeout, cancellationToken);
                browser.Disconnected += (_, _) => Interlocked.CompareExchange(ref _browser, null, browser);
                _browser = browser;
                LogConnected(logger, browser.Version);
                return browser;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogConnectFailed(logger, e.GetType().Name);
                throw new RenderUnavailableException("The renderer could not be reached.", e);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets <paramref name="browser"/> after a failed or timed-out operation; the next render reconnects.</summary>
    private void Drop(IBrowser browser, Exception reason)
    {
        if (Interlocked.CompareExchange(ref _browser, null, browser) == browser)
        {
            LogDropped(logger, reason.GetType().Name);
            _ = CloseQuietlyAsync(browser.CloseAsync());
        }
    }

    private void ArmIdle()
    {
        var timer = time.CreateTimer(_ => _ = CloseIdleAsync(), null, IdleClose, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _idle, timer)?.Dispose();
    }

    private async Task CloseIdleAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _active) == 0 && _browser is not null)
            {
                await CloseAllAsync();
                LogIdleClosed(logger);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection and stops Playwright's Node driver; under the gate.</summary>
    private async Task CloseAllAsync()
    {
        if (Interlocked.Exchange(ref _browser, null) is { } browser)
        {
            await CloseQuietlyAsync(browser.CloseAsync());
        }

        _playwright?.Dispose();
        _playwright = null;
    }

    private static async Task CloseQuietlyAsync(Task close)
    {
        try
        {
            await close.WaitAsync(CloseTimeout);
        }
        catch (Exception)
        {
            // Closing what is gone already, or a server that no longer answers: nothing is left to free here.
        }
    }

    [LoggerMessage(LogLevel.Information, "Renderer connected (browser {BrowserVersion})")]
    private static partial void LogConnected(ILogger logger, string browserVersion);

    [LoggerMessage(LogLevel.Warning, "Renderer connection failed ({ExceptionType})")]
    private static partial void LogConnectFailed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Warning, "Renderer connection dropped after a failed browser operation ({ExceptionType})")]
    private static partial void LogDropped(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Information, "Renderer connection closed after 60 s without a render")]
    private static partial void LogIdleClosed(ILogger logger);

    /// <summary>One page in its own context.</summary>
    private sealed class PageSession(PlaywrightRenderer renderer, RenderRequest request, IRenderFetcher fetcher, TimeProvider time, (int Width, int Height) viewport)
    {
        private readonly ConcurrentQueue<Uri> _discovered = new();
        private long _started;
        private long _lastActivity;
        private int _open;
        private int _mainServed;
        private int _ready;
        private Uri? _navigation;
        private IPage? _page;
        private int _fetched;
        private int _cached;
        private int _blocked;
        private int _refused;
        private int _truncated;
        private int _failed;

        public async Task<RenderResult> RunAsync(IBrowser browser, CancellationToken cancellationToken)
        {
            _started = time.GetTimestamp();
            _lastActivity = _started;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.Limits.PageTimeout);
            var pageTime = deadline.Token;
            IBrowserContext context;
            try
            {
                context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ServiceWorkers = ServiceWorkerPolicy.Block,
                    AcceptDownloads = false,
                    ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height },
                    UserAgent = request.UserAgent,
                }).WaitAsync(OperationTimeout, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                renderer.Drop(browser, e);
                throw new RenderUnavailableException("No browser context.", e);
            }

            try
            {
                await context.RouteAsync("**/*", route => RouteAsync(route, pageTime)).WaitAsync(OperationTimeout, cancellationToken);
                // Mandatory (§8.3): a page's own WebSocket could otherwise reach the renderer's port.
                await context.RouteWebSocketAsync("**/*", socket => _ = socket.CloseAsync(new WebSocketRouteCloseOptions { Code = 1008, Reason = "not allowed" }))
                    .WaitAsync(OperationTimeout, cancellationToken);
                var page = await context.NewPageAsync().WaitAsync(OperationTimeout, cancellationToken);
                _page = page;
                page.Popup += (_, popup) => OnPopup(popup);
                page.Download += (_, download) => Discover(download.Url);

                var notReady = await ReadyAsync(page);
                if (_navigation is { } navigation)
                {
                    return Result(string.Empty, ready: false) with { Navigation = navigation };
                }

                if (notReady is not null)
                {
                    return Result(string.Empty, ready: false) with { NotReadyReason = notReady };
                }

                Volatile.Write(ref _ready, 1);
                if (request.ScrollToLoad)
                {
                    await ScrollAsync(page);
                }

                var html = RenderedHtml.Clean(await page.ContentAsync().WaitAsync(OperationTimeout, cancellationToken));
                return Result(html, ready: true);
            }
            catch (Exception e) when (e is PlaywrightException or TimeoutException)
            {
                renderer.Drop(browser, e);
                throw new RenderUnavailableException("A browser operation failed.", e);
            }
            finally
            {
                try
                {
                    await context.CloseAsync().WaitAsync(CloseTimeout, CancellationToken.None);
                }
                catch (Exception e)
                {
                    renderer.Drop(browser, e);
                }
            }
        }

        /// <summary>
        /// The ready rule within the page's time (§8.3): <c>load</c>, then the <c>readySelector</c>'s element holding text
        /// when there is one, then the quiet rule. Null when ready; else the reason (<see cref="RenderResult.NotReadyReason"/>).
        /// A navigation away first is in <see cref="_navigation"/>, which the caller checks before the reason.
        /// </summary>
        private async Task<string?> ReadyAsync(IPage page)
        {
            var loaded = false;
            try
            {
                await page.GotoAsync(request.Url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Remaining() });
                loaded = true;
                if (request.ReadySelector is { } selector && !await HoldsTextAsync(page, selector))
                {
                    return SelectorMissed();
                }

                // A label the app renders before its data holds text too: the page is ready only once it is quiet as well (#490).
                return await QuietAsync() ? null : RenderReasons.Timeout;
            }
            catch (TimeoutException)
            {
                // Playwright's own timeouts (load, selector) are System.TimeoutException.
                return loaded ? SelectorMissed() : RenderReasons.Timeout;
            }
            catch (PlaywrightException e) when (IsBadSelector(e))
            {
                return RenderReasons.BadSelector;
            }
            catch (PlaywrightException) when (page.Context.Browser?.IsConnected == true && !page.IsClosed)
            {
                // A navigation the page itself interrupted: not ready (render.timeout).
                return RenderReasons.Timeout;
            }
        }

        /// <summary>Whether the selector's first element holds text within the page's time; false at the deadline or after a navigation.</summary>
        private async Task<bool> HoldsTextAsync(IPage page, string selector)
        {
            var wait = page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = Remaining() });
            if (await Task.WhenAny(wait, NavigatedAsync()) != wait)
            {
                return false;
            }

            await wait;
            var element = page.Locator(selector).First;
            while (TimeLeft && _navigation is null)
            {
                // A selector on a container that shows a "Loading…" placeholder holds text too early: name the loaded content (§8.3).
                if ((await element.InnerTextAsync(new LocatorInnerTextOptions { Timeout = Remaining() })).Trim().Length > 0)
                {
                    return true;
                }

                await Task.Delay(Poll * 2, time);
            }

            return false;
        }

        /// <summary>
        /// The selector held no text by the deadline: <c>render.selector_not_found</c> when the loaded page was quiet then (it
        /// would not have come), else <c>render.timeout</c> (the page was still loading).
        /// </summary>
        private string SelectorMissed() => Quiet ? RenderReasons.SelectorNotFound : RenderReasons.Timeout;

        /// <summary>No routed request open for the quiet period; false at the deadline or after a navigation.</summary>
        private async Task<bool> QuietAsync()
        {
            while (TimeLeft && _navigation is null)
            {
                if (Quiet)
                {
                    return true;
                }

                await Task.Delay(Poll, time);
            }

            return false;
        }

        private async Task NavigatedAsync()
        {
            while (TimeLeft && _navigation is null)
            {
                await Task.Delay(Poll, time);
            }
        }

        /// <summary>Scrolls to the bottom until the height stops growing and no request was made, at most 10 steps, the quiet rule after each.</summary>
        private async Task ScrollAsync(IPage page)
        {
            for (var step = 0; step < RenderRequest.MaxScrollSteps && TimeLeft; step++)
            {
                var requestsBefore = Volatile.Read(ref _fetched) + Volatile.Read(ref _cached);
                var height = await page.EvaluateAsync<double>("() => { const h = document.documentElement.scrollHeight; window.scrollTo(0, h); return h; }");
                // IntersectionObserver callbacks run on the next frames.
                await Task.Delay(Poll * 5, time);
                await QuietAsync();
                var after = await page.EvaluateAsync<double>("() => document.documentElement.scrollHeight");
                if (after <= height && Volatile.Read(ref _fetched) + Volatile.Read(ref _cached) == requestsBefore)
                {
                    return;
                }
            }
        }

        private async Task RouteAsync(IRoute route, CancellationToken deadline)
        {
            Interlocked.Increment(ref _open);
            Touch();
            try
            {
                var browserRequest = route.Request;
                if (browserRequest.IsNavigationRequest && FrameOf(browserRequest) is { } frame)
                {
                    await NavigationAsync(route, browserRequest, frame);
                    return;
                }

                if (!Uri.TryCreate(browserRequest.Url, UriKind.Absolute, out var url))
                {
                    Interlocked.Increment(ref _refused);
                    await route.AbortAsync("blockedbyclient");
                    return;
                }

                var headers = await browserRequest.HeadersArrayAsync();
                var response = await fetcher.FetchAsync(
                    new RenderSubrequest(browserRequest.Method, url, [.. headers.Select(h => KeyValuePair.Create(h.Name, h.Value))], browserRequest.PostDataBuffer,
                        browserRequest.ResourceType),
                    deadline);
                Count(response.Kind);
                if (response.AbortReason is { } reason)
                {
                    await route.AbortAsync(reason);
                }
                else
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = response.Status, Headers = response.Headers, BodyBytes = response.Body });
                }
            }
            catch (OperationCanceledException)
            {
                // The page's time is up; the context closes with the route.
            }
            catch (PlaywrightException)
            {
                // The page or its context closed while the request was answered.
            }
            finally
            {
                Touch();
                Interlocked.Decrement(ref _open);
            }
        }

        /// <summary>The document itself (the first main-frame navigation), a later main-frame navigation, a popup's, or an iframe's.</summary>
        private async Task NavigationAsync(IRoute route, IRequest navigation, IFrame frame)
        {
            if (_page is { } page && frame.Page != page)
            {
                Discover(navigation.Url);
                await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
                return;
            }

            if (frame.ParentFrame is not null)
            {
                // Iframe documents are not fetched; the output drops iframes (§8.3).
                Interlocked.Increment(ref _blocked);
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html", Body = string.Empty });
                return;
            }

            if (Interlocked.Exchange(ref _mainServed, 1) == 0)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, Headers = MainHeaders(), BodyBytes = await MainBodyAsync() });
                return;
            }

            // Script navigation or meta refresh: a 204 keeps the current document (an abort would replace it with an error page).
            if (Uri.TryCreate(navigation.Url, UriKind.Absolute, out var target))
            {
                if (Volatile.Read(ref _ready) == 0)
                {
                    Interlocked.CompareExchange(ref _navigation, target, null);
                }
                else
                {
                    _discovered.Enqueue(target);
                }
            }

            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
        }

        private Dictionary<string, string> MainHeaders()
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = "text/html" + (request.MainDocument.Charset is { Length: > 0 } charset ? "; charset=" + charset : string.Empty),
            };
            if (request.SetCookies.Count > 0)
            {
                headers["set-cookie"] = string.Join('\n', request.SetCookies);
            }

            return headers;
        }

        private async Task<byte[]> MainBodyAsync()
        {
            await using var stream = request.MainDocument.OpenRead();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        /// <summary>A popup's URL is a discovered link; the popup is closed at once (its requests fail closed on the render network anyway).</summary>
        private void OnPopup(IPage popup)
        {
            Discover(popup.Url);
            popup.Request += (_, r) =>
            {
                if (r.IsNavigationRequest)
                {
                    Discover(r.Url);
                }
            };
            _ = CloseQuietlyAsync(popup.CloseAsync());
        }

        private void Discover(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Scheme is "http" or "https")
            {
                _discovered.Enqueue(link);
            }
        }

        private void Count(RenderResponseKind kind)
        {
            switch (kind)
            {
                case RenderResponseKind.Fetched:
                    Interlocked.Increment(ref _fetched);
                    break;
                case RenderResponseKind.Cached:
                    Interlocked.Increment(ref _cached);
                    break;
                case RenderResponseKind.Blocked:
                    Interlocked.Increment(ref _blocked);
                    break;
                case RenderResponseKind.Refused:
                    Interlocked.Increment(ref _refused);
                    break;
                case RenderResponseKind.Truncated:
                    Interlocked.Increment(ref _truncated);
                    break;
                default:
                    Interlocked.Increment(ref _failed);
                    break;
            }
        }

        private RenderResult Result(string html, bool ready) =>
            new(html, new RenderStats(_fetched, _cached, _blocked, _refused, _truncated, _failed, time.GetElapsedTime(_started)), ready)
            {
                Discovered = [.. _discovered.DistinctBy(u => u.AbsoluteUri)],
            };

        private void Touch() => Interlocked.Exchange(ref _lastActivity, time.GetTimestamp());

        private bool TimeLeft => time.GetElapsedTime(_started) < request.Limits.PageTimeout;

        /// <summary>No routed request is open and none has been for <see cref="RenderLimits.QuietPeriod"/>.</summary>
        private bool Quiet => Volatile.Read(ref _open) == 0 && time.GetElapsedTime(Interlocked.Read(ref _lastActivity)) >= request.Limits.QuietPeriod;

        /// <summary>Milliseconds left of the page's time (Playwright's timeouts; 0 would mean none, so at least 1).</summary>
        private float Remaining() => (float)Math.Max(1, (request.Limits.PageTimeout - time.GetElapsedTime(_started)).TotalMilliseconds);

        /// <summary>The request's frame; null for a worker's request, which has none.</summary>
        private static IFrame? FrameOf(IRequest navigation)
        {
            try
            {
                return navigation.Frame;
            }
            catch (PlaywrightException)
            {
                return null;
            }
        }
    }
}
