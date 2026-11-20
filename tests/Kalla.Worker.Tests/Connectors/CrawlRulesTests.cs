using AngleSharp.Html.Parser;
using Microsoft.Extensions.Time.Testing;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors.Crawl;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>Scope (§5.2), traps (§5.3), link and robots-directive extraction (§5.2) and the night window (§6.5).</summary>
public sealed class CrawlRulesTests
{
    private static CrawlScope Scope(string[]? include = null, string[]? exclude = null, string[]? extraHosts = null) =>
        CrawlScope.Create(new CrawlSettings
        {
            StartUrls = ["https://www.umea.se/"],
            Include = include ?? [],
            Exclude = exclude ?? CrawlSettings.DefaultExcludes,
            ExtraHosts = extraHosts ?? [],
        });

    [Theory]
    [InlineData("https://www.umea.se/forskola", true, false, null)]
    [InlineData("http://www.umea.se/forskola", true, false, null)]
    [InlineData("https://www.umea.se/4.88db09b1724ee319ca45d.html", true, false, null)]
    [InlineData("https://www.umea.se/sok?q=skola", false, false, "scope.excluded")]
    [InlineData("https://www.umea.se/login?next=%2F", false, false, "scope.excluded")]
    [InlineData("https://www.umea.se/bilder/karta.jpg", false, false, "scope.type")]
    [InlineData("https://www.umea.se/film.mp4", false, false, "scope.type")]
    [InlineData("https://www.umea.se/paket.zip", false, false, "scope.type")]
    [InlineData("https://www.umea.se/setup.exe", false, false, "scope.type")]
    [InlineData("https://www.umea.se/gammal.doc", false, false, "scope.type")]
    [InlineData("https://www.umea.se/beslut.pdf", true, false, null)]
    [InlineData("https://other.example/page", false, false, "scope.off_site")]
    [InlineData("https://umea.se/page", false, false, "scope.off_site")]
    public void Admits_only_in_scope_urls(string url, bool admitted, bool leaf, string? reason)
    {
        Scope().Check(url).ShouldBe(new ScopeDecision(admitted, leaf, reason));
    }

    [Fact]
    public void An_off_site_link_is_admitted_only_by_an_include_naming_its_host_and_then_as_a_leaf()
    {
        var scope = Scope(include: ["www.umea.se/**", "regionvasterbotten.se/vard/**", "re:.*\\.example/.*"]);

        scope.Check("https://regionvasterbotten.se/vard/akut").ShouldBe(ScopeDecision.OffSiteLeaf);
        scope.Check("https://regionvasterbotten.se/annat").ShouldBe(ScopeDecision.Out(CrawlScope.OffSite));
        // A regex include names no host literally: off-site stays out.
        scope.Check("https://other.example/x").ShouldBe(ScopeDecision.Out(CrawlScope.OffSite));
        scope.Check("https://www.umea.se/x").ShouldBe(ScopeDecision.In);
    }

    [Fact]
    public void An_include_written_with_a_scheme_admits_its_section_and_names_its_host()
    {
        // The pilot's source: both patterns pasted from the browser, which used to admit nothing, not even the start URLs.
        var scope = Scope(include:
        [
            "https://www.umea.se/forskolaskolaochutbildning/vuxenutbildning",
            "https://www.umea.se/forskolaskolaochutbildning/vuxenutbildning/**",
            "https://regionvasterbotten.se/vard/**",
        ]);

        scope.Check("https://www.umea.se/forskolaskolaochutbildning/vuxenutbildning").ShouldBe(ScopeDecision.In);
        scope.Check("https://www.umea.se/forskolaskolaochutbildning/vuxenutbildning/komvux").ShouldBe(ScopeDecision.In);
        scope.Check("https://www.umea.se/omsorg").ShouldBe(ScopeDecision.Out(CrawlScope.NotIncluded));
        scope.Check("https://regionvasterbotten.se/vard/akut").ShouldBe(ScopeDecision.OffSiteLeaf);
    }

    [Fact]
    public void A_non_empty_include_narrows_the_scope_hosts_and_exclude_wins()
    {
        var scope = Scope(include: ["www.umea.se/skola/**"], exclude: ["www.umea.se/skola/arkiv/**"], extraHosts: ["kommun.umea.se"]);

        scope.Check("https://www.umea.se/skola/a").ShouldBe(ScopeDecision.In);
        scope.Check("https://www.umea.se/omsorg").ShouldBe(ScopeDecision.Out(CrawlScope.NotIncluded));
        scope.Check("https://www.umea.se/skola/arkiv/2001").ShouldBe(ScopeDecision.Out(CrawlScope.Excluded));
        scope.Hosts.ShouldBe(["www.umea.se", "kommun.umea.se"], ignoreOrder: true);
        scope.Check("https://kommun.umea.se/skola/a").ShouldBe(ScopeDecision.Out(CrawlScope.NotIncluded));
    }

    [Theory]
    [InlineData("https://www.umea.se/a/b/a/b/a/b", TrapDetector.EndlessPath)]
    [InlineData("https://www.umea.se/x/x/x", TrapDetector.EndlessPath)]
    [InlineData("https://www.umea.se/1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16", TrapDetector.EndlessPath)]
    [InlineData("https://www.umea.se/s?a=1&b=2&c=3&d=4&e=5&f=6&g=7", TrapDetector.QueryVariants)]
    [InlineData("https://www.umea.se/kalender/2019", TrapDetector.Calendar)]
    [InlineData("https://www.umea.se/kalender/2031-01", TrapDetector.Calendar)]
    [InlineData("https://www.umea.se/kalender?datum=1999-12-31", TrapDetector.Calendar)]
    [InlineData("https://www.umea.se/kalender/2024", null)]
    [InlineData("https://www.umea.se/a/b/a/b", null)]
    [InlineData("https://www.umea.se/s?a=1&b=2&c=3&d=4&e=5&f=6", null)]
    [InlineData("https://www.umea.se/4.88db09b1724ee319ca45d.html", null)]
    public void Detects_url_space_traps(string url, string? trap)
    {
        new TrapDetector(new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero))).Check(new Uri(url)).ShouldBe(trap);
    }

    [Fact]
    public void Stops_query_variants_after_50_per_path_and_dated_urls_after_100_per_prefix()
    {
        var traps = new TrapDetector(new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero)));
        for (var i = 0; i < TrapDetector.MaxQueryVariantsPerPath; i++)
        {
            var url = new Uri($"https://www.umea.se/lista?sida={i}");
            traps.Check(url).ShouldBeNull();
            traps.Admitted(url);
        }

        traps.Check(new Uri("https://www.umea.se/lista?sida=999")).ShouldBe(TrapDetector.QueryVariants);
        traps.Check(new Uri("https://www.umea.se/annan?sida=1")).ShouldBeNull();
        traps.SessionParameterCandidates.ShouldBe(["sida"]);

        var day = new DateOnly(2026, 1, 1);
        for (var i = 0; i < TrapDetector.MaxDatedUrlsPerPrefix; i++)
        {
            var url = new Uri($"https://www.umea.se/kalender/{day.AddDays(i):yyyy-MM-dd}");
            traps.Check(url).ShouldBeNull();
            traps.Admitted(url);
        }

        traps.Check(new Uri("https://www.umea.se/kalender/2026-06-01")).ShouldBe(TrapDetector.Calendar);
    }

    [Fact]
    public void Expands_a_content_hash_seen_on_20_urls_only_once_more()
    {
        var traps = new TrapDetector(TimeProvider.System);
        Enumerable.Range(0, 19).Select(_ => traps.ShouldExpand("soft404")).ShouldAllBe(e => e);
        traps.ShouldExpand("soft404").ShouldBeFalse();
        traps.ShouldExpand("other").ShouldBeTrue();
    }

    [Fact]
    public void Extracts_links_against_the_base_with_the_canonical_and_skips_other_schemes()
    {
        var html = """
            <html><head><base href="https://www.umea.se/sv/"><link rel="canonical" href="/sv/start"></head><body>
            <a href="skola">Skola</a><a href="/omsorg#top">Omsorg</a><a href="mailto:a@umea.se">Mail</a><a href="javascript:void(0)">x</a>
            <a href="#anchor">a</a><a href="https://other.example/?utm_source=x">ext</a><a href="skola">dup</a><area href="/karta">
            </body></html>
            """;
        using var document = new HtmlParser().ParseDocument(html);

        var links = LinkExtractor.Extract(document, new Uri("https://www.umea.se/page"), null, "UmeaKommunBot");

        links.Links.ShouldBe(["https://www.umea.se/sv/start", "https://www.umea.se/sv/skola", "https://www.umea.se/omsorg", "https://other.example/", "https://www.umea.se/karta"]);
        links.NoIndex.ShouldBeFalse();
        links.NoFollow.ShouldBeFalse();
    }

    [Theory]
    [InlineData("<meta name=\"robots\" content=\"noindex\">", null, true, false)]
    [InlineData("<meta name=\"ROBOTS\" content=\"nofollow, noarchive\">", null, false, true)]
    [InlineData("<meta name=\"UmeaKommunBot\" content=\"none\">", null, true, true)]
    [InlineData("<meta name=\"googlebot\" content=\"noindex\">", null, false, false)]
    [InlineData("", "noindex, nofollow", true, true)]
    [InlineData("", "UmeaKommunBot: noindex", true, false)]
    [InlineData("", "googlebot: noindex, nofollow", false, false)]
    [InlineData("", "unavailable_after: 25 Jun 2010 15:00:00 PST", false, false)]
    public void Reads_meta_robots_and_x_robots_tag_for_us(string meta, string? header, bool noIndex, bool noFollow)
    {
        using var document = new HtmlParser().ParseDocument($"<html><head>{meta}</head><body></body></html>");

        var links = LinkExtractor.Extract(document, new Uri("https://www.umea.se/"), header, "UmeaKommunBot");

        (links.NoIndex, links.NoFollow).ShouldBe((noIndex, noFollow));
    }

    [Theory]
    [InlineData("<meta name=\"ExampleBot\" content=\"noindex\">", null, true)]
    [InlineData("<meta name=\"UmeaKommunBot\" content=\"noindex\">", null, false)]
    [InlineData("", "examplebot: noindex", true)]
    [InlineData("", "UmeaKommunBot: noindex", false)]
    public void Directives_follow_the_configured_product_token(string meta, string? header, bool noIndex)
    {
        using var document = new HtmlParser().ParseDocument($"<html><head>{meta}</head><body></body></html>");

        LinkExtractor.Extract(document, new Uri("https://www.example.org/"), header, "ExampleBot").NoIndex.ShouldBe(noIndex);
    }

    [Theory]
    [InlineData("2026-11-07T21:30:00Z", true)]
    [InlineData("2026-11-07T20:30:00Z", false)]
    [InlineData("2026-11-08T04:59:00Z", true)]
    [InlineData("2026-11-08T05:00:00Z", false)]
    [InlineData("2026-07-07T20:30:00Z", true)]
    public void The_night_window_is_22_to_06_stockholm_time(string utc, bool open)
    {
        CrawlWindow.Parse("22:00-06:00").IsOpen(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(open);
    }

    [Fact]
    public void The_next_opening_is_tonight_at_22_and_equal_times_are_always_open()
    {
        var window = CrawlWindow.Parse("22:00-06:00");
        var noon = new DateTimeOffset(2026, 11, 7, 11, 0, 0, TimeSpan.Zero);

        window.NextOpening(noon).ShouldBe(new DateTimeOffset(2026, 11, 7, 21, 0, 0, TimeSpan.Zero));
        CrawlWindow.Parse("00:00-00:00").IsOpen(noon).ShouldBeTrue();
        Should.Throw<FormatException>(() => CrawlWindow.Parse("22-06"));
    }
}
