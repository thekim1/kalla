using System.Text;
using Kalla.Worker.Connectors.Robots;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>robots.txt against the examples of RFC 9309 (§2.2, §5) with our product token, plus the parse limits.</summary>
public sealed class RobotsTxtTests
{
    /// <summary>RFC 9309 §5.1, with each named crawler replaced by ours in turn.</summary>
    private const string Rfc9309Example = """
        User-Agent: *
        Disallow: *.gif$
        Disallow: /example/
        Allow: /publications/

        User-Agent: foobot
        Disallow:/
        Allow:/example/page.html
        Allow:/example/allowed.gif

        User-Agent: barbot
        User-Agent: bazbot
        Disallow: /example/page.html

        User-Agent: quxbot

        EOF
        """;

    [Theory]
    [InlineData("/", true)]
    [InlineData("/example/", false)]
    [InlineData("/example/page.html", false)]
    [InlineData("/publications/", true)]
    [InlineData("/a/b.gif", false)]
    [InlineData("/a/b.gif?x=1", true)]
    public void Without_a_group_for_us_the_star_group_applies(string path, bool allowed) =>
        Parse(Rfc9309Example).IsAllowed(Url(path)).ShouldBe(allowed);

    [Theory]
    [InlineData("/", false)]
    [InlineData("/example/page.html", true)]
    [InlineData("/example/allowed.gif", true)]
    [InlineData("/example/other.html", false)]
    // Our group replaces "*": its *.gif rule does not apply.
    [InlineData("/publications/", false)]
    public void Our_own_group_replaces_the_star_group(string path, bool allowed) =>
        Parse(Rfc9309Example.Replace("foobot", "UmeaKommunBot", StringComparison.Ordinal)).IsAllowed(Url(path)).ShouldBe(allowed);

    [Theory]
    [InlineData("/example/page.html", false)]
    [InlineData("/example/", true)]
    public void Consecutive_user_agent_lines_share_one_group(string path, bool allowed) =>
        Parse(Rfc9309Example.Replace("bazbot", "UmeaKommunBot", StringComparison.Ordinal)).IsAllowed(Url(path)).ShouldBe(allowed);

    [Fact]
    public void A_group_for_us_without_rules_allows_everything() =>
        Parse(Rfc9309Example.Replace("quxbot", "UmeaKommunBot", StringComparison.Ordinal)).IsAllowed(Url("/example/")).ShouldBeTrue();

    [Theory]
    [InlineData("UmeaKommunBot")]
    [InlineData("umeakommunbot")]
    [InlineData("UmeaKommunBot/1.0")]
    public void The_product_token_matches_case_insensitively(string agent) =>
        Parse($"User-agent: {agent}\nDisallow: /\n\nUser-agent: *\nAllow: /\n").IsAllowed(Url("/a")).ShouldBeFalse();

    [Fact]
    public void A_longer_name_is_not_ours() =>
        Parse("User-agent: UmeaKommunBotX\nDisallow: /\n").IsAllowed(Url("/a")).ShouldBeTrue();

    [Fact]
    public void Groups_naming_us_are_combined()
    {
        var robots = Parse("User-agent: UmeaKommunBot\nDisallow: /a\n\nUser-agent: *\nDisallow: /\n\nUser-agent: UmeaKommunBot\nDisallow: /b\n");

        robots.IsAllowed(Url("/a")).ShouldBeFalse();
        robots.IsAllowed(Url("/b")).ShouldBeFalse();
        robots.IsAllowed(Url("/c")).ShouldBeTrue();
    }

    [Theory]
    // RFC 9309 §5.2: the longest match wins.
    [InlineData("/example/page/", true)]
    [InlineData("/example/page/disallowed.gif", false)]
    public void The_longest_match_wins(string path, bool allowed) =>
        Parse("User-Agent: UmeaKommunBot\nAllow : /example/page/\nDisallow : /example/page/disallowed.gif\n").IsAllowed(Url(path)).ShouldBe(allowed);

    [Fact]
    public void Allow_wins_a_tie() =>
        Parse("User-agent: *\nDisallow: /page\nAllow: /page\n").IsAllowed(Url("/page")).ShouldBeTrue();

    [Theory]
    [InlineData("Disallow: /*.php$", "/index.php", false)]
    [InlineData("Disallow: /*.php$", "/index.php?x=1", true)]
    [InlineData("Disallow: /*.php", "/index.php?x=1", false)]
    [InlineData("Disallow: /fish*", "/fishheads/yummy.html", false)]
    [InlineData("Disallow: /fish*", "/Fish.asp", true)]
    [InlineData("Disallow: /*/sok", "/a/b/sok", false)]
    [InlineData("Disallow: /a$b", "/a$b", false)]
    [InlineData("Disallow: /***x", "/abcx", false)]
    // RFC 9309 §2.2.3: %2A is a literal "*", and non-ASCII patterns compare in their UTF-8 escapes.
    [InlineData("Disallow: /path/file-with-a-%2A.html", "/path/file-with-a-%2A.html", false)]
    [InlineData("Disallow: /path/file-with-a-%2A.html", "/path/file-with-a-x.html", true)]
    [InlineData("Disallow: /foo/bar/ツ", "/foo/bar/%E3%83%84", false)]
    [InlineData("Disallow: /foo/bar/%e3%83%84", "/foo/bar/ツ", false)]
    [InlineData("Disallow: /%7Ejoe/", "/~joe/index.html", false)]
    public void Wildcards_anchors_and_encoding(string rule, string path, bool allowed) =>
        Parse($"User-agent: *\n{rule}\n").IsAllowed(Url(path)).ShouldBe(allowed);

    [Fact]
    public void Robots_txt_itself_is_always_allowed() =>
        Parse("User-agent: *\nDisallow: /\n").IsAllowed(Url("/robots.txt")).ShouldBeTrue();

    [Fact]
    public void An_empty_disallow_and_comments_change_nothing() =>
        Parse("﻿# a comment\nUser-agent: * # everyone\nDisallow: # nothing\n\nnonsense line\n").IsAllowed(Url("/a")).ShouldBeTrue();

    [Fact]
    public void Missing_and_unreachable_files_allow_and_disallow_everything()
    {
        RobotsTxt.AllowAll.IsAllowed(Url("/a")).ShouldBeTrue();
        RobotsTxt.DisallowAll.IsAllowed(Url("/a")).ShouldBeFalse();
        RobotsTxt.DisallowAll.IsAllowed(Url("/robots.txt")).ShouldBeTrue();
    }

    [Fact]
    public void Reads_crawl_delay_of_the_applying_group_and_every_sitemap_line()
    {
        var robots = Parse("""
            Sitemap: https://www.umea.se/sitemapindex.xml
            User-agent: *
            Crawl-delay: 30
            User-agent: UmeaKommunBot
            Crawl-delay: 2.5
            Disallow: /x
            Sitemap: https://www.umea.se/other.xml
            """);

        robots.CrawlDelaySeconds.ShouldBe(2.5);
        robots.Sitemaps.ShouldBe(["https://www.umea.se/sitemapindex.xml", "https://www.umea.se/other.xml"]);
    }

    [Fact]
    public void Only_the_first_500_KiB_are_parsed()
    {
        var padding = new StringBuilder("User-agent: *\nDisallow: /early\n");
        while (padding.Length < RobotsTxt.MaxBytes)
        {
            padding.Append("# padding padding padding padding padding padding\n");
        }

        padding.Append("Disallow: /late\n");
        var robots = Parse(padding.ToString());

        robots.IsAllowed(Url("/early")).ShouldBeFalse();
        robots.IsAllowed(Url("/late")).ShouldBeTrue();
    }

    [Fact]
    public void Groups_are_matched_against_the_configured_product_token()
    {
        const string Text = "User-agent: ExampleBot\nDisallow: /a\n\nUser-agent: UmeaKommunBot\nDisallow: /b\n";

        var robots = Parse(Text, "ExampleBot");

        robots.IsAllowed(Url("/a")).ShouldBeFalse();
        robots.IsAllowed(Url("/b")).ShouldBeTrue();
    }

    private static RobotsTxt Parse(string text, string productToken = "UmeaKommunBot") =>
        RobotsTxt.Parse(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)), productToken);

    private static Uri Url(string path) => new("https://www.umea.se" + path);
}
