using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Deletion;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// Freshness in the crawler (P1b item 4; connectors design §5.4, §18 "Integration" and "Deletion guard"; weakness review
/// §3) against a fixture site whose sitemap the test edits between runs: completeness by ratio (one failing seed of 200
/// still completes, three do not, a failed sitemap pauses its host), the removal check (HEAD, GET after a 405, a 404
/// deleted, a 200 kept), the <c>lastmod</c> skip and incremental runs only on a host whose site profile marks
/// <c>lastmod</c> reliable, and <c>revalidateEvery</c>. Every rule has a negative proof.
/// </summary>
public sealed class FreshnessTests : IAsyncLifetime
{
    private static readonly string Old = "2026-01-15";

    private readonly ConcurrentDictionary<string, string?> _sitemap = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _version = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _hits = new();
    private readonly HashSet<string> _noHead = new(StringComparer.Ordinal);
    private int _sitemapStatus = 200;
    private TestSite _site = null!;

    public async ValueTask InitializeAsync() => _site = await TestSite.StartAsync(Map);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_page_that_left_the_sitemap_is_deleted_on_404_and_kept_on_200_in_a_full_traversal()
    {
        await using var rig = CrawlRig.Create(_site);
        var source = Guid.NewGuid();
        List("keep", "gone", "nohead", "stays");
        _noHead.Add("/p/nohead");
        var first = Context(source);
        await rig.CrawlAsync(first);
        (await rig.Traversal.EvaluateAsync(first, false, Ct)).Complete.ShouldBeTrue();
        var documents = Documents("keep", "gone", "nohead", "stays").ToDictionary(d => d.Key, d => d.Value with { LastRunId = first.Run.RunId });

        // keep, gone and nohead leave the sitemap; gone is removed from the site.
        Unlist("keep", "gone", "nohead");
        _status["/p/gone"] = 404;
        _hits.Clear();
        var second = Context(source);
        var results = await rig.CrawlAsync(second);
        var verdict = await rig.Traversal.EvaluateAsync(second, false, Ct);

        verdict.Complete.ShouldBeTrue();
        Done(results, "/p/gone").ShouldSatisfyAllConditions(
            d => d.Outcome.ShouldBe(new FetchOutcome.Gone("http.404")), d => d.Facts.ShouldContain(CrawlTraversal.RemovalCheck));
        Done(results, "/p/keep").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        Done(results, "/p/nohead").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        // HEAD first; a full traversal then fetches a live page as usual; a refused HEAD falls back to GET.
        _hits.ShouldContain("HEAD /p/gone");
        _hits.ShouldNotContain("GET /p/gone");
        _hits.ShouldContain("HEAD /p/keep");
        _hits.ShouldContain("GET /p/keep");
        _hits.ShouldContain("HEAD /p/nohead");
        _hits.ShouldContain("GET /p/nohead");
        // The removal checks are no seeds, and their SitemapLastMod is cleared so the next run does not check them again.
        (await rig.Store.GetAsync(second.Run.RunId, Ct))!.Seeds.ShouldBe(2);
        rig.Store.Row(source, Url("/p/keep"))!.SitemapLastMod.ShouldBeNull();
        rig.Store.Row(source, Url("/p/stays"))!.SitemapLastMod.ShouldNotBeNull();

        var candidates = Reconcile(second, results, documents, verdict);
        candidates.ShouldBe([new DeletionCandidate(documents["gone"].Id, DeletionReasons.Http404)]);
        DeletionGuard.Evaluate(candidates, documents.Count, false, GuardSettings.Default).Apply.ShouldBe(candidates);
    }

    [Fact]
    public async Task A_partial_listing_makes_no_removal_check_so_a_404_page_off_the_sitemap_stays()
    {
        // Negative proof of the removal check: without a complete listing nothing is known to have left the sitemap.
        await using var rig = CrawlRig.Create(_site);
        var source = Guid.NewGuid();
        List("gone", "stays");
        await rig.CrawlAsync(Context(source));
        Unlist("gone");
        _status["/p/gone"] = 404;
        var second = Context(source);
        await rig.Traversal.StartAsync(second, Ct);
        var events = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(new ListingContext(source, "public", second.Run.RunId, second.Settings), new CrawlCheckpoint(), Ct))
        {
            events.Add(e);
        }

        await rig.Traversal.AdmitSeedsAsync(second, events, Ct);

        (await rig.Traversal.ListingDoneAsync(second, Completeness.Partial(CrawlConnector.SitemapUnavailable), Ct)).ShouldBe(0);
        (await rig.Traversal.ListingDoneAsync(second, Completeness.Full, Ct)).ShouldBe(1);
        rig.Store.Row(source, Url("/p/gone"))!.ShouldSatisfyAllConditions(r => r.RemovalCheck.ShouldBeTrue(), r => r.Depth.ShouldBe((short)0));
    }

    [Fact]
    public async Task In_an_incremental_run_the_removal_check_is_a_HEAD_and_a_live_page_is_only_kept()
    {
        await using var rig = CrawlRig.Create(_site);
        rig.Profiles.Set("public", TestSite.Host, lastmodReliable: true);
        var source = Guid.NewGuid();
        List("keep", "gone", "nohead", "stays");
        _noHead.Add("/p/nohead");
        await rig.CrawlAsync(Context(source));

        Unlist("keep", "gone", "nohead");
        _status["/p/gone"] = 410;
        _hits.Clear();
        var (context, results) = await rig.RunAsync(Context(source, incremental: true));

        context.Incremental.ShouldBeTrue();
        Done(results, "/p/gone").Outcome.ShouldBe(new FetchOutcome.Gone("http.410"));
        Done(results, "/p/keep").ShouldSatisfyAllConditions(
            d => d.Outcome.ShouldBeOfType<FetchOutcome.NotModified>(), d => d.Facts.ShouldContain(CrawlTraversal.RemovalCheck));
        Done(results, "/p/nohead").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        _hits.ShouldContain("HEAD /p/keep");
        _hits.ShouldNotContain("GET /p/keep");
        _hits.ShouldContain("GET /p/nohead");
        rig.Store.Row(source, Url("/p/keep"))!.LastStatus.ShouldBe((short)200);
        // Unchanged sitemap pages are not fetched at all in an incremental run.
        _hits.ShouldNotContain("GET /p/stays");
        (await rig.Traversal.EvaluateAsync(context, false, Ct)).Reasons.ShouldBe([TraversalCompleteness.Incremental]);
    }

    [Fact]
    public async Task An_incremental_run_fetches_changed_and_new_seeds_only_and_never_marks_the_traversal_complete()
    {
        await using var rig = CrawlRig.Create(_site);
        rig.Profiles.Set("public", TestSite.Host, lastmodReliable: true);
        var source = Guid.NewGuid();
        List("a", "b");
        await rig.CrawlAsync(Context(source));

        _sitemap["/p/b"] = Future();
        _version["/p/b"] = 2;
        List("new");
        _hits.Clear();
        var (context, results) = await rig.RunAsync(Context(source, incremental: true));
        var verdict = await rig.Traversal.EvaluateAsync(context, false, Ct);

        _hits.ShouldContain("GET /p/b");
        _hits.ShouldContain("GET /p/new");
        _hits.ShouldNotContain("GET /p/a");
        _hits.ShouldNotContain("GET /");
        results.Keys.ShouldNotContain(Url("/p/a"));
        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldBe([TraversalCompleteness.Incremental]);
        (await rig.Store.GetAsync(context.Run.RunId, Ct))!.Complete.ShouldBeFalse();
        // Negative proof: the same facts as a full traversal complete it.
        var full = Context(source);
        await rig.CrawlAsync(full);
        (await rig.Traversal.EvaluateAsync(full, false, Ct)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_lastmod_reliable_an_incremental_run_becomes_a_full_traversal_that_fetches_every_url()
    {
        await using var rig = CrawlRig.Create(_site);
        rig.Profiles.Set("public", TestSite.Host, lastmodReliable: false);
        var source = Guid.NewGuid();
        List("a", "b");
        await rig.CrawlAsync(Context(source));
        _hits.Clear();

        var (context, results) = await rig.RunAsync(Context(source, incremental: true));

        context.Incremental.ShouldBeFalse();
        _hits.ShouldContain("GET /p/a");
        _hits.ShouldContain("GET /p/b");
        results.Values.OfType<CrawlFetchResult.Done>().ShouldAllBe(d => !d.Facts.Contains(CrawlTraversal.LastmodSkip));
        (await rig.Traversal.EvaluateAsync(context, false, Ct)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task The_lastmod_skip_needs_a_reliable_host_an_old_lastmod_and_a_run_that_does_not_revalidate()
    {
        await using var rig = CrawlRig.Create(_site);
        var source = Guid.NewGuid();
        List("a", "b");
        await rig.CrawlAsync(Context(source));

        // Not reliable (the default, weakness review §3): every URL is requested.
        _hits.Clear();
        await rig.CrawlAsync(Context(source));
        _hits.ShouldContain("GET /p/a");

        rig.Profiles.Set("public", TestSite.Host, lastmodReliable: true);
        rig.ProfileClock.Advance(LastmodReliability.CacheFor);
        _sitemap["/p/b"] = Future();
        _hits.Clear();
        var reliable = Context(source);
        var results = await rig.CrawlAsync(reliable);
        _hits.ShouldNotContain("GET /p/a");
        Done(results, "/p/a").ShouldSatisfyAllConditions(
            d => d.Outcome.ShouldBeOfType<FetchOutcome.NotModified>(), d => d.Facts.ShouldContain(CrawlTraversal.LastmodSkip));
        // A newer lastmod is fetched; the skipped page's links still expand, and a skip resolves its seed.
        _hits.ShouldContain("GET /p/b");
        results.Keys.ShouldContain(Url("/p/linked"));
        (await rig.Traversal.EvaluateAsync(reliable, false, Ct)).Complete.ShouldBeTrue();

        // revalidateEvery: a revalidating traversal makes no skip and sends no validators.
        _hits.Clear();
        await rig.CrawlAsync(Context(source) with { Revalidate = true });
        _hits.ShouldContain("GET /p/a");
    }

    [Theory]
    [InlineData(1, CompletenessRule.Ratio, true)]
    [InlineData(3, CompletenessRule.Ratio, false)]
    [InlineData(1, CompletenessRule.Strict, false)]
    public async Task One_failing_seed_of_200_completes_a_traversal_by_ratio_three_do_not(int failing, CompletenessRule rule, bool complete)
    {
        await using var rig = CrawlRig.Create(_site, rule: rule);
        List([.. Enumerable.Range(0, 199).Select(i => $"s{i}")]);
        for (var i = 0; i < failing; i++)
        {
            _status[$"/p/s{i}"] = 403;
        }

        var context = Context(Guid.NewGuid());
        await rig.CrawlAsync(context);
        var record = (await rig.Store.GetAsync(context.Run.RunId, Ct))!;
        var verdict = await rig.Traversal.EvaluateAsync(context, false, Ct);

        record.Seeds.ShouldBe(200);
        record.SeedsFailed.ShouldBe(failing);
        verdict.Complete.ShouldBe(complete);
    }

    [Fact]
    public async Task A_sitemap_that_cannot_be_read_pauses_its_host_so_the_traversal_is_incomplete_by_ratio()
    {
        await using var rig = CrawlRig.Create(_site);
        List("a");
        _sitemapStatus = 500;
        var context = Context(Guid.NewGuid());

        await rig.CrawlAsync(context);
        var record = (await rig.Store.GetAsync(context.Run.RunId, Ct))!;
        var verdict = await rig.Traversal.EvaluateAsync(context, false, Ct);

        // The start URL resolved (1 of 1 seeds), but the sitemap's seeds are unknown.
        record.SeedsFailed.ShouldBe(0);
        record.PausedHosts.ShouldContainKey($"{TestSite.Host}:{_site.Port}");
        record.PausedHosts.Single().Value.ShouldBe(new HostPause(1, CrawlConnector.SitemapUnavailable, null));
        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldBe([TraversalCompleteness.HostPaused]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Future() => DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).PathAndQuery == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    /// <summary>What the engine's reconcile does with a run's fetch results: reached documents seen, explicit answers gone.</summary>
    private static IReadOnlyList<DeletionCandidate> Reconcile(
        CrawlRunContext context, Dictionary<string, CrawlFetchResult> results, Dictionary<string, SourceDocument> documents, TraversalVerdict verdict)
    {
        var byUrl = documents.Values.ToDictionary(d => d.CanonicalIdentity, StringComparer.Ordinal);
        var gone = new Dictionary<Guid, string>();
        var current = new List<SourceDocument>();
        foreach (var document in byUrl.Values)
        {
            var reached = results.TryGetValue(document.CanonicalIdentity, out var result) && result is CrawlFetchResult.Done { Outcome: FetchOutcome.Content or FetchOutcome.NotModified };
            if (result is CrawlFetchResult.Done { Outcome: FetchOutcome.Gone { Reason: var reason } })
            {
                gone[document.Id] = reason;
            }

            current.Add(reached ? document with { LastRunId = context.Run.RunId } : document);
        }

        return DeletionCandidates.FromTraversal(context.Run.RunId, current, verdict, gone, new HashSet<Guid>(), DateTimeOffset.UtcNow);
    }

    private Dictionary<string, SourceDocument> Documents(params string[] names) =>
        names.ToDictionary(n => n, n => new SourceDocument { Id = Guid.NewGuid(), CanonicalIdentity = Url($"/p/{n}"), LastSeenAt = DateTimeOffset.UtcNow });

    private void List(params string[] names)
    {
        foreach (var name in names)
        {
            _sitemap[$"/p/{name}"] = Old;
        }
    }

    private void Unlist(params string[] names)
    {
        foreach (var name in names)
        {
            _sitemap.TryRemove($"/p/{name}", out _);
        }
    }

    private CrawlRunContext Context(Guid sourceId, bool incremental = false) =>
        new(new CrawlRunKey(sourceId, "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual,
            ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/")], MaxPages = 1000 }), Revalidate: false, Incremental: incremental);

    private string Url(string path) => _site.Url(path).AbsoluteUri;

    private void Map(WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            _hits.Enqueue($"{http.Request.Method} {http.Request.Path}");
            await next();
        });
        app.MapGet("/robots.txt", (HttpContext http) => Results.Text($"User-agent: *\nSitemap: http://{http.Request.Host}/sitemap.xml\n"));
        app.MapGet("/sitemap.xml", (HttpContext http) =>
        {
            if (_sitemapStatus != 200)
            {
                return Results.StatusCode(_sitemapStatus);
            }

            var entries = string.Concat(_sitemap.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e =>
                $"<url><loc>http://{http.Request.Host}{e.Key}</loc>{(e.Value is null ? string.Empty : $"<lastmod>{e.Value}</lastmod>")}</url>"));
            return Results.Content($"<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">{entries}</urlset>", "application/xml");
        });
        app.MapGet("/", () => Results.Content("<html><body><main><p>Start</p></main></body></html>", "text/html"));
        app.MapMethods("/p/{name}", ["GET", "HEAD"], (HttpContext http, string name) =>
        {
            var path = $"/p/{name}";
            if (HttpMethods.IsHead(http.Request.Method) && _noHead.Contains(path))
            {
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            var status = _status.GetValueOrDefault(path, 200);
            if (status != 200)
            {
                return Results.StatusCode(status);
            }

            var links = name == "a" ? "<a href=\"/p/linked\">L</a>" : string.Empty;
            return Results.Content($"<html><body><main><p>Sida {name} v{_version.GetValueOrDefault(path, 1)}</p>{links}</main></body></html>", "text/html");
        });
    }
}
