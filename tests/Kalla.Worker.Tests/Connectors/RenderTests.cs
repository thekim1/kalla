using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Crawl;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Render;
using Kalla.Worker.Connectors.Urls;
using PageLinks = Kalla.Worker.Connectors.Crawl.PageLinks;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// <c>browser</c> sources on a fake renderer (connectors design §8, A-11): the rendered DOM is the stored content with its
/// discovered links and fetch mode, no conditional GET is sent, a render slot is taken only when free (else the job goes
/// back without an attempt), a failed render keeps the last good content and the traversal its last good links (never the
/// raw shell), a <c>readySelector</c> the browser refuses or never finds fails the item at once, a navigation before ready is
/// a redirect hop; the real <see cref="PlaywrightRenderer"/>'s ready rule on a <see cref="FakeBrowser"/> page through the
/// worker's route (selector and quiet, the selector reasons kept apart from a page still loading); and the worker side of
/// the browser's requests (<see cref="RenderFetcher"/>): blocking, the method policy, the asset cache, the guard with <c>Purpose = Render</c>,
/// robots.txt, cookies within the page, redirects and the request limit.
/// </summary>
public sealed class RenderTests : IAsyncLifetime
{
    /// <summary>The data the slow API request answers with (<c>/api/slow</c>, after <see cref="SlowData"/>).</summary>
    private const string SlowText = "Innehåll som kom efter en stund";

    private static readonly TimeSpan SlowData = TimeSpan.FromMilliseconds(800);

    private const string Shell = "<html><head><title>App</title></head><body><main><div id=\"app\">Laddar…</div></main><script src=\"/app.js\"></script></body></html>";

    private readonly ConcurrentQueue<string> _hits = new();
    private readonly FakeRenderer _renderer = new();
    private TestSite _site = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _site = await TestSite.StartAsync(Map);
        _renderer.Render = (request, _) => Task.FromResult(request.Url.AbsolutePath switch
        {
            "/" => FakeRenderer.Page("<html><body><main><p>Innehåll från API</p><a href=\"/c\">C</a></main></body></html>", new Uri(Url("/d"))),
            var path => FakeRenderer.Page($"<html><body><main><p>Sida {path}</p></main></body></html>"),
        });
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_browser_source_stores_the_rendered_dom_with_its_discovered_links_and_fetch_mode_browser()
    {
        await using var rig = CrawlRig.Create(_site, renderer: _renderer, render: RenderTestKit.Options());
        var context = Context(Guid.NewGuid(), FetchMode.Browser);

        var results = await rig.CrawlAsync(context);

        var content = Done(results, "/").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        content.Facts.FetchMode.ShouldBe(FetchModes.Browser);
        content.Raw.MediaType.ShouldBe("text/html");
        var stored = await ReadAsync(rig.Path.Blobs, content.Raw.Key);
        stored.ShouldContain("Innehåll från API");
        stored.ShouldNotContain("Laddar", Case.Sensitive, "the rendered DOM is stored, not the shell");
        (await PageLinks.LoadAsync(rig.Path.Blobs, "public", content.Raw.ContentHash, Ct))!.Links.ShouldBe([Url("/c"), Url("/d")]);
        // /c from the rendered DOM's <a href>, /d discovered by the browser (a download, a popup): both crawled and rendered.
        Done(results, "/c").Outcome.ShouldBeOfType<FetchOutcome.Content>().Facts.FetchMode.ShouldBe(FetchModes.Browser);
        Done(results, "/d").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        // The browser got the shell the worker fetched, its cookie and the host's User-Agent.
        var main = _renderer.Requests.First(r => r.Url.AbsolutePath == "/");
        _renderer.MainDocuments["/"].ShouldContain("Laddar");
        main.SetCookies.ShouldContain(c => c.StartsWith("session=1", StringComparison.Ordinal));
        main.UserAgent.ShouldBe(CrawlerIdentity.DefaultUserAgent);
        main.Limits.ShouldBe(RenderTestKit.Options().Limits());
    }

    [Fact]
    public async Task A_browser_source_sends_no_conditional_get_and_renders_every_page_again()
    {
        await using var rig = CrawlRig.Create(_site, renderer: _renderer, render: RenderTestKit.Options());
        var browser = Guid.NewGuid();
        await rig.CrawlAsync(Context(browser, FetchMode.Browser));
        rig.Store.Row(browser, Url("/"))!.ETag.ShouldBe("\"v1\"");

        var again = await rig.CrawlAsync(Context(browser, FetchMode.Browser));

        _site.Requests["/"].ContainsKey("If-None-Match").ShouldBeFalse("a browser source never sends validators (§8.5)");
        Done(again, "/").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        _renderer.Requests.Count(r => r.Url.AbsolutePath == "/").ShouldBe(2);

        // Negative proof: an HTTP source of the same site sends its stored validators and gets a 304.
        var http = Guid.NewGuid();
        await rig.CrawlAsync(Context(http, FetchMode.Http));
        var conditional = await rig.CrawlAsync(Context(http, FetchMode.Http));
        _site.Requests["/"]["If-None-Match"].ToString().ShouldBe("\"v1\"");
        Done(conditional, "/").Outcome.ShouldBeOfType<FetchOutcome.NotModified>();
    }

    [Fact]
    public async Task A_failed_render_keeps_the_last_good_content_and_the_traversal_continues_from_its_last_good_links()
    {
        await using var rig = CrawlRig.Create(_site, renderer: _renderer, render: RenderTestKit.Options());
        var source = Guid.NewGuid();
        await rig.CrawlAsync(Context(source, FetchMode.Browser));
        var good = rig.Store.Row(source, Url("/"))!.ContentHash;
        var blobs = RawBlobs(rig);
        var shell = _renderer.Render;
        _renderer.Render = (request, fetcher) => request.Url.AbsolutePath == "/" ? Task.FromResult(FakeRenderer.NotReady()) : shell!(request, fetcher);

        var context = await rig.ListAsync(Context(source, FetchMode.Browser));
        var result = await rig.Traversal.ProcessAsync(context, rig.Store.Row(source, Url("/"))!.UrlHash, Ct);

        result.ShouldBe(new CrawlFetchResult.Requeue(RenderReasons.RetryAfter, RenderReasons.Timeout, CountsAttempt: true));
        rig.Store.Row(source, Url("/"))!.ContentHash.ShouldBe(good, "the document keeps its last good content");
        RawBlobs(rig).ShouldBe(blobs, "no fallback: the raw shell is never stored");
        // The links of the last good content are admitted to this run, as a 304's would be (§8.4): nothing below is missed.
        rig.Store.Row(source, Url("/c"))!.RunId.ShouldBe(context.Run.RunId);
        rig.Store.Row(source, Url("/d"))!.RunId.ShouldBe(context.Run.RunId);

        _renderer.Render = (_, _) => throw new RenderUnavailableException();
        (await rig.Traversal.ProcessAsync(context, rig.Store.Row(source, Url("/"))!.UrlHash, Ct))
            .ShouldBe(new CrawlFetchResult.Requeue(RenderReasons.RetryAfter, RenderReasons.Unavailable, CountsAttempt: true));
    }

    [Theory]
    [InlineData(RenderReasons.BadSelector)]
    [InlineData(RenderReasons.SelectorNotFound)]
    public async Task A_ready_selector_failure_fails_the_item_at_once_and_keeps_the_last_good_content_and_links(string reason)
    {
        await using var rig = CrawlRig.Create(_site, renderer: _renderer, render: RenderTestKit.Options());
        var source = Guid.NewGuid();
        await rig.CrawlAsync(Context(source, FetchMode.Browser));
        var good = rig.Store.Row(source, Url("/"))!.ContentHash;
        var blobs = RawBlobs(rig);
        var page = _renderer.Render;
        _renderer.Render = (request, fetcher) => request.Url.AbsolutePath == "/" ? Task.FromResult(FakeRenderer.NotReady(reason)) : page!(request, fetcher);
        var renders = _renderer.Requests.Count;

        var context = await rig.ListAsync(Context(source, FetchMode.Browser));
        var result = await rig.Traversal.ProcessAsync(context, rig.Store.Row(source, Url("/"))!.UrlHash, Ct);

        // Permanent (#488, #490): the item is done with the reason after one render, not requeued as render.timeout is.
        result.ShouldBeOfType<CrawlFetchResult.Done>().Outcome.ShouldBe(new FetchOutcome.Failed(reason));
        _renderer.Requests.Count.ShouldBe(renders + 1);
        result.ShouldBeOfType<CrawlFetchResult.Done>().Frontier.Facts!.ContentHash.ShouldBe(good, "the document keeps its last good content");
        RawBlobs(rig).ShouldBe(blobs, "no fallback: the raw shell is never stored");
        rig.Store.Row(source, Url("/c"))!.RunId.ShouldBe(context.Run.RunId, "the pages below are admitted from the last good links");
        rig.Store.Row(source, Url("/d"))!.RunId.ShouldBe(context.Run.RunId);
    }

    [Fact]
    public async Task The_real_renderer_waits_for_the_quiet_rule_after_the_ready_selector_holds_text()
    {
        // e.umea.se (#490): the app shows a translated h2 label at once and its content after a data request of a few seconds.
        var browser = new FakeBrowser
        {
            Script = async page =>
            {
                page.Show("h2", "Starta en e-tjänst");
                page.Show("#content", await page.FetchAsync(new Uri(Url("/api/slow"))));
            },
        };
        await using var renderer = Renderer(browser);
        // Ready after about 1.3 s (the data, then the quiet period); 15 s so a loaded runner can't make it render.timeout (#645).
        await using var path = _site.Fetch(renderer: renderer, render: RealRenderOptions(pageTimeoutSeconds: 15));

        var outcome = await path.Connector.FetchAsync(UrlListContext(FetchMode.Browser, readySelector: "h2"), Item("/"), Ct);

        // The outcome with its reason in the message, so a failure says which (render.timeout or render.unavailable).
        var content = outcome as FetchOutcome.Content;
        content.ShouldNotBeNull($"the outcome was {outcome}");
        var stored = await ReadAsync(path.Blobs, content.Raw.Key);
        stored.ShouldContain("Starta en e-tjänst");
        stored.ShouldContain(SlowText, Case.Sensitive, "not ready on the label alone: the data request ended and the page was quiet first");
    }

    [Fact]
    public async Task The_real_renderer_reports_a_refused_ready_selector_as_render_bad_selector_without_waiting()
    {
        // The driver's own message for <h1> (packages/isomorphic/cssParser.ts, Playwright 1.65.1).
        var browser = new FakeBrowser { SelectorError = "Unexpected token \"<\" while parsing css selector \"<h1>\". Did you mean to CSS.escape it?\nCall log:\n  - waiting for <h1>" };
        await using var renderer = Renderer(browser);
        await using var path = _site.Fetch(renderer: renderer, render: RealRenderOptions());
        var started = TimeProvider.System.GetTimestamp();

        var outcome = await path.Connector.FetchAsync(UrlListContext(FetchMode.Browser, readySelector: "<h1>"), Item("/"), Ct);

        outcome.ShouldBe(new FetchOutcome.Failed(RenderReasons.BadSelector));
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(RealRenderOptions().PageTimeout, "a refused selector fails at once");
        Directory.Exists(Path.Join(path.BlobRoot.FullName, "raw")).ShouldBeFalse();
    }

    [Fact]
    public async Task The_real_renderer_tells_a_selector_that_never_matched_from_a_page_still_loading()
    {
        var quiet = new FakeBrowser { Script = page => { page.Show("h2", "Starta en e-tjänst"); return Task.CompletedTask; } };
        await using (var renderer = Renderer(quiet))
        await using (var path = _site.Fetch(renderer: renderer, render: RealRenderOptions()))
        {
            (await path.Connector.FetchAsync(UrlListContext(FetchMode.Browser, readySelector: "#intro-description"), Item("/"), Ct))
                .ShouldBe(new FetchOutcome.Failed(RenderReasons.SelectorNotFound));
        }

        // Negative proof: a data request still open at the deadline is the page still loading, transient as before.
        var loading = new FakeBrowser { Script = async page => await page.FetchAsync(new Uri(Url("/api/hang"))) };
        await using (var renderer = Renderer(loading))
        await using (var path = _site.Fetch(renderer: renderer, render: RealRenderOptions()))
        {
            (await path.Connector.FetchAsync(UrlListContext(FetchMode.Browser, readySelector: "#intro-description"), Item("/"), Ct))
                .ShouldBe(new FetchOutcome.Transient(RenderReasons.Timeout, RenderReasons.RetryAfter, CountsAttempt: true));
        }
    }

    [Theory]
    [InlineData("Unexpected token \"<\" while parsing css selector \"<h1>\". Did you mean to CSS.escape it?", true)]
    [InlineData("Unexpected token \"\" while parsing css selector \"div[\". Did you mean to CSS.escape it?", true)]
    [InlineData("Unsupported token \";\" while parsing css selector \"#intro-description;\". Did you mean to CSS.escape it?", true)]
    [InlineData("Unknown engine \"xpath2\" while parsing selector xpath2=//h1", true)]
    [InlineData("Malformed selector: internal:has=x", true)]
    [InlineData("SyntaxError: Failed to execute 'querySelectorAll' on 'Document': 'h1:foo' is not a valid selector.", true)]
    [InlineData("Execution context was destroyed, most likely because of a navigation", false)]
    [InlineData("Navigation interrupted by another navigation", false)]
    [InlineData("Target page, context or browser has been closed", false)]
    public void A_bad_selector_is_told_from_a_navigation_the_page_interrupted_by_playwrights_message(string message, bool bad)
    {
        PlaywrightRenderer.IsBadSelector(new Microsoft.Playwright.PlaywrightException(message)).ShouldBe(bad);
    }

    [Fact]
    public async Task A_job_takes_a_render_slot_only_when_one_is_free_and_is_released_without_an_attempt_otherwise()
    {
        await using var rig = CrawlRig.Create(_site, renderer: _renderer, render: RenderTestKit.Options());
        var source = Guid.NewGuid();
        var context = await rig.ListAsync(Context(source, FetchMode.Browser));
        var row = rig.Store.Row(source, Url("/"))!;
        // The host's one render slot (renderPerHost 1) is held by another page.
        var held = rig.Path.Renders.TryAcquire(row.Host).ShouldBeOfType<RenderSlot.Granted>();
        _hits.Clear();

        var busy = await rig.Traversal.ProcessAsync(context, row.UrlHash, Ct);

        busy.ShouldBe(new CrawlFetchResult.Requeue(RenderReasons.BusyDelay, RenderReasons.Busy, CountsAttempt: false));
        _hits.ShouldNotContain("GET /", "no request and no host slot spent without a render slot");
        _renderer.Requests.ShouldBeEmpty();

        held.Lease.Dispose();
        (await rig.Traversal.ProcessAsync(context, row.UrlHash, Ct)).ShouldBeOfType<CrawlFetchResult.Done>().Outcome.ShouldBeOfType<FetchOutcome.Content>();
        rig.Path.Renders.InUse.ShouldBe(0, "the slot is released after the render");
    }

    [Fact]
    public async Task Without_a_renderer_endpoint_a_browser_source_waits_as_render_not_configured_without_an_attempt()
    {
        await using var rig = CrawlRig.Create(_site, renderer: _renderer);
        var source = Guid.NewGuid();
        var context = await rig.ListAsync(Context(source, FetchMode.Browser));

        var result = await rig.Traversal.ProcessAsync(context, rig.Store.Row(source, Url("/"))!.UrlHash, Ct);

        result.ShouldBe(new CrawlFetchResult.Requeue(RenderReasons.RetryAfter, RenderReasons.NotConfigured, CountsAttempt: false));
        _hits.ShouldNotContain("GET /");
    }

    [Fact]
    public async Task Render_slots_cap_the_worker_and_each_host()
    {
        var policy = new FixedCrawlPolicy();
        policy.Overrides["b.test"] = new HostPoliteness { RenderPerHost = 2, MaxConnections = 2 };
        var slots = new RenderSlots(Microsoft.Extensions.Options.Options.Create(RenderTestKit.Options(maxConcurrent: 2)), policy, new RenderMetrics(ConnectorTestKit.Meters));

        var a = slots.TryAcquire("a.test").ShouldBeOfType<RenderSlot.Granted>();
        slots.TryAcquire("a.test").ShouldBe(new RenderSlot.Refused(RenderReasons.Busy, RenderReasons.BusyDelay), "renderPerHost defaults to 1");
        var b = slots.TryAcquire("b.test").ShouldBeOfType<RenderSlot.Granted>();
        slots.TryAcquire("b.test").ShouldBe(new RenderSlot.Refused(RenderReasons.Busy, RenderReasons.BusyDelay), "Render:MaxConcurrent is 2");
        a.Lease.Dispose();
        a.Lease.Dispose();
        slots.TryAcquire("b.test").ShouldBeOfType<RenderSlot.Granted>("b.test may render two pages at once");
        slots.InUse.ShouldBe(2);
        b.Lease.Dispose();
        slots.InUse.ShouldBe(1);
    }

    [Fact]
    public async Task A_navigation_before_ready_is_a_redirect_hop_the_worker_fetches_and_renders()
    {
        await using var path = _site.Fetch(renderer: _renderer, render: RenderTestKit.Options());
        var shell = _renderer.Render!;
        _renderer.Render = (request, fetcher) => request.Url.AbsolutePath == "/nav"
            ? Task.FromResult(FakeRenderer.Navigated(new Uri(Url("/b"))))
            : shell(request, fetcher);

        var outcome = await path.Connector.FetchAsync(UrlListContext(FetchMode.Browser), Item("/nav"), Ct);

        var content = outcome.ShouldBeOfType<FetchOutcome.Content>();
        content.Facts.CanonicalIdentity.ShouldBe(Url("/b"));
        content.Facts.Redirects.ShouldBe([new Uri(Url("/nav"))]);
        (await ReadAsync(path.Blobs, content.Raw.Key)).ShouldContain("Sida /b");
        _renderer.Requests.Select(r => r.Url.AbsolutePath).ShouldBe(["/nav", "/b"]);
    }

    [Fact]
    public async Task A_page_that_is_not_ready_is_a_render_timeout_and_its_shell_is_never_stored()
    {
        await using var path = _site.Fetch(renderer: _renderer, render: RenderTestKit.Options());
        _renderer.Render = (_, _) => Task.FromResult(FakeRenderer.NotReady());

        var outcome = await path.Connector.FetchAsync(UrlListContext(FetchMode.Browser), Item("/"), Ct);

        outcome.ShouldBe(new FetchOutcome.Transient(RenderReasons.Timeout, RenderReasons.RetryAfter, CountsAttempt: true));
        Directory.Exists(Path.Join(path.BlobRoot.FullName, "raw")).ShouldBeFalse();
        // Negative proof: the same page from an HTTP source is the shell, stored.
        (await path.Connector.FetchAsync(UrlListContext(FetchMode.Http), Item("/"), Ct)).ShouldBeOfType<FetchOutcome.Content>().Facts.FetchMode.ShouldBe(FetchModes.Http);
    }

    [Fact]
    public async Task The_render_fetcher_blocks_assets_and_trackers_applies_the_method_policy_and_caches_scripts()
    {
        await using var path = _site.Fetch(dns: _site.Resolver().Add("other.test", "127.0.0.1"), render: RenderTestKit.Options());
        var page = Page(path);

        (await page.FetchAsync(Get("/bild.png", "image"), Ct)).ShouldBe(RenderResponse.NoContent(RenderResponseKind.Blocked));
        (await page.FetchAsync(new RenderSubrequest("GET", new Uri("https://www.googletagmanager.com/gtm.js"), [], null, "script"), Ct)).Kind
            .ShouldBe(RenderResponseKind.Blocked);
        (await page.FetchAsync(new RenderSubrequest("PUT", new Uri(Url("/api/echo")), [], [1], "fetch"), Ct)).ShouldBe(RenderResponse.NoContent(RenderResponseKind.Refused));
        (await page.FetchAsync(new RenderSubrequest("POST", new Uri($"http://other.test:{_site.Port}/api/echo"), [], [1], "fetch"), Ct)).Kind
            .ShouldBe(RenderResponseKind.Refused, "POST only to the page's own host");
        var post = await page.FetchAsync(new RenderSubrequest("POST", new Uri(Url("/api/echo")), [KeyValuePair.Create("content-type", "text/plain")], "hej"u8.ToArray(), "fetch"), Ct);
        (post.Kind, post.Status, Encoding.UTF8.GetString(post.Body)).ShouldBe((RenderResponseKind.Fetched, 200, "POST hej"));

        var script = await page.FetchAsync(Get("/app.js", "script"), Ct);
        var again = await Page(path).FetchAsync(Get("/app.js", "script"), Ct);
        (script.Kind, again.Kind).ShouldBe((RenderResponseKind.Fetched, RenderResponseKind.Cached));
        again.Body.ShouldBe(script.Body);
        _hits.Count(h => h == "GET /app.js").ShouldBe(1, "the second page of the run takes the script from the cache");
        _hits.ShouldNotContain(h => h.Contains("bild.png", StringComparison.Ordinal) || h.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_render_fetcher_refuses_a_private_address_with_purpose_render()
    {
        var blocked = new ConcurrentQueue<(string Reason, string Purpose)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "rag.egress.blocked")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(t => t.Key, t => t.Value as string);
            blocked.Enqueue((values["reason"]!, values["purpose"]!));
        });
        listener.Start();
        await using var path = _site.Fetch(dns: _site.Resolver().Add("intern.test", "10.215.0.9"), render: RenderTestKit.Options());
        var page = Page(path);

        var literal = await page.FetchAsync(new RenderSubrequest("GET", new Uri("http://10.215.0.9/admin"), [], null, "fetch"), Ct);
        var named = await page.FetchAsync(new RenderSubrequest("GET", new Uri($"http://intern.test:{_site.Port}/admin"), [], null, "fetch"), Ct);

        literal.ShouldBe(RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient"));
        named.ShouldBe(RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient"));
        // The literal address is refused by the URL check, the name by its resolved address: robots.txt is asked first, so its fetch is the one refused there.
        blocked.ShouldContain((Worker.Connectors.Egress.EgressGuard.PrivateAddress, "render"));
        blocked.ShouldContain((Worker.Connectors.Egress.EgressGuard.PrivateAddress, "robots"));
        // Negative proof: robots.txt also refuses, without the guard.
        (await page.FetchAsync(Get("/api/hemligt", "fetch"), Ct)).ShouldBe(RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient"));
        _hits.ShouldNotContain("GET /api/hemligt");
    }

    [Fact]
    public async Task The_render_fetcher_forwards_cookies_within_the_page_follows_redirects_and_stops_at_the_request_limit()
    {
        await using var path = _site.Fetch(render: RenderTestKit.Options());
        var page = Page(path, maxRequests: 4);

        (await page.FetchAsync(Get("/api/data", "fetch"), Ct)).Status.ShouldBe(401);
        var withCookie = await page.FetchAsync(Get("/api/data", "fetch", ("cookie", "session=1")), Ct);
        (withCookie.Status, Encoding.UTF8.GetString(withCookie.Body)).ShouldBe((200, "{\"text\":\"Innehåll från API\"}"));
        var redirected = await page.FetchAsync(Get("/old.js", "script"), Ct);
        (redirected.Status, Encoding.UTF8.GetString(redirected.Body)).ShouldBe((200, "console.log('v2');"));
        redirected.Headers.ShouldNotContainKey("content-length");
        var login = await page.FetchAsync(Get("/login", "fetch"), Ct);
        login.Headers["set-cookie"].ShouldStartWith("session=1");

        (await page.FetchAsync(Get("/api/data", "fetch"), Ct)).ShouldBe(RenderResponse.NoContent(RenderResponseKind.Truncated));
    }

    [Fact]
    public void Rendered_html_drops_scripts_styles_templates_iframes_and_inline_handlers()
    {
        var html = RenderedHtml.Clean("""
            <html><head><style>p{}</style><script>var a;</script></head><body onload="x()"><main><p onclick="y()">Text</p>
            <noscript>NOSCRIPT-TEXT</noscript><template>TEMPLATE-TEXT</template><iframe src="/x"></iframe></main></body></html>
            """);

        html.ShouldStartWith("<!DOCTYPE html><html>");
        html.ShouldContain("<p>Text</p>");
        foreach (var gone in new[] { "<style", "<script", "NOSCRIPT-TEXT", "TEMPLATE-TEXT", "<iframe", "onclick", "onload" })
        {
            html.ShouldNotContain(gone);
        }
    }

    /// <summary>The real renderer on <paramref name="browser"/>.</summary>
    private static PlaywrightRenderer Renderer(FakeBrowser browser) =>
        new(Microsoft.Extensions.Options.Options.Create(RealRenderOptions()), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaywrightRenderer>.Instance)
        {
            CreatePlaywright = browser.CreateAsync,
        };

    /// <summary>
    /// A short page time for the real renderer's tests (the ones that wait out the deadline), the quiet period as configured
    /// by default. A test whose page becomes ready passes a longer one: it does not wait for the deadline, and a loaded runner
    /// must not turn it into <c>render.timeout</c> (#645).
    /// </summary>
    private static RenderOptions RealRenderOptions(int pageTimeoutSeconds = 3)
    {
        var options = RenderTestKit.Options();
        options.PageTimeout = TimeSpan.FromSeconds(pageTimeoutSeconds);
        return options;
    }

    private RenderFetcher Page(FetchPath path, int maxRequests = 50) =>
        path.RenderFetchers.For(new RenderPage(new Uri(Url("/")), "public", Guid.Parse("0196f4a2-0000-7000-8000-000000000001"), path.Robots.CheckAsync,
            new RenderLimits(maxRequests, 1024 * 1024, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100))));

    private RenderSubrequest Get(string pathAndQuery, string resourceType, params (string Name, string Value)[] headers) =>
        new("GET", new Uri(Url(pathAndQuery)), [.. headers.Select(h => KeyValuePair.Create(h.Name, h.Value))], null, resourceType);

    private CrawlRunContext Context(Guid sourceId, FetchMode mode) =>
        new(new CrawlRunKey(sourceId, "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual,
            ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/")], MaxPages = 100, FetchMode = mode }), Revalidate: false);

    private FetchContext UrlListContext(FetchMode mode, string? readySelector = null) =>
        new(Guid.NewGuid(), "public", Guid.NewGuid(), ContractJson.ToElement(new UrlListSettings { Urls = [Url("/")], FetchMode = mode, ReadySelector = readySelector }),
            Revalidate: false);

    private WorkItem Item(string pathAndQuery) => new(IdentityKind.Url, Url(pathAndQuery), new Uri(Url(pathAndQuery)), 0, WorkHints.None);

    private string Url(string pathAndQuery) => UrlCanonicalizer.Canonicalize(_site.Url(pathAndQuery).AbsoluteUri).Value!;

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).AbsolutePath == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    private static string[] RawBlobs(CrawlRig rig) =>
        [.. Directory.EnumerateFiles(Path.Join(rig.Path.BlobRoot.FullName, "raw"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private static async Task<string> ReadAsync(Infrastructure.Blobs.FileSystemBlobStore blobs, BlobKey key)
    {
        await using var stream = await blobs.OpenReadAsync(key, null, Ct);
        using var reader = new StreamReader(stream!);
        return await reader.ReadToEndAsync(Ct);
    }

    private void Map(WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            _hits.Enqueue($"{http.Request.Method} {http.Request.Path}");
            await next();
        });
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /api/hemligt\n"));
        app.MapGet("/", (HttpContext http) =>
        {
            http.Response.Headers.ETag = "\"v1\"";
            if (http.Request.Headers.IfNoneMatch == "\"v1\"")
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            http.Response.Headers.SetCookie = "session=1; Path=/";
            return Results.Content(Shell, "text/html; charset=utf-8");
        });
        app.MapGet("/app.js", () => Results.Text("console.log('app');", "text/javascript"));
        app.MapGet("/old.js", () => Results.Redirect("/v2/app.js", permanent: true));
        app.MapGet("/v2/app.js", () => Results.Text("console.log('v2');", "text/javascript"));
        app.MapGet("/login", (HttpContext http) =>
        {
            http.Response.Headers.SetCookie = "session=1; Path=/";
            return Results.Text("ok");
        });
        app.MapGet("/api/data", (HttpContext http) => http.Request.Cookies["session"] == "1"
            ? Results.Text("{\"text\":\"Innehåll från API\"}", "application/json")
            : Results.StatusCode(StatusCodes.Status401Unauthorized));
        app.MapGet("/api/slow", async (HttpContext http) =>
        {
            await Task.Delay(SlowData, http.RequestAborted);
            return Results.Text(SlowText, "text/plain; charset=utf-8");
        });
        app.MapGet("/api/hang", async (HttpContext http) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), http.RequestAborted);
            return Results.Text("too late");
        });
        app.MapPost("/api/echo", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            return Results.Text("POST " + await reader.ReadToEndAsync());
        });
        foreach (var page in new[] { "/b", "/c", "/d", "/nav" })
        {
            app.MapGet(page, () => Results.Content(Shell, "text/html; charset=utf-8"));
        }

        // A real 404, so the HTTP source's soft-404 probe takes no fingerprint from the shell.
        app.MapFallback(() => Results.NotFound());
    }
}
