using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.SiteProfiles;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// <c>include</c> and <c>exclude</c> match a URL's path form, not its identity (connectors design §5.2, §3.1 rule 10; P1a
/// item 20, pilot set-up 2026-11-01): on a host whose site profile has a verified Sitevision identity rule, a glob over a
/// section admits the section's pages from links, the sitemap and redirects, though the frontier and the documents keep
/// the node-id identity; where only the identity is known (a frontier row, a document), the patterns decide nothing.
/// </summary>
public sealed class CrawlPathScopeTests : IAsyncLifetime
{
    private const string Pattern = @"^/(?:.*/)?(?:[^/]+\.)?(?<id>[0-9]+\.[0-9a-f]{10,})\.html$";

    private readonly string _tag = Guid.NewGuid().ToString("N");
    private TestSite _site = null!;

    public async ValueTask InitializeAsync() => _site = await TestSite.StartAsync(Map);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_section_include_admits_its_node_id_pages_from_links_the_sitemap_and_redirects_and_nothing_else()
    {
        await using var rig = Rig();
        var context = Context(Settings(include: [Glob("/section"), Glob("/section/**")]));

        var results = await rig.CrawlAsync(context);

        // Links (two paths to one node: one row), a link from a node page, a sitemap entry and a redirect target in the section.
        string[] expected =
        [
            Url("/4.10000000000a.html"), Url("/4.20000000000b.html"), Url("/4.40000000000d.html"), Url("/4.50000000000e.html"),
            Url("/4.60000000000f.html"), Url("/section"), Url("/section/go-in"), Url("/section/go-out"),
        ];
        var rows = rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).Order(StringComparer.Ordinal).ToList();
        rows.ShouldBe(expected, ignoreOrder: false,
            "a path include admits the section's node-id pages under their identity, and no page of another section; "
            + $"missing [{string.Join(", ", expected.Except(rows, StringComparer.Ordinal))}], extra [{string.Join(", ", rows.Except(expected, StringComparer.Ordinal))}]");
        Content(results, "/4.10000000000a.html").Facts.CanonicalIdentity.ShouldBe(Url("/4.10000000000a.html"));
        Content(results, "/section/go-in").Facts.CanonicalIdentity.ShouldBe(Url("/4.60000000000f.html"));
        // #228: each node row keeps the path it was first found under in the run and is fetched there; a redirect target keeps none.
        rig.Store.Rows(context.Run.SourceId).OrderBy(r => r.Url, StringComparer.Ordinal).Select(r => r.FetchUrl).ShouldBe(
            [
                Url("/section/sida-a.4.10000000000a.html"), Url("/section/sida-b.4.20000000000b.html"), Url("/section/hidden/sida-d.4.40000000000d.html"),
                Url("/section/djup/sida-e.4.50000000000e.html"), null, null, null, null,
            ],
            ignoreOrder: false);
        Content(results, "/4.10000000000a.html").Facts.FinalUrl!.AbsoluteUri.ShouldBe(Url("/section/sida-a.4.10000000000a.html"));
        Content(results, "/section/go-in").Facts.FinalUrl!.AbsoluteUri.ShouldBe(Url("/4.60000000000f.html"), "a redirect target is fetched under its identity, as before");
        Done(results, "/section/go-out").Outcome.ShouldBe(new FetchOutcome.Skipped("redirect.out_of_scope"));
        (await rig.Traversal.EvaluateAsync(context, cancelled: false, Ct)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cold_host_whose_latency_stretches_the_slot_interval_past_a_second_still_drains()
    {
        // The flake of the test above (2026-11-01): a slow first response (a cold start) makes the external host's slot
        // interval its latency (HostBudgetPolicy.Interval); past MaxInProcessWait the fetches come back Later, and a drain
        // bounded by rounds ran out of them before the slot opened.
        var hosts = new InMemoryCrawlHostStore(TimeProvider.System);
        hosts[$"{TestSite.Host}:{_site.Port}"] = new CrawlHostState
        {
            Host = $"{TestSite.Host}:{_site.Port}", Kind = HostKind.External, RequestsPerSecond = 500, MaxConnections = 4,
            NextAllowedAt = DateTimeOffset.UtcNow, LatencyEwmaMs = 2500,
        };
        await using var rig = Rig(hosts);
        var context = Context(Settings(include: [Glob("/section"), Glob("/section/**")]));

        var results = await rig.CrawlAsync(context);

        rig.Requeues.ShouldBeGreaterThan(0, "the stretched interval sent fetches back");
        results.Values.ShouldAllBe(r => r is CrawlFetchResult.Done);
        rig.Store.Rows(context.Run.SourceId).ShouldAllBe(r => r.State == FrontierState.Idle);
        (await rig.Traversal.EvaluateAsync(context, cancelled: false, Ct)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_path_exclude_excludes_a_node_id_page_under_it()
    {
        await using var rig = Rig();
        var context = Context(Settings(exclude: [Glob("/section/hidden/**"), Glob("/other/**")]));

        await rig.CrawlAsync(context);

        var urls = rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).ToList();
        urls.ShouldNotContain(Url("/4.40000000000d.html"), "the page under the excluded path is not admitted");
        urls.ShouldContain(Url("/4.10000000000a.html"));
    }

    [Fact]
    public void Where_only_the_identity_is_known_the_patterns_decide_nothing_and_never_narrow()
    {
        var settings = new CrawlSettings { StartUrls = ["https://www.umea.se/kommunochpolitik"], Include = ["www.umea.se/kommunochpolitik/**"], Exclude = ["www.umea.se/x/**"] };
        var scope = CrawlScope.Create(settings);
        var canonicalizer = new CanonicalizerOptions
        {
            Hosts = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal) { ["www.umea.se"] = new([], PathIdentityRule.Create(Pattern, "/${id}.html")) },
        };
        const string link = "https://www.umea.se/kommunochpolitik/languages.4.1c16b00a1742340e02e1dd1.html";

        UrlCanonicalizer.Canonicalize(link, canonicalizer).Value.ShouldBe("https://www.umea.se/4.1c16b00a1742340e02e1dd1.html");
        UrlCanonicalizer.PathForm(link, canonicalizer).Value.ShouldBe(link);
        scope.Check(UrlCanonicalizer.PathForm(link, canonicalizer).Value!).ShouldBe(ScopeDecision.In);
        scope.Check(UrlCanonicalizer.PathForm("https://www.umea.se/x/sida.4.1c16b00a1742340e02e1dd1.html", canonicalizer).Value!).Reason.ShouldBe(CrawlScope.Excluded);
        // A node-id identity: in scope whatever the patterns say; the host and type rules still apply.
        scope.CheckIdentity("https://www.umea.se/4.1c16b00a1742340e02e1dd1.html", canonicalizer).ShouldBe(ScopeDecision.In);
        scope.CheckIdentity("https://other.se/4.1c16b00a1742340e02e1dd1.html", canonicalizer).Reason.ShouldBe(CrawlScope.OffSite);
        // An identity the rule did not map is its own path form.
        scope.CheckIdentity("https://www.umea.se/bygga", canonicalizer).Reason.ShouldBe(CrawlScope.NotIncluded);

        var document = new SourceDocument { Id = Guid.NewGuid(), CanonicalIdentity = "https://www.umea.se/4.1c16b00a1742340e02e1dd1.html", LastSeenAt = DateTimeOffset.UtcNow };
        var verdict = new TraversalVerdict(false, [], null, null);
        SettingsCandidates.Crawl(Guid.NewGuid(), [document], verdict, new Dictionary<Guid, string>(), new HashSet<Guid>(), DateTimeOffset.UtcNow,
                new CrawlSource(settings, scope, canonicalizer), new Dictionary<Guid, string>(), new HashSet<Guid>(), identityChanged: false)
            .ShouldBeEmpty("a node-id document is never scope-narrowed by a path pattern");
    }

    [Fact]
    public void A_kept_path_form_decides_the_patterns_and_a_row_or_document_without_one_keeps_the_identity_check()
    {
        var settings = new CrawlSettings { StartUrls = ["https://www.umea.se/kommunochpolitik"], Include = ["www.umea.se/kommunochpolitik/**"] };
        var scope = CrawlScope.Create(settings);
        var canonicalizer = new CanonicalizerOptions
        {
            Hosts = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal) { ["www.umea.se"] = new([], PathIdentityRule.Create(Pattern, "/${id}.html")) },
        };
        const string node = "https://www.umea.se/4.1c16b00a1742340e02e1dd1.html";
        const string inside = "https://www.umea.se/kommunochpolitik/languages.4.1c16b00a1742340e02e1dd1.html";
        const string outside = "https://www.umea.se/bygga/languages.4.1c16b00a1742340e02e1dd1.html";

        // #228: the path form a row or document keeps decides; the bare node id (an old row, a redirect target's URL) does not.
        scope.CheckStored(node, inside, canonicalizer).ShouldBe(ScopeDecision.In);
        scope.CheckStored(node, outside, canonicalizer).Reason.ShouldBe(CrawlScope.NotIncluded);
        scope.CheckStored(node, null, canonicalizer).ShouldBe(ScopeDecision.In);
        scope.CheckStored(node, node, canonicalizer).ShouldBe(ScopeDecision.In);
        scope.CheckStored("https://www.umea.se/bygga", "https://www.umea.se/bygga", canonicalizer).Reason.ShouldBe(CrawlScope.NotIncluded);

        SourceDocument Document(string? url, string? fetchUrl) =>
            new() { Id = Guid.NewGuid(), CanonicalIdentity = node, Url = url, FetchUrl = fetchUrl, LastSeenAt = DateTimeOffset.UtcNow };
        var old = Document(node, null);
        var foundOutside = Document(inside, outside);
        var fetchedOutside = Document(outside, null);
        var kept = Document(inside, null);
        var verdict = new TraversalVerdict(false, [], null, null);
        SettingsCandidates.Crawl(Guid.NewGuid(), [old, foundOutside, fetchedOutside, kept], verdict, new Dictionary<Guid, string>(), new HashSet<Guid>(),
                DateTimeOffset.UtcNow, new CrawlSource(settings, scope, canonicalizer), new Dictionary<Guid, string>(), new HashSet<Guid>(), identityChanged: false)
            .ShouldBe([new DeletionCandidate(foundOutside.Id, DeletionReasons.ScopeNarrowed), new DeletionCandidate(fetchedOutside.Id, DeletionReasons.ScopeNarrowed)],
                "the frontier's path form wins over Documents.Url, which counts where the frontier keeps none; an old node-id document is kept");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).PathAndQuery == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    private static FetchOutcome.Content Content(Dictionary<string, CrawlFetchResult> results, string path) =>
        Done(results, path).Outcome.ShouldBeOfType<FetchOutcome.Content>();

    private static CrawlRunContext Context(System.Text.Json.JsonElement settings) =>
        new(new CrawlRunKey(Guid.NewGuid(), "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual, settings, Revalidate: false);

    /// <summary>A rig whose test host has a verified Sitevision identity rule (the umea.se profile's).</summary>
    private CrawlRig Rig(InMemoryCrawlHostStore? hosts = null)
    {
        var rig = CrawlRig.Create(_site, hosts: hosts);
        rig.Profiles.Set("public", TestSite.Host, new SiteProfile
        {
            MainRoot = "main",
            IdentityRule = new SiteProfileIdentityRule { Pattern = Pattern, Replacement = "/${id}.html", Verified = true },
        });
        return rig;
    }

    private string Url(string path) => _site.Url(path).AbsoluteUri;

    private string Glob(string path) => $"{TestSite.Host}:{_site.Port}{path}";

    private System.Text.Json.JsonElement Settings(IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null) =>
        ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/section")], Include = include ?? [], Exclude = exclude ?? [], MaxPages = 100 });

    private void Map(WebApplication app)
    {
        IResult Html(string body) => Results.Content($"<html><body><main><p>{_tag}</p>{body}</main></body></html>", "text/html");

        app.MapGet("/robots.txt", (HttpContext http) => Results.Text($"User-agent: *\nAllow: /\nSitemap: http://{http.Request.Host}/sitemap.xml\n"));
        app.MapGet("/sitemap.xml", (HttpContext http) => Results.Content(
            $"<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
            + $"<url><loc>http://{http.Request.Host}/section/sida-b.4.20000000000b.html</loc></url>"
            + $"<url><loc>http://{http.Request.Host}/other/sida-c.4.30000000000c.html</loc></url></urlset>",
            "application/xml"));
        app.MapGet("/section", () => Html(
            "<a href=\"/section/sida-a.4.10000000000a.html\">A</a><a href=\"/section/under/sida-a.4.10000000000a.html\">A igen</a>"
            + "<a href=\"/section/hidden/sida-d.4.40000000000d.html\">D</a><a href=\"/other/sida-c.4.30000000000c.html\">C</a>"
            + "<a href=\"/section/go-in\">In</a><a href=\"/section/go-out\">Ut</a>"));
        app.MapGet("/section/go-in", () => Results.Redirect("/section/ny.4.60000000000f.html"));
        app.MapGet("/section/go-out", () => Results.Redirect("/other/ny.4.70000000000a.html"));
        // Sitevision serves a node under any path that ends in its id; the crawler requests the path as found (#228).
        app.MapGet("/{**path}", (string path) => Node(Sitevision.NodeOf(path)));
        IResult Node(string node) => node switch
        {
            "4.10000000000a.html" => Html("<p>A</p><a href=\"/section/djup/sida-e.4.50000000000e.html\">E</a><a href=\"/other/sida-g.4.80000000000b.html\">G</a>"),
            "4.20000000000b.html" or "4.30000000000c.html" or "4.40000000000d.html" or "4.50000000000e.html" or "4.60000000000f.html"
                or "4.70000000000a.html" or "4.80000000000b.html" => Html($"<p>{node}</p>"),
            _ => Results.NotFound(),
        };
    }
}
