using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.Pipelines.Crawl;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// A crawl of a generated site end to end on in-memory stores (connectors design §5, §6.0, §18 "Integration"): seeds from
/// robots.txt and a gzipped sitemap index, scope, robots, traps, redirects out of scope, canonical folding,
/// <c>noindex</c>/<c>nofollow</c> on an external versus a municipal host, the page limit, the top-up batch and an
/// unreachable robots.txt. The database-backed form of these tests waits for the schema (J2).
/// </summary>
public sealed class CrawlTraversalTests : IAsyncLifetime
{
    private const string AliasHtml = "<html><head><link rel=\"canonical\" href=\"/a\"></head><body><main><p>Sida A</p><a href=\"/d\">D</a></main></body></html>";

    private TestSite _site = null!;
    private int _robotsStatus = 200;

    public async ValueTask InitializeAsync() => _site = await TestSite.StartAsync(Map);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Crawls_an_external_site_within_scope_robots_and_traps()
    {
        await using var rig = CrawlRig.Create(_site);
        var context = Context(Settings());

        var results = await rig.CrawlAsync(context);
        var urls = rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).ToList();

        // Seeds: the start URL and the in-scope sitemap entries; the image and the off-site entry are dropped.
        rig.Store.Rows(context.Run.SourceId).Where(r => r.Depth == 0).Select(r => r.Url)
            .ShouldBe([Url("/"), Url("/a"), Url("/b")], ignoreOrder: true);
        urls.ShouldNotContain(Url("/bilder/karta.jpg"));
        // Negative proofs: an off-site link and a calendar trap are never admitted.
        urls.ShouldNotContain(u => u.Contains("other.test", StringComparison.Ordinal));
        urls.ShouldNotContain(Url("/kalender/2010"));
        (await rig.Store.GetAsync(context.Run.RunId, Ct))!.TrapStats.ShouldContainKey(TrapDetector.Calendar);

        Done(results, "/a").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        Done(results, "/privat/x").Outcome.ShouldBe(new FetchOutcome.Skipped("robots.disallow"));
        Done(results, "/redirect-out").Outcome.ShouldBe(new FetchOutcome.Skipped("redirect.out_of_scope"));
        Done(results, "/moved").Outcome.ShouldBe(new FetchOutcome.Gone("http.moved_out_of_scope"));
        Done(results, "/alias").Fold.ShouldBe(new CanonicalFold(CanonicalFoldKind.Folded, Url("/a")));
        Done(results, "/alias").Facts.ShouldContain(CrawlTraversal.Folded);
        Done(results, "/c").Fold.ShouldBe(new CanonicalFold(CanonicalFoldKind.Unverified, Url("/")));

        // External host: noindex skips the page but its links are followed; nofollow takes no links.
        Done(results, "/noindex").Outcome.ShouldBe(new FetchOutcome.Skipped(CrawlTraversal.NoIndex));
        urls.ShouldContain(Url("/from-noindex"));
        urls.ShouldNotContain(Url("/hidden"));

        var verdict = await rig.Traversal.EvaluateAsync(context, cancelled: false, Ct);
        verdict.Reasons.ShouldBeEmpty();
        verdict.Complete.ShouldBeTrue();
        rig.Store.Rows(context.Run.SourceId).ShouldAllBe(r => r.State == FrontierState.Idle);
    }

    [Fact]
    public async Task A_municipal_host_ignores_noindex_and_nofollow_and_records_them()
    {
        var policy = new FixedCrawlPolicy(TestSite.Host);
        policy.Overrides[TestSite.Host] = FixedCrawlPolicy.Budget(1000);
        await using var rig = CrawlRig.Create(_site, policy);
        var context = Context(Settings());

        var results = await rig.CrawlAsync(context);

        var noindex = Done(results, "/noindex");
        noindex.Outcome.ShouldBeOfType<FetchOutcome.Content>();
        noindex.Facts.ShouldContain(CrawlTraversal.NoIndexIgnored);
        Done(results, "/nofollow").Facts.ShouldContain(CrawlTraversal.NoFollowIgnored);
        rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).ShouldContain(Url("/hidden"));
    }

    [Fact]
    public async Task C7_a_crawl_that_hits_max_pages_is_incomplete_and_proposes_no_not_seen_deletion()
    {
        await using var rig = CrawlRig.Create(_site);
        var context = Context(Settings(maxPages: 4));

        await rig.CrawlAsync(context);
        var record = (await rig.Store.GetAsync(context.Run.RunId, Ct))!;
        var verdict = await rig.Traversal.EvaluateAsync(context, cancelled: false, Ct);

        record.Admitted.ShouldBe(4);
        record.LimitHit.ShouldBeTrue();
        rig.Store.Rows(context.Run.SourceId).Count.ShouldBe(4);
        verdict.Complete.ShouldBeFalse();
        verdict.RunDegradation.ShouldBe(TraversalCompleteness.LimitPages);
        // Every earlier document the limited run did not reach stays.
        var earlier = Enumerable.Range(0, 20).Select(i => new SourceDocument
        {
            Id = Guid.NewGuid(),
            CanonicalIdentity = Url($"/old{i}"),
            LastRunId = Guid.NewGuid(),
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(-7),
        }).ToList();
        DeletionCandidates.FromTraversal(context.Run.RunId, earlier, verdict, new Dictionary<Guid, string>(), new HashSet<Guid>(), DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task Max_depth_zero_fetches_the_seeds_only()
    {
        await using var rig = CrawlRig.Create(_site);
        var context = Context(Settings(maxDepth: 0));

        await rig.CrawlAsync(context);

        rig.Store.Rows(context.Run.SourceId).ShouldAllBe(r => r.Depth == 0);
        rig.Store.Rows(context.Run.SourceId).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Each_host_gets_at_most_the_batch_of_fetch_jobs_and_is_topped_up_as_they_finish()
    {
        await using var rig = CrawlRig.Create(_site, batch: 2);
        var context = Context(Settings());
        await rig.Traversal.StartAsync(context, Ct);
        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(Listing(context), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        await rig.Traversal.AdmitSeedsAsync(context, events, Ct);
        (await rig.TopUp.TopUpAsync(context, null, null, Ct)).Enqueued.ShouldBe(2);
        // Negative proof: a second top-up while both are open releases nothing.
        (await rig.TopUp.TopUpAsync(context, null, null, Ct)).Enqueued.ShouldBe(0);
        var jobs = rig.Queue.Jobs;
        jobs.ShouldAllBe(j => j.Class == "fetch" && j.RunId == context.Run.RunId && j.DedupKey!.StartsWith($"fetch:{context.Run.SourceId}:", StringComparison.Ordinal));
        (await rig.Store.HostLoadAsync(context.Run, Ct)).Single().ShouldBe(new HostLoad($"{TestSite.Host}:{_site.Port}", 1, 2));

        // Draining tops up as fetches finish, never holding more than the batch released at once.
        await rig.DrainAsync(context);
        (await rig.Traversal.EvaluateAsync(context, false, Ct)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_scheduled_run_outside_the_night_window_releases_nothing_and_waits_for_it()
    {
        await using var rig = CrawlRig.Create(_site);
        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
        // A one-minute window an hour from now (Stockholm time) is closed now.
        var window = $"{local.AddHours(1):HH:mm}-{local.AddHours(1).AddMinutes(1):HH:mm}";
        var context = Context(Settings(window: window)) with { Trigger = RunTrigger.Schedule };
        await rig.Traversal.StartAsync(context, Ct);
        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(Listing(context), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        await rig.Traversal.AdmitSeedsAsync(context, events, Ct);
        var result = await rig.TopUp.TopUpAsync(context, null, null, Ct);

        result.Enqueued.ShouldBe(0);
        result.WindowOpensAt.ShouldNotBeNull();
        result.WindowOpensAt.Value.ShouldBeGreaterThan(now);
        var continuation = rig.TopUp.Continuation(context, result.WindowOpensAt.Value);
        continuation.Class.ShouldBe("sync");
        continuation.DedupKey.ShouldBe(JobDedupKeys.Sync(context.Run.SourceId));
        continuation.Delay.ShouldBeGreaterThan(TimeSpan.FromMinutes(50));
        ContractJson.Deserialize<SyncPayload>(System.Text.Json.JsonDocument.Parse(continuation.Payload).RootElement).Phase.ShouldBe(SyncPhase.Fetch);
    }

    [Fact]
    public async Task A_host_whose_profile_says_crawl_window_any_is_released_outside_the_night_window()
    {
        var policy = new FixedCrawlPolicy();
        policy.Overrides[TestSite.Host] = FixedCrawlPolicy.Budget(500) with { CrawlWindow = "any" };
        await using var rig = CrawlRig.Create(_site, policy);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
        var window = $"{local.AddHours(1):HH:mm}-{local.AddHours(1).AddMinutes(1):HH:mm}";
        var context = Context(Settings(window: window)) with { Trigger = RunTrigger.Schedule };
        await rig.Traversal.StartAsync(context, Ct);
        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(Listing(context), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        await rig.Traversal.AdmitSeedsAsync(context, events, Ct);
        var result = await rig.TopUp.TopUpAsync(context, null, null, Ct);

        result.Enqueued.ShouldBe(3);
        result.WindowOpensAt.ShouldBeNull();
        var results = await rig.DrainAsync(context);
        Done(results, "/b").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        (await rig.Traversal.EvaluateAsync(context, false, Ct)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_whose_profile_ignores_robots_txt_is_fetched_where_robots_disallows_and_its_sitemap_lines_are_still_read()
    {
        var policy = new FixedCrawlPolicy();
        policy.Overrides[TestSite.Host] = FixedCrawlPolicy.Budget(500) with { Robots = new HostRobots { Mode = "ignore", Reason = "own site, web team consent" } };
        await using var rig = CrawlRig.Create(_site, policy);
        var context = Context(Settings());

        var results = await rig.CrawlAsync(context);

        Done(results, "/privat/x").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        // The seeds from robots.txt's Sitemap: line.
        rig.Store.Rows(context.Run.SourceId).Where(r => r.Depth == 0).Select(r => r.Url).ShouldBe([Url("/"), Url("/a"), Url("/b")], ignoreOrder: true);
        // A redirect hop is not asked either; with robots.txt respected the same hop is refused.
        var hop = new HttpFetchRequest(_site.Url("/to-privat"), EgressPurpose.Fetch, null, null, CanonicalizerOptions.Default, rig.Path.Robots.CheckAsync);
        var ok = (await rig.Path.Fetcher.FetchAsync(hop, Ct)).ShouldBeOfType<HttpFetchResult.Ok>();
        await ok.Body.DisposeAsync();
        ok.FinalUrl.ShouldBe(_site.Url("/privat/x"));
        await using var respecting = CrawlRig.Create(_site);
        (await respecting.Path.Fetcher.FetchAsync(hop with { Robots = respecting.Path.Robots.CheckAsync }, Ct))
            .ShouldBeOfType<HttpFetchResult.Refused>().Reason.ShouldBe("robots.disallow");
    }

    [Fact]
    public async Task An_unreachable_robots_txt_pauses_nothing_on_a_host_whose_profile_ignores_it()
    {
        _robotsStatus = 503;
        var policy = new FixedCrawlPolicy();
        policy.Overrides[TestSite.Host] = FixedCrawlPolicy.Budget(500) with { Robots = new HostRobots { Mode = "ignore", Reason = "own site" } };
        await using var rig = CrawlRig.Create(_site, policy);
        var context = Context(Settings());

        var results = await rig.CrawlAsync(context);

        Done(results, "/").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        Done(results, "/privat/x").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        (await rig.Store.GetAsync(context.Run.RunId, Ct))!.PausedHosts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_host_profile_s_user_agent_is_sent_on_every_request_and_robots_groups_still_apply()
    {
        const string browser = "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0";
        var policy = new FixedCrawlPolicy();
        policy.Overrides[TestSite.Host] = FixedCrawlPolicy.Budget(500) with { UserAgent = browser };
        await using var rig = CrawlRig.Create(_site, policy);
        var context = Context(Settings());

        var results = await rig.CrawlAsync(context);

        foreach (var path in new[] { "/robots.txt", "/sitemapindex.xml", "/sitemap1.xml.gz", "/", "/a" })
        {
            _site.Requests[path]["User-Agent"].ToString().ShouldBe(browser, path);
        }

        // robots.txt is still honoured with another User-Agent.
        Done(results, "/privat/x").Outcome.ShouldBe(new FetchOutcome.Skipped("robots.disallow"));
    }

    [Fact]
    public async Task An_unreachable_robots_txt_pauses_the_host_and_fails_its_seeds()
    {
        _robotsStatus = 503;
        await using var rig = CrawlRig.Create(_site);
        var context = Context(Settings());

        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await rig.Traversal.StartAsync(context, Ct);
        await foreach (var e in rig.Connector.ListAsync(Listing(context), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        events.OfType<ListingEvent<CrawlCheckpoint>.Failure>().Select(f => f.Reason).ShouldContain(CrawlConnector.RobotsUnreachable);
        events[^1].ShouldBe(new ListingEvent<CrawlCheckpoint>.Done(Completeness.Partial(CrawlConnector.RobotsUnreachable)));
        await rig.Traversal.AdmitSeedsAsync(context, events, Ct);
        await rig.TopUp.TopUpAsync(context, null, null, Ct);
        var results = await rig.DrainAsync(context);

        results[Url("/")].ShouldBe(new CrawlFetchResult.NotReached(CrawlConnector.RobotsUnreachable));
        var verdict = await rig.Traversal.EvaluateAsync(context, false, Ct);
        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldContain(TraversalCompleteness.HostPaused);
        verdict.RunFailure.ShouldBe(TraversalCompleteness.SeedsUnreachable);
    }

    [Theory]
    // #801: a sitemap given in the source's settings, as compressed sitemaps are served. umea.se (2026-11-17) serves its
    // sitemap1.xml.gz as application/x-gzip without Content-Encoding; some servers add Content-Encoding: gzip on top.
    [InlineData("gzip")]
    [InlineData("x-gzip")]
    [InlineData("octet")]
    [InlineData("encoded")]
    public async Task A_declared_gzip_sitemap_is_read_whatever_its_content_type_and_encoding(string variant)
    {
        await using var rig = CrawlRig.Create(_site);
        var settings = ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/")], Sitemaps = [Url($"/declared/{variant}.xml.gz")], MaxPages = 100 });
        var context = Context(settings);

        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await rig.Traversal.StartAsync(context, Ct);
        await foreach (var e in rig.Connector.ListAsync(Listing(context), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        events.OfType<ListingEvent<CrawlCheckpoint>.Item>().Select(i => i.Work.CanonicalIdentity).ShouldContain(Url($"/declared-page-{variant}"));
        events.OfType<ListingEvent<CrawlCheckpoint>.Failure>().Where(f => f.Identity.Contains("/declared/", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).PathAndQuery == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    private static ListingContext Listing(CrawlRunContext context) =>
        new(context.Run.SourceId, context.Run.Classification, context.Run.RunId, context.Settings);

    private static CrawlRunContext Context(System.Text.Json.JsonElement settings) =>
        new(new CrawlRunKey(Guid.NewGuid(), "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual, settings, Revalidate: false);

    private string Url(string path) => _site.Url(path).AbsoluteUri;

    private System.Text.Json.JsonElement Settings(int maxPages = 100, int maxDepth = 10, string window = "22:00-06:00") =>
        ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/")], MaxPages = maxPages, MaxDepth = maxDepth, CrawlWindow = window });

    private void Map(WebApplication app)
    {
        static IResult Html(string body, string head = "") =>
            Results.Content($"<html><head>{head}</head><body><main>{body}</main></body></html>", "text/html");

        app.MapGet("/robots.txt", (HttpContext http) => _robotsStatus == 200
            ? Results.Text($"User-agent: *\nDisallow: /privat\nSitemap: http://{http.Request.Host}/sitemapindex.xml\n")
            : Results.StatusCode(_robotsStatus));
        app.MapGet("/sitemapindex.xml", (HttpContext http) => Results.Content(
            $"<?xml version=\"1.0\"?><sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>http://{http.Request.Host}/sitemap1.xml.gz</loc></sitemap></sitemapindex>",
            "application/xml"));
        app.MapGet("/sitemap1.xml.gz", (HttpContext http) =>
        {
            var xml = $"<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
                + $"<url><loc>http://{http.Request.Host}/a</loc><lastmod>2026-01-15</lastmod></url>"
                + $"<url><loc>http://{http.Request.Host}/b</loc></url>"
                + $"<url><loc>http://{http.Request.Host}/bilder/karta.jpg</loc></url>"
                + $"<url><loc>http://other.test:{http.Request.Host.Port}/x</loc></url></urlset>";
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(xml));
            }

            return Results.Bytes(buffer.ToArray(), "application/gzip");
        });
        static byte[] Gzip(byte[] bytes)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(bytes);
            }

            return buffer.ToArray();
        }

        app.MapGet("/declared/{variant}.xml.gz", (string variant, HttpContext http) =>
        {
            var xml = $"<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>http://{http.Request.Host}/declared-page-{variant}</loc></url></urlset>";
            var body = Gzip(Encoding.UTF8.GetBytes(xml));
            if (variant == "encoded")
            {
                // A gzip file sent with Content-Encoding: gzip as well: the client takes off one layer, the parser the other.
                http.Response.Headers.ContentEncoding = "gzip";
                return Results.Bytes(Gzip(body), "application/x-gzip");
            }

            return Results.Bytes(body, variant switch { "gzip" => "application/gzip", "x-gzip" => "application/x-gzip", _ => "application/octet-stream" });
        });
        app.MapGet("/", () => Html(
            "<p>Start</p><a href=\"/a\">A</a><a href=\"/c\">C</a><a href=\"/privat/x\">P</a><a href=\"/kalender/2010\">K</a>"
            + "<a href=\"http://other.test/y\">O</a><a href=\"/redirect-out\">R</a><a href=\"/moved\">M</a><a href=\"/alias\">Alias</a>"
            + "<a href=\"/noindex\">N</a><a href=\"/nofollow\">F</a>"));
        app.MapGet("/a", () => Results.Content(AliasHtml, "text/html"));
        app.MapGet("/alias", () => Results.Content(AliasHtml, "text/html"));
        app.MapGet("/b", () => Html("<p>B</p>"));
        app.MapGet("/c", () => Html("<p>C</p>", "<link rel=\"canonical\" href=\"/\">"));
        app.MapGet("/d", () => Html("<p>D</p>"));
        app.MapGet("/privat/x", () => Html("<p>Privat</p>"));
        app.MapGet("/to-privat", () => Results.Redirect("/privat/x"));
        app.MapGet("/redirect-out", (HttpContext http) => Results.Redirect($"http://other.test:{http.Request.Host.Port}/login"));
        app.MapGet("/moved", (HttpContext http) => Results.Redirect($"http://other.test:{http.Request.Host.Port}/new", permanent: true));
        app.MapGet("/noindex", () => Html("<p>Noindex</p><a href=\"/from-noindex\">x</a>", "<meta name=\"robots\" content=\"noindex\">"));
        app.MapGet("/from-noindex", () => Html("<p>Från noindex</p>"));
        app.MapGet("/nofollow", () => Html("<p>Nofollow</p><a href=\"/hidden\">x</a>", "<meta name=\"robots\" content=\"nofollow\">"));
        app.MapGet("/hidden", () => Html("<p>Dold</p>"));
    }
}
