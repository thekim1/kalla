using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Tests.Connectors;

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
        rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).Order(StringComparer.Ordinal).ShouldBe(
            [
                Url("/4.10000000000a.html"), Url("/4.20000000000b.html"), Url("/4.40000000000d.html"), Url("/4.50000000000e.html"),
                Url("/4.60000000000f.html"), Url("/section"), Url("/section/go-in"), Url("/section/go-out"),
            ],
            ignoreOrder: false,
            "a path include admits the section's node-id pages under their identity, and no page of another section");
        Content(results, "/4.10000000000a.html").Facts.CanonicalIdentity.ShouldBe(Url("/4.10000000000a.html"));
        Content(results, "/section/go-in").Facts.CanonicalIdentity.ShouldBe(Url("/4.60000000000f.html"));
        Done(results, "/section/go-out").Outcome.ShouldBe(new FetchOutcome.Skipped("redirect.out_of_scope"));
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).PathAndQuery == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    private static FetchOutcome.Content Content(Dictionary<string, CrawlFetchResult> results, string path) =>
        Done(results, path).Outcome.ShouldBeOfType<FetchOutcome.Content>();

    private static CrawlRunContext Context(System.Text.Json.JsonElement settings) =>
        new(new CrawlRunKey(Guid.NewGuid(), "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual, settings, Revalidate: false);

    /// <summary>A rig whose test host has a verified Sitevision identity rule (the umea.se profile's).</summary>
    private CrawlRig Rig()
    {
        var rig = CrawlRig.Create(_site);
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
        // The crawler requests the identity (the node id), as Sitevision serves it (design §3.3).
        app.MapGet("/{node}", (string node) => node switch
        {
            "4.10000000000a.html" => Html("<p>A</p><a href=\"/section/djup/sida-e.4.50000000000e.html\">E</a><a href=\"/other/sida-g.4.80000000000b.html\">G</a>"),
            "4.20000000000b.html" or "4.30000000000c.html" or "4.40000000000d.html" or "4.50000000000e.html" or "4.60000000000f.html"
                or "4.70000000000a.html" or "4.80000000000b.html" => Html($"<p>{node}</p>"),
            _ => Results.NotFound(),
        });
    }
}
