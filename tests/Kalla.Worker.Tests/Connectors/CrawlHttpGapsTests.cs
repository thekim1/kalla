using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Http;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// The HTTP crawler gaps from the Crawlee review (A-15; connectors design §5.1, §5.3) on in-memory stores against a fixture
/// site: the soft-404 fingerprint per host and run, a challenge page keeping the last good content and pausing its host,
/// sitemap retries, plain-text sitemaps and <c>/sitemap_index.xml</c>. Every rule has a negative proof.
/// </summary>
public sealed class CrawlHttpGapsTests : IAsyncLifetime
{
    private const string NotFoundPage = "<html><head><title>Sidan hittades inte</title></head><body><main><h1>Sidan hittades inte</h1><p>Sök på webbplatsen.</p></main></body></html>";

    private readonly ConcurrentQueue<string> _hits = new();
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private bool _aMissing;
    private bool _aChallenged;
    private bool _softNotFound = true;
    private int _sitemapFailures;
    private string? _robotsSitemap;
    private bool _sitemapIndex;
    private TestSite _site = null!;

    public async ValueTask InitializeAsync() => _site = await TestSite.StartAsync(Map);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_not_found_page_served_with_200_fails_as_soft_404_and_never_replaces_good_content()
    {
        await using var rig = CrawlRig.Create(_site);
        var source = Guid.NewGuid();
        var first = Context(source);
        var firstResults = await rig.CrawlAsync(first);
        var good = Done(firstResults, "/a").Outcome.ShouldBeOfType<FetchOutcome.Content>().Raw.ContentHash;
        Done(firstResults, "/b").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        // One probe per host and run, through the normal fetch path.
        _hits.Count(h => h.EndsWith(SoftNotFound.ProbeSuffix, StringComparison.Ordinal)).ShouldBe(1);

        // /a now answers with the host's "page not found" page, with a 200.
        _aMissing = true;
        var second = Context(source);
        var results = await rig.CrawlAsync(second);

        Done(results, "/a").Outcome.ShouldBe(new FetchOutcome.Failed(SoftNotFound.Reason));
        rig.Store.Row(source, Url("/a"))!.ContentHash.ShouldBe(good);
        Done(results, "/b").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        _hits.Count(h => h.EndsWith(SoftNotFound.ProbeSuffix, StringComparison.Ordinal)).ShouldBe(2);
        // Not stored: the raw blobs are the three good pages of the first run.
        Directory.EnumerateFiles(Path.Join(rig.Path.BlobRoot.FullName, "raw", "public")).Count(f => !f.EndsWith(".links.json.gz", StringComparison.Ordinal))
            .ShouldBe(3);
    }

    [Fact]
    public async Task A_host_that_answers_missing_pages_with_404_has_no_fingerprint()
    {
        _softNotFound = false;
        _aMissing = true;
        await using var rig = CrawlRig.Create(_site);

        var results = await rig.CrawlAsync(Context(Guid.NewGuid()));

        // The same page is content when the host's probe got a real 404.
        Done(results, "/a").Outcome.ShouldBeOfType<FetchOutcome.Content>();
    }

    [Fact]
    public async Task A_challenge_page_keeps_the_last_good_content_backs_off_and_pauses_the_host_when_repeated()
    {
        var hosts = new InMemoryCrawlHostStore(TimeProvider.System);
        await using var rig = CrawlRig.Create(_site, hosts: hosts);
        var source = Guid.NewGuid();
        await rig.CrawlAsync(Context(source));
        var good = rig.Store.Row(source, Url("/a"))!.ContentHash;
        good.ShouldNotBeNull();

        _aChallenged = true;
        var second = Context(source);
        var requeues = rig.Requeues;
        var results = await rig.CrawlAsync(second);

        // The first challenge backed off as on a 429 and requeued; the second ended the host's part of the run.
        rig.Requeues.ShouldBeGreaterThan(requeues);
        _calls["GET /a"].ShouldBe(3);
        hosts[$"{TestSite.Host}:{_site.Port}"].BackoffFactor.ShouldBeGreaterThan(1);
        results[Url("/a")].ShouldBe(new CrawlFetchResult.NotReached(FetchOutcomes.Challenge));
        rig.Store.Row(source, Url("/a"))!.ContentHash.ShouldBe(good);
        (await rig.Store.GetAsync(second.Run.RunId, Ct))!.PausedHosts[$"{TestSite.Host}:{_site.Port}"].Reason.ShouldBe(FetchOutcomes.Challenge);
        var verdict = await rig.Traversal.EvaluateAsync(second, false, Ct);
        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldContain(TraversalCompleteness.HostPaused);
    }

    [Fact]
    public async Task A_sitemap_that_fails_once_is_retried_and_lists_its_urls()
    {
        _robotsSitemap = "/sitemap.xml";
        _sitemapFailures = 1;
        await using var rig = CrawlRig.Create(_site);

        var events = await ListAsync(rig);

        events.OfType<ListingEvent<CrawlCheckpoint>.Failure>().ShouldBeEmpty();
        Seeds(events).ShouldContain(Url("/b"));
        _calls["GET /sitemap.xml"].ShouldBe(2);
        events[^1].ShouldBe(new ListingEvent<CrawlCheckpoint>.Done(Completeness.Full));
    }

    [Fact]
    public async Task A_sitemap_that_keeps_failing_is_unavailable_after_two_retries()
    {
        _robotsSitemap = "/sitemap.xml";
        _sitemapFailures = 5;
        await using var rig = CrawlRig.Create(_site);

        var events = await ListAsync(rig);

        events.OfType<ListingEvent<CrawlCheckpoint>.Failure>().Select(f => f.Reason).ShouldBe([CrawlConnector.SitemapUnavailable]);
        _calls["GET /sitemap.xml"].ShouldBe(1 + CrawlConnector.SitemapRetries);
    }

    [Fact]
    public async Task A_plain_text_sitemap_lists_its_urls()
    {
        _robotsSitemap = "/urls.txt";
        await using var rig = CrawlRig.Create(_site);

        var events = await ListAsync(rig);

        events.OfType<ListingEvent<CrawlCheckpoint>.Failure>().ShouldBeEmpty();
        Seeds(events).ShouldBe([Url("/"), Url("/a"), Url("/b")], ignoreOrder: true);
    }

    [Fact]
    public async Task Sitemap_index_xml_is_probed_after_sitemap_xml_and_its_absence_is_no_failure()
    {
        // Absent: both guesses answer with the host's "page not found" page, a 200 or a 404.
        foreach (var soft in new[] { true, false })
        {
            _softNotFound = soft;
            await using var none = CrawlRig.Create(_site);
            var without = await ListAsync(none);
            without.OfType<ListingEvent<CrawlCheckpoint>.Failure>().ShouldBeEmpty();
            Seeds(without).ShouldBe([Url("/")]);
        }

        _sitemapIndex = true;
        await using var rig = CrawlRig.Create(_site);

        var events = await ListAsync(rig);

        events.OfType<ListingEvent<CrawlCheckpoint>.Failure>().ShouldBeEmpty();
        Seeds(events).ShouldBe([Url("/"), Url("/a"), Url("/b")], ignoreOrder: true);
        _hits.ShouldContain("GET /sitemap.xml");
        _hits.ShouldContain("GET /sitemap_index.xml");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).PathAndQuery == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    private static List<string> Seeds(List<ListingEvent<CrawlCheckpoint>> events) =>
        [.. events.OfType<ListingEvent<CrawlCheckpoint>.Item>().Select(i => i.Work.CanonicalIdentity)];

    private async Task<List<ListingEvent<CrawlCheckpoint>>> ListAsync(CrawlRig rig)
    {
        var context = Context(Guid.NewGuid());
        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(new ListingContext(context.Run.SourceId, "public", context.Run.RunId, context.Settings), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        return events;
    }

    private CrawlRunContext Context(Guid sourceId) =>
        new(new CrawlRunKey(sourceId, "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual,
            ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/")], MaxPages = 100 }), Revalidate: false);

    private string Url(string path) => _site.Url(path).AbsoluteUri;

    private IResult Missing() =>
        Results.Content(NotFoundPage, "text/html", statusCode: _softNotFound ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);

    private void Map(WebApplication app)
    {
        static IResult Html(string body) => Results.Content($"<html><body><main>{body}</main></body></html>", "text/html");

        app.Use(async (http, next) =>
        {
            var hit = $"{http.Request.Method} {http.Request.Path}";
            _hits.Enqueue(hit);
            _calls.AddOrUpdate(hit, 1, (_, n) => n + 1);
            await next();
        });
        app.MapGet("/robots.txt", (HttpContext http) =>
            Results.Text("User-agent: *\nAllow: /\n" + (_robotsSitemap is { } sitemap ? $"Sitemap: http://{http.Request.Host}{sitemap}\n" : string.Empty)));
        app.MapGet("/sitemap.xml", (HttpContext http) => _robotsSitemap is null
            ? Missing()
            : _calls["GET /sitemap.xml"] <= _sitemapFailures
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Content($"<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>http://{http.Request.Host}/b</loc></url></urlset>",
                    "application/xml"));
        app.MapGet("/sitemap_index.xml", (HttpContext http) => _sitemapIndex
            ? Results.Content($"<?xml version=\"1.0\"?><sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>http://{http.Request.Host}/urls.txt</loc></sitemap></sitemapindex>",
                "application/xml")
            : Missing());
        app.MapGet("/urls.txt", (HttpContext http) => Results.Text($"http://{http.Request.Host}/a\n\nhttp://{http.Request.Host}/b\n", "text/plain"));
        app.MapGet("/", () => Html("<p>Start</p><a href=\"/a\">A</a><a href=\"/b\">B</a>"));
        app.MapGet("/a", (HttpContext http) =>
        {
            if (_aChallenged)
            {
                http.Response.Headers["cf-mitigated"] = "challenge";
                http.Response.Headers.RetryAfter = "1";
                return Results.Content("<html><body>Just a moment...</body></html>", "text/html", statusCode: StatusCodes.Status403Forbidden);
            }

            return _aMissing ? Results.Content(NotFoundPage, "text/html") : Html("<p>Sida A</p>");
        });
        app.MapGet("/b", () => Html("<p>Sida B</p>"));
        // Every other path: the host's "page not found" page, with a 200 (a soft 404) or a real 404.
        app.MapFallback(Missing);
    }
}
