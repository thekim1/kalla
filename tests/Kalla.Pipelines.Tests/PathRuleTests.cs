using System.Text.Json;
using Kalla.Pipelines.Processing;

namespace Kalla.Pipelines.Tests;

/// <summary>
/// The <c>pathRules</c> matcher (processing design §7, <i>One matcher</i>) that the worker's <c>chunk</c> stage and the
/// admin's Topics tab share.
/// </summary>
public sealed class PathRuleTests
{
    private const string Html = "text/html";
    private const string Pdf = "application/pdf";

    [Fact]
    public void A_match_gives_the_rules_valid_tags_its_document_type_and_its_date_from_the_named_groups()
    {
        var rules = PathRules.Read(JsonDocument.Parse("""
            [ { "match": "^/protokoll/(?<y>\\d{4})/", "on": "path-regex", "tags": ["Protokoll", "ogiltig tagg", "protokoll"], "documentType": "minutes", "date": "${y}-01-01" },
              { "match": "^Kallelse", "on": "title-regex", "tags": ["kallelse"] },
              { "match": "WWW.umea.se", "on": "host", "tags": ["umea"] },
              { "match": "application/pdf", "on": "mediaType", "tags": ["pdf"], "date": "${y}-01-01" },
              { "match": "(", "on": "path-regex", "tags": ["broken"] },
              { "match": "x", "on": "unknown", "tags": ["unknown"] } ]
            """).RootElement);
        var subject = new PathRuleSubject(Pdf) { Url = new Uri("https://www.umea.se/protokoll/2024/ks.pdf"), Title = "Kallelse KS" };

        PathRules.Match(rules[0], subject).ShouldBe(new PathRuleMatch(["protokoll"], "minutes", new DateOnly(2024, 1, 1)), new MatchComparer());
        PathRules.Match(rules[1], subject)!.Tags.ShouldBe(["kallelse"]);
        PathRules.Match(rules[2], subject)!.Tags.ShouldBe(["umea"]);
        PathRules.Match(rules[3], subject).ShouldBe(new PathRuleMatch(["pdf"], null, null), new MatchComparer(), "a group the rule doesn't have leaves no valid date");
        PathRules.Match(rules[4], subject).ShouldBeNull("an invalid regex matches nothing");
        PathRules.Match(rules[5], subject).ShouldBeNull();
        PathRules.Match(rules[0], new PathRuleSubject(Pdf) { Url = new Uri("https://www.umea.se/bygga/a.pdf") }).ShouldBeNull();
        PathRules.Match(rules[1], new PathRuleSubject(Pdf) { DisplayName = "Kallelse.pdf" }).ShouldBeNull("no title, no title match");
    }

    [Fact]
    public void Read_takes_an_array_and_anything_else_is_no_rules()
    {
        PathRules.Read(null).ShouldBeEmpty();
        PathRules.Read(JsonDocument.Parse("{}").RootElement).ShouldBeEmpty();
        PathRules.Read(JsonDocument.Parse("""[ { "match": "/a/**", "on": "path" } ]""").RootElement).Single().ShouldBe(new PathRule { Match = "/a/**", On = "path" }, new RuleComparer());
    }

    [Theory]
    [InlineData(" Bygglov ", "bygglov")]
    [InlineData("ÅTERVINNING", "återvinning")]
    [InlineData("e-tjänst", "e-tjänst")]
    [InlineData("två ord", null)]
    [InlineData("", null)]
    public void A_tag_is_normalised_or_refused(string tag, string? expected) => PathRules.NormaliseTag(tag).ShouldBe(expected);

    [Fact]
    public void An_upload_path_rule_matches_its_folders_with_or_without_a_leading_slash()
    {
        // admin design §5.5.7: a folder upload's display name is its relative path, and a topic rule on the path tags its documents.
        var rules = PathRules.Read(System.Text.Json.JsonDocument.Parse("""
            [ { "match": "HR/**", "on": "path", "tags": ["hr"] },
              { "match": "/HR/Policys/*.pdf", "on": "path", "tags": ["policy"] },
              { "match": "Ekonomi/**", "on": "path", "tags": ["ekonomi"] } ]
            """).RootElement);
        Tags(rules, new PathRuleSubject(Pdf) { DisplayName = "HR/Policys/semester.pdf" }).ShouldBe(["hr", "policy"]);
        Tags(rules, new PathRuleSubject(Pdf) { DisplayName = "HR/Policys/Arkiv/gammal.pdf" }).ShouldBe(["hr"]);
        Tags(rules, new PathRuleSubject(Pdf) { DisplayName = "semester.pdf" }).ShouldBeEmpty();

        // A website path is matched as it is: HR/** does not match the URL path /HR/….
        Tags(rules, new PathRuleSubject(Pdf) { Url = new Uri("https://x.se/HR/Policys/semester.pdf") }).ShouldBe(["policy"]);
    }

    [Fact]
    public void A_website_path_rule_takes_a_pasted_address_or_a_host_led_glob_and_a_trailing_slash_means_everything_under_it()
    {
        // #929: an editor pastes the section's address from the browser; matched as the path alone it tagged nothing.
        var rules = PathRules.Read(System.Text.Json.JsonDocument.Parse("""
            [ { "match": "https://www.umea.se/bygga-bo-och-miljo/bygglov/?utm=x#top", "on": "path", "tags": ["bygglov"] },
              { "match": "WWW.Umea.se/kommun-och-politik/**", "on": "path", "tags": ["politik"] },
              { "match": "https://www.vakin.se/", "on": "path", "tags": ["vakin"] },
              { "match": "/utbildning/", "on": "path", "tags": ["utbildning"] } ]
            """).RootElement);
        Tags("https://www.umea.se/bygga-bo-och-miljo/bygglov/ansok.html").ShouldBe(["bygglov"]);
        Tags("https://www.umea.se/bygga-bo-och-miljo/bygglov/").ShouldBe(["bygglov"]);
        Tags("https://www.umea.se/bygga-bo-och-miljo/bygglov").ShouldBeEmpty("the section's own address without its slash is another path");
        Tags("https://umea.se/bygga-bo-och-miljo/bygglov/ansok.html").ShouldBeEmpty("the host is the rule's: www.umea.se");
        Tags("https://www.umea.se/kommun-och-politik/protokoll/2024/ks.pdf").ShouldBe(["politik"]);
        Tags("https://www.vakin.se/sortera/glas").ShouldBe(["vakin"]);
        Tags("https://www.umea.se/utbildning/forskola/plats.html").ShouldBe(["utbildning"]);
        Tags("https://www.vakin.se/utbildning/").ShouldBe(["vakin", "utbildning"]);

        // The forms the rule is reduced to; an upload's relative path is never read as a host.
        PathRules.PathGlob("https://www.umea.se/a/b.html?x=1", website: true).ShouldBe(("www.umea.se", "/a/b.html"));
        PathRules.PathGlob("http://xn--rebro-iua.se:8080/a/*", website: true).ShouldBe(("xn--rebro-iua.se", "/a/*"));
        PathRules.PathGlob("https://örebro.se/a/", website: true).ShouldBe(("xn--rebro-iua.se", "/a/**"));
        PathRules.PathGlob("umea.se", website: true).ShouldBe(("umea.se", "/**"));
        PathRules.PathGlob("/a/b?.html", website: true).ShouldBe((null, "/a/b?.html"));
        PathRules.PathGlob("HR/**", website: true).ShouldBe((null, "HR/**"));
        PathRules.PathGlob("protokoll.2024/**", website: false).ShouldBe((null, "protokoll.2024/**"));
        PathRules.PathGlob("HR/", website: false).ShouldBe((null, "HR/**"));

        IReadOnlyList<string> Tags(string url) => PathRuleTests.Tags(rules, new PathRuleSubject(Html) { Url = new Uri(url) });
    }

    [Fact]
    public void A_rules_pattern_is_compiled_once_and_an_invalid_one_still_matches_nothing()
    {
        // #1079: the chunk stage and the topic preview match the same rule against every document of a source.
        PathRules.Glob("/bygga/**").ShouldBeSameAs(PathRules.Glob("/bygga/**"));
        PathRules.Glob("/bygga/**").ShouldNotBeSameAs(PathRules.Glob("/bo/**"));

        var regex = new PathRule { Match = @"^/protokoll/(?<year>\d{4})/", On = "path-regex", Tags = ["protokoll"], Date = "${year}-01-01" };
        var first = new PathRuleSubject(Pdf) { Url = new Uri("https://www.umea.se/protokoll/2024/a.pdf") };
        var second = new PathRuleSubject(Pdf) { Url = new Uri("https://www.umea.se/protokoll/2025/b.pdf") };
        PathRules.Match(regex, first)!.Date.ShouldBe(new DateOnly(2024, 1, 1));
        PathRules.Match(regex, second)!.Date.ShouldBe(new DateOnly(2025, 1, 1));

        var invalid = new PathRule { Match = "(", On = "path-regex", Tags = ["x"] };
        PathRules.Match(invalid, first).ShouldBeNull();
        PathRules.Match(invalid, first).ShouldBeNull("an invalid pattern is not cached as a match");
    }

    /// <summary>Every matching rule's tags in rule order, each once, as the <c>chunk</c> stage collects them.</summary>
    internal static IReadOnlyList<string> Tags(IReadOnlyList<PathRule> rules, PathRuleSubject subject) =>
        [.. rules.Select(rule => PathRules.Match(rule, subject)).OfType<PathRuleMatch>().SelectMany(match => match.Tags).Distinct(StringComparer.Ordinal)];

    private sealed class MatchComparer : IEqualityComparer<PathRuleMatch?>
    {
        public bool Equals(PathRuleMatch? x, PathRuleMatch? y) =>
            x is null || y is null ? x == y : x.Tags.SequenceEqual(y.Tags) && x.DocumentType == y.DocumentType && x.Date == y.Date;

        public int GetHashCode(PathRuleMatch? obj) => 0;
    }

    private sealed class RuleComparer : IEqualityComparer<PathRule>
    {
        public bool Equals(PathRule? x, PathRule? y) =>
            x is null || y is null ? x == y : (x.Match, x.On, x.DocumentType, x.Date) == (y.Match, y.On, y.DocumentType, y.Date) && x.Tags.SequenceEqual(y.Tags);

        public int GetHashCode(PathRule obj) => 0;
    }
}
