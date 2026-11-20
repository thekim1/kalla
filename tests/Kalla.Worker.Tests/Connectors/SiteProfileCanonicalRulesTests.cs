using Kalla.Pipelines.SiteProfiles;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// A crawl's canonicaliser takes the host's applied site profile (connectors design §3.1 rules 7 and 10, §5.6): its
/// <c>ignoreQueryParams</c> with the trailing-<c>*</c> prefix rule, and its identity rule only when verified; other hosts
/// keep the source's rules. Also <c>.jfif</c> is an image and out of scope by default.
/// </summary>
public sealed class SiteProfileCanonicalRulesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The fields of <c>contracts/site-profiles/v1/www.umea.se.profile.json</c> that the canonicaliser reads.</summary>
    private static SiteProfile Umea(bool verified = true) => new()
    {
        Cms = "sitevision",
        MainRoot = "main, .pagecontent",
        IdentityRule = new SiteProfileIdentityRule
        {
            Pattern = @"^/(?:.*/)?(?:[^/]+\.)?(?<id>[0-9]+\.[0-9a-f]{10,})\.html$",
            Replacement = "/${id}.html",
            Verified = verified,
        },
        IgnoreQueryParams = ["sv.*", "open.*", "state", "profiling", "contactPage", "contactUserId", "addToCart"],
    };

    [Fact]
    public void The_umea_profile_maps_a_full_url_to_its_node_id_and_drops_its_parameters()
    {
        var options = new CanonicalizerOptions
        {
            IgnoreQueryParams = ["sort"],
            Hosts = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal) { ["www.umea.se"] = SiteProfileCanonicalRules.From(Umea())! },
        };

        UrlCanonicalizer.Canonicalize(
            "https://www.umea.se/kommunochpolitik/digitalomstallningochit.4.88db09b1724ee319ca45d.html?sv.target=12.abc&open.12.x=true&State=create&start=10&sort=a",
            options).Value.ShouldBe("https://www.umea.se/4.88db09b1724ee319ca45d.html?start=10");
        // Another host keeps its path and the profile's parameters; the source's own still go.
        UrlCanonicalizer.Canonicalize("https://other.se/a/b.4.88db09b1724ee319ca45d.html?state=1&sort=a", options).Value
            .ShouldBe("https://other.se/a/b.4.88db09b1724ee319ca45d.html?state=1");
    }

    [Fact]
    public void An_unverified_identity_rule_is_not_applied_but_the_parameters_are()
    {
        var rules = SiteProfileCanonicalRules.From(Umea(verified: false))!;
        rules.Identity.ShouldBeNull();
        var options = new CanonicalizerOptions { Hosts = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal) { ["www.umea.se"] = rules } };

        UrlCanonicalizer.Canonicalize("https://www.umea.se/a/b.4.88db09b1724ee319ca45d.html?sv.url=x&keep=1", options).Value
            .ShouldBe("https://www.umea.se/a/b.4.88db09b1724ee319ca45d.html?keep=1");
    }

    [Fact]
    public void A_profile_without_parameters_or_a_verified_rule_adds_nothing() =>
        SiteProfileCanonicalRules.From(new SiteProfile { MainRoot = "main" }).ShouldBeNull();

    [Fact]
    public async Task The_scope_hosts_get_the_rules_of_their_applied_profile_at_the_level()
    {
        var reader = new ProfileReader();
        reader.Profiles[("public", "www.umea.se")] = Umea();
        var rules = new SiteProfileCanonicalRules(reader, TimeProvider.System);

        var hosts = await rules.ForHostsAsync("public", ["www.umea.se", "other.se"], Ct);

        hosts.Canonical.Keys.ShouldBe(["www.umea.se"]);
        hosts.ExcludedTypes.Keys.ShouldBe(["www.umea.se"]);
        (await rules.ForHostsAsync("public", ["www.umea.se"], Ct)).Canonical["www.umea.se"].ShouldBeSameAs(hosts.Canonical["www.umea.se"],
            "the rules are built once per profile read, so the identity rule's expression is not compiled per fetch");
        var other = await rules.ForHostsAsync("internal", ["www.umea.se"], Ct);
        other.Canonical.ShouldBeEmpty("the profile belongs to its level");
        other.ExcludedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void An_identity_rule_matches_without_a_timeout_so_load_never_keeps_the_path()
    {
        // #471: a backtracking match of this expression runs far past the old 50 ms timeout, which kept the path, as a
        // descheduled thread or a GC pause under CPU load could for any rule; the non-backtracking engine maps it.
        var rule = PathIdentityRule.Create(@"^/(?:(?:a|aa)+x|a+)(?<id>y)\.html$", "/${id}.html")!;

        rule.Apply("/" + new string('a', 40) + "y.html").ShouldBe("/y.html");
        rule.Matches("/" + new string('a', 40) + "y.html").ShouldBeTrue();
        PathIdentityRule.Create(@"^/(?=a)(?<id>a)\.html$", "/${id}.html").ShouldBeNull("a construct the non-backtracking engine lacks is no rule, as an invalid expression");
    }

    [Fact]
    public void A_jfif_url_is_an_image_and_not_admitted() =>
        CrawlScope.Create(new CrawlSettings { StartUrls = ["https://www.umea.se/"] }).Check("https://www.umea.se/bilder/karta.jfif")
            .ShouldBe(ScopeDecision.Out(CrawlScope.ExcludedType));

    private sealed class ProfileReader : ISiteProfileReader
    {
        public Dictionary<(string, string), SiteProfile> Profiles { get; } = [];

        public Task<AppliedSiteProfile?> GetAppliedAsync(string classification, string host, CancellationToken cancellationToken) =>
            Task.FromResult(Profiles.TryGetValue((classification, host), out var profile) ? new AppliedSiteProfile(Guid.NewGuid(), 1, "sitevision", profile) : null);
    }
}
