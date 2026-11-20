using System.Text.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Commands;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Urls;
using Kalla.Worker.Engine;
using SiteProfile = Kalla.Pipelines.SiteProfiles.SiteProfile;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// The settings-change effects without a database (connectors design §12.3, §12.4): the tag comparison, the crawl's
/// <c>scope-narrowed</c> and <c>identity-changed</c> candidates and the lowered depth, a URL list's <c>identity-changed</c>
/// and <c>removed-from-list</c>, and which site-profile changes a crawl must re-sync for.
/// </summary>
public sealed class SettingsCandidatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly Guid Earlier = Guid.NewGuid();
    private static readonly TraversalVerdict Complete = new(true, [], null, null);
    private static readonly TraversalVerdict Partial = new(false, ["crawl.limit_pages"], null, null);

    [Fact]
    public void Only_scope_identity_and_fetch_changes_need_a_run_and_unknown_hashes_need_nothing()
    {
        var before = Hashes(new { startUrls = new[] { "https://www.umea.se/" } });

        SettingsEffects.Compare(before, Hashes(new { startUrls = new[] { "https://www.umea.se/" }, noiseSelectors = new[] { ".x" }, crawlWindow = "20:00-04:00" }))
            .ShouldBe(SettingsChange.None);
        SettingsEffects.Compare(before, Hashes(new { startUrls = new[] { "https://www.umea.se/" }, exclude = new[] { "**/sok?*" } }))
            .ShouldBe(new SettingsChange(false, true));
        SettingsEffects.Compare(before, Hashes(new { startUrls = new[] { "https://www.umea.se/" }, maxPages = 5 })).NeedsRun.ShouldBeTrue();
        SettingsEffects.Compare(before, Hashes(new { startUrls = new[] { "https://www.umea.se/" }, ignoreQueryParams = new[] { "lang" } }))
            .ShouldBe(new SettingsChange(true, false));
        SettingsEffects.Compare(null, before).ShouldBe(SettingsChange.None);
        var browser = SettingsEffects.Compare(before, Hashes(new { startUrls = new[] { "https://www.umea.se/" }, fetchMode = "browser" }));
        browser.ShouldBe(new SettingsChange(false, false, Fetch: true));
        browser.NeedsRun.ShouldBeTrue("a fetchMode change starts a sync that fetches every page again (§12.3)");
        SettingsEffects.Compare(before, Hashes(new { startUrls = new[] { "https://www.umea.se/" }, noiseSelectors = new[] { "#x" } })).NeedsRun
            .ShouldBeFalse("another behaviour change waits for the next sync");
        SettingsEffects.HashesJson(SourceTypes.Crawl, JsonSerializer.SerializeToElement(new { unknown = 1 })).ShouldBeNull();
    }

    [Fact]
    public void Documents_outside_the_narrowed_scope_are_exempt_scope_narrowed_candidates_in_any_run()
    {
        var source = Source(new CrawlSettings { StartUrls = ["https://www.umea.se/"], Exclude = ["**/arkiv/**"] });
        var kept = Doc("https://www.umea.se/bygga", reached: true);
        var archived = Enumerable.Range(0, 6).Select(i => Doc($"https://www.umea.se/arkiv/{i}", reached: false)).ToList();
        var offSite = Doc("https://www.example.org/sida", reached: false);
        var held = Doc("https://www.umea.se/arkiv/held", reached: false) with { HeldAt = Now, HeldBy = "admin" };
        List<SourceDocument> all = [kept, offSite, held, .. archived];

        var candidates = Candidates(all, Partial, source);

        candidates.Select(c => c.Reason).Distinct().ShouldBe([DeletionReasons.ScopeNarrowed]);
        candidates.Select(c => c.DocumentId).ShouldBe([offSite.Id, .. archived.Select(d => d.Id)], ignoreOrder: true);
        var decision = DeletionGuard.Evaluate(candidates, DeletionCandidates.Live(all), emptyListing: false, GuardSettings.Default);
        decision.Tripped.ShouldBeFalse("narrowing removals are exempt from the ratio");
        decision.Apply.Count.ShouldBe(7);
    }

    [Fact]
    public void A_stored_media_type_in_the_hosts_excluded_types_is_out_of_scope_even_without_an_extension()
    {
        var source = Source(new CrawlSettings { StartUrls = ["https://www.umea.se/"] });
        source = source with
        {
            Scope = source.Scope.WithExcludedTypes(new Dictionary<string, IReadOnlyList<string>> { ["www.umea.se"] = ["image/*", "application/pdf"] }),
        };
        var pdf = Doc("https://www.umea.se/download?id=1", reached: true);
        var page = Doc("https://www.umea.se/sida", reached: true);
        var types = new Dictionary<Guid, string> { [pdf.Id] = "application/pdf", [page.Id] = "text/html; charset=utf-8" };

        var candidates = SettingsCandidates.Crawl(Run, [pdf, page], Complete, new Dictionary<Guid, string>(), new HashSet<Guid>(), Now, source, types,
            new HashSet<Guid>(), identityChanged: false);

        candidates.ShouldBe([new DeletionCandidate(pdf.Id, DeletionReasons.ScopeNarrowed)]);
    }

    [Fact]
    public void An_identity_the_rules_no_longer_produce_or_an_identity_change_turns_not_seen_into_identity_changed()
    {
        var settings = new CrawlSettings { StartUrls = ["https://www.umea.se/"], IgnoreQueryParams = ["lang"] };
        var source = Source(settings);
        var stale = Doc("https://www.umea.se/sida?lang=sv", reached: false);
        var gone = Doc("https://www.umea.se/borta", reached: false);
        var seen = Doc("https://www.umea.se/sida", reached: true);

        Candidates([stale, gone, seen], Complete, source).ShouldBe(
            [new DeletionCandidate(stale.Id, DeletionReasons.IdentityChanged), new DeletionCandidate(gone.Id, DeletionReasons.NotSeen)], ignoreOrder: true);
        Candidates([stale, gone, seen], Complete, source, identityChanged: true).Select(c => c.Reason).Distinct()
            .ShouldBe([DeletionReasons.IdentityChanged]);
        Candidates([stale, gone, seen], Partial, source).ShouldBeEmpty("only a complete traversal knows what it did not find");
    }

    [Fact]
    public void A_url_list_identity_the_rules_no_longer_produce_or_an_identity_change_is_identity_changed_else_removed_from_list()
    {
        var options = new CanonicalizerOptions { IgnoreQueryParams = ["lang"] };
        var stale = Doc("https://www.umea.se/sida?lang=sv", reached: false);
        var dropped = Doc("https://www.umea.se/borta", reached: false);
        var listed = Doc("https://www.umea.se/sida", reached: true);
        var held = Doc("https://www.umea.se/spärrad", reached: false) with { HeldAt = Now, HeldBy = "admin" };
        var identities = new HashSet<string>(StringComparer.Ordinal) { listed.CanonicalIdentity };

        SettingsCandidates.UrlList([stale, dropped, listed, held], identities, options, identityChanged: false).ShouldBe(
            [new DeletionCandidate(stale.Id, DeletionReasons.IdentityChanged), new DeletionCandidate(dropped.Id, DeletionReasons.RemovedFromList)],
            ignoreOrder: true);
        SettingsCandidates.UrlList([stale, dropped, listed, held], identities, options, identityChanged: true).ShouldBe(
            [new DeletionCandidate(stale.Id, DeletionReasons.IdentityChanged), new DeletionCandidate(dropped.Id, DeletionReasons.IdentityChanged)],
            ignoreOrder: true);
    }

    [Fact]
    public void A_document_last_reached_beyond_a_lowered_max_depth_is_no_candidate()
    {
        var source = Source(new CrawlSettings { StartUrls = ["https://www.umea.se/"], MaxDepth = 1 });
        var deep = Doc("https://www.umea.se/a/b/c", reached: false);
        var near = Doc("https://www.umea.se/a", reached: false);

        var candidates = SettingsCandidates.Crawl(Run, [deep, near], Complete, new Dictionary<Guid, string>(), new HashSet<Guid>(), Now, source,
            new Dictionary<Guid, string>(), new HashSet<Guid> { deep.Id }, identityChanged: false);

        candidates.ShouldBe([new DeletionCandidate(near.Id, DeletionReasons.NotSeen)]);
    }

    [Fact]
    public void A_site_profile_change_re_syncs_a_crawl_only_for_excluded_types_or_identity()
    {
        var profile = new SiteProfile { MainRoot = "main" };

        SiteProfileFollowUp.ChangesCrawl(null, profile).ShouldBeFalse("the default excludedTypes, no identity rules");
        SiteProfileFollowUp.ChangesCrawl(profile, profile with { MainRoot = "article", LastmodReliable = true }).ShouldBeFalse();
        SiteProfileFollowUp.ChangesCrawl(profile, profile with { ExcludedTypes = ["VIDEO/*", "image/*"] }).ShouldBeFalse("a set, case-insensitive");
        SiteProfileFollowUp.ChangesCrawl(profile, profile with { ExcludedTypes = ["image/*", "video/*", "application/pdf"] }).ShouldBeTrue();
        SiteProfileFollowUp.ChangesCrawl(profile, profile with { ExcludedTypes = ["image/*"] }).ShouldBeTrue("widening is a scope change too");
        SiteProfileFollowUp.ChangesCrawl(profile, profile with { IgnoreQueryParams = ["sv.target"] }).ShouldBeTrue();
        SiteProfileFollowUp.ChangesCrawl(profile, profile with { IdentityRule = new() { Pattern = "^/(?<id>\\d+)$", Replacement = "/${id}" } }).ShouldBeTrue();
    }

    private static SettingsHashes Hashes(object settings) => SourceSettings.Hashes(SourceTypes.Crawl, JsonSerializer.SerializeToElement(settings));

    private static CrawlSource Source(CrawlSettings settings) =>
        new(settings, CrawlScope.Create(settings),
            new CanonicalizerOptions { IgnoreQueryParams = settings.IgnoreQueryParams, DirectoryIndexNames = settings.DirectoryIndexNames });

    private static IReadOnlyList<DeletionCandidate> Candidates(IReadOnlyList<SourceDocument> all, TraversalVerdict verdict, CrawlSource source,
        bool identityChanged = false) =>
        SettingsCandidates.Crawl(Run, all, verdict, new Dictionary<Guid, string>(), new HashSet<Guid>(), Now, source, new Dictionary<Guid, string>(),
            new HashSet<Guid>(), identityChanged);

    private static SourceDocument Doc(string url, bool reached) => new()
    {
        Id = Guid.NewGuid(),
        CanonicalIdentity = UrlCanonicalizer.Canonicalize(url).Value ?? url,
        LastRunId = reached ? Run : Earlier,
        LastSeenAt = reached ? Now : Now.AddDays(-1),
    };
}
