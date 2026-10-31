using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// The host's site profile <c>excludedTypes</c> reach the crawl scope (connectors design §5.2, §5.6): a link whose
/// extension names an excluded type is never admitted or requested, a response of an excluded type is not read past its
/// headers (so nothing is stored or ingested), and the other types are fetched as before. Without the profile the same
/// links are fetched, which is the negative proof that the profile, not the defaults, excluded them.
/// </summary>
public sealed class SiteProfileExcludedTypesTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _hits = new();
    private readonly string _marker = Guid.NewGuid().ToString("N");
    private TestSite _site = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _site = await TestSite.StartAsync(Map);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_type_in_the_hosts_excluded_types_is_not_fetched_and_the_other_types_are()
    {
        await using var rig = CrawlRig.Create(_site);
        // The profile's key is the host without the test site's port (SiteProfileHost).
        rig.Profiles.Set("public", TestSite.Host, new SiteProfile { MainRoot = "main", ExcludedTypes = ["image/*", "video/*", "text/plain"] });
        var context = Context();

        var results = await rig.CrawlAsync(context);
        var urls = rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).ToList();

        urls.ShouldNotContain(Url("/notes.txt"), "an excluded extension is not admitted");
        _hits.ShouldNotContain("GET /notes.txt");
        Done(results, "/data").Outcome.ShouldBe(new FetchOutcome.Refused(Worker.Connectors.RunItemOutcome.Unsupported, "http.content_type"),
            "an excluded type without an extension stops after the headers");
        Done(results, "/sida").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        Done(results, "/rapport.pdf").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        urls.ShouldNotContain(Url("/karta.jpg"), "the profile keeps the default image exclusion");
    }

    [Fact]
    public async Task Without_the_profile_the_same_links_are_fetched()
    {
        await using var rig = CrawlRig.Create(_site);
        var context = Context();

        var results = await rig.CrawlAsync(context);

        Done(results, "/notes.txt").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        Done(results, "/data").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        Done(results, "/sida").Outcome.ShouldBeOfType<FetchOutcome.Content>();
        rig.Store.Rows(context.Run.SourceId).Select(r => r.Url).ShouldNotContain(Url("/karta.jpg"), "images are excluded by default");
    }

    [Fact]
    public void The_scope_applies_each_hosts_profile_and_the_default_to_hosts_without_one()
    {
        var scope = CrawlScope.Create(new CrawlSettings { StartUrls = ["https://a.example/", "https://b.example/"] })
            .WithExcludedTypes(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["a.example"] = ["application/pdf"] });

        scope.Check("https://a.example/x.pdf").ShouldBe(ScopeDecision.Out(CrawlScope.ExcludedType));
        scope.Check("https://a.example/x.txt").ShouldBe(ScopeDecision.In);
        scope.Check("https://b.example/x.pdf").ShouldBe(ScopeDecision.In, "a host without a profile keeps the default");
        scope.Check("https://b.example/x.jpg").ShouldBe(ScopeDecision.Out(CrawlScope.ExcludedType));
        scope.AcceptedMediaTypes(new Uri("https://a.example:8443/x")).ShouldNotContain("application/pdf");
        scope.AcceptedMediaTypes(new Uri("https://b.example/x")).ShouldContain("application/pdf");
    }

    private static CrawlFetchResult.Done Done(Dictionary<string, CrawlFetchResult> results, string path) =>
        results.Single(r => new Uri(r.Key).PathAndQuery == path).Value.ShouldBeOfType<CrawlFetchResult.Done>();

    private CrawlRunContext Context() =>
        new(new CrawlRunKey(Guid.NewGuid(), "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual,
            ContractJson.ToElement(new CrawlSettings { StartUrls = [Url("/")], MaxPages = 20, MaxDepth = 3, CrawlWindow = "22:00-06:00" }), Revalidate: false);

    private string Url(string path) => _site.Url(path).AbsoluteUri;

    private void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            _hits.Enqueue($"{context.Request.Method} {context.Request.Path}");
            await next(context);
        });
        app.MapGet("/", () => Results.Content(
            $"""<html><body><main><p>Start {_marker}</p><a href="/sida">Sida</a> <a href="/notes.txt">Anteckningar</a> <a href="/data">Data</a> <a href="/rapport.pdf">Rapport</a> <a href="/karta.jpg">Karta</a></main></body></html>""",
            "text/html"));
        app.MapGet("/sida", () => Results.Content($"<html><body><main><p>Sida {_marker}</p></main></body></html>", "text/html"));
        app.MapGet("/notes.txt", () => Results.Text($"Anteckningar {_marker}", "text/plain"));
        app.MapGet("/data", () => Results.Text($"Data {_marker}", "text/plain"));
        app.MapGet("/rapport.pdf", () => Results.Bytes(System.Text.Encoding.ASCII.GetBytes($"%PDF-1.7\n% {_marker}\n%%EOF\n"), "application/pdf"));
        app.MapGet("/karta.jpg", () => Results.Bytes([0xFF, 0xD8, 0xFF], "image/jpeg"));
    }
}
