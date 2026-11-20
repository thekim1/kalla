using Kalla.Pipelines.Sources;

namespace Kalla.Pipelines.Tests;

/// <summary>Include/exclude globs and <c>re:</c> patterns over <c>host/path?query</c> (connectors design §5.2).</summary>
public sealed class UrlPatternTests
{
    [Theory]
    [InlineData("www.umea.se/**", "https://www.umea.se/", true)]
    [InlineData("www.umea.se/**", "https://www.umea.se/a/b?c=1", true)]
    [InlineData("www.umea.se/**", "https://umea.se/a", false)]
    // The host part is case-insensitive, the path case-sensitive.
    [InlineData("WWW.UMEA.SE/a/*", "https://www.umea.se/a/b", true)]
    [InlineData("www.umea.se/A/*", "https://www.umea.se/a/b", false)]
    // * stays within one segment, ** crosses them.
    [InlineData("www.umea.se/a/*", "https://www.umea.se/a/b/c", false)]
    [InlineData("www.umea.se/a/**", "https://www.umea.se/a/b/c", true)]
    [InlineData("*.umea.se/**", "https://skola.umea.se/x", true)]
    [InlineData("*.umea.se/**", "https://evil.example/skola.umea.se/x", false)]
    // The website template's default excludes.
    [InlineData("**/sok?*", "https://www.umea.se/sok?q=1", true)]
    [InlineData("**/sok?*", "https://www.umea.se/sokning", false)]
    [InlineData("**?*sort=*", "https://www.umea.se/lista?page=2&sort=name", true)]
    [InlineData("**/login*", "https://www.umea.se/a/login.aspx", true)]
    // Eneo passes the start URL as a regex, so "." and "?" match anything; here they are literal.
    [InlineData("www.umea.se/a.html", "https://wwwXumea.se/a.html", false)]
    [InlineData("www.umea.se/a.html", "https://www.umea.se/aXhtml", false)]
    [InlineData("www.umea.se/a?b", "https://www.umea.se/aXb", false)]
    [InlineData("www.umea.se/a?b", "https://www.umea.se/a?b", true)]
    [InlineData("re:^www\\.umea\\.se/(nyheter|press)/", "https://www.umea.se/nyheter/2026", true)]
    [InlineData("re:^www\\.umea\\.se/(nyheter|press)/", "https://www.umea.se/sok/nyheter/", false)]
    public void Matches_the_canonical_url_without_scheme(string pattern, string url, bool expected) =>
        UrlPattern.Parse(pattern).Matches(url).ShouldBe(expected);

    [Theory]
    [InlineData("www.umea.se/intranat/**")]
    [InlineData("re:^www\\.umea\\.se/intranat/")]
    public void Patterns_have_no_match_timeout_so_load_never_turns_an_exclude_into_no_match(string pattern)
    {
        var parsed = UrlPattern.Parse(pattern);

        parsed.Timeout.ShouldBe(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);
        parsed.Matches("https://www.umea.se/intranat/" + new string('a', 100_000)).ShouldBeTrue();
    }

    [Theory]
    // An address pasted from the browser: the scheme is dropped, so the glob still matches (the URL is matched without it).
    [InlineData("https://www.umea.se/vuxenutbildning/**", "https://www.umea.se/vuxenutbildning/komvux", true)]
    [InlineData("http://www.umea.se/vuxenutbildning/**", "https://www.umea.se/vuxenutbildning/komvux", true)]
    [InlineData("HTTPS://www.umea.se/vuxenutbildning", "https://www.umea.se/vuxenutbildning", true)]
    [InlineData("  https://www.umea.se/a/*  ", "https://www.umea.se/a/b", true)]
    [InlineData("https://www.umea.se/vuxenutbildning/**", "https://www.umea.se/omsorg", false)]
    public void A_leading_scheme_on_a_glob_is_ignored(string pattern, string url, bool expected) =>
        UrlPattern.Parse(pattern).Matches(url).ShouldBe(expected);

    [Theory]
    [InlineData("https://www.umea.se/a/**", "www.umea.se/a/**")]
    [InlineData("Http://www.umea.se", "www.umea.se")]
    [InlineData(" www.umea.se/** ", "www.umea.se/**")]
    [InlineData("re:^https://x", "re:^https://x")]
    public void Normalize_drops_the_scheme_of_a_glob_and_leaves_a_regex_alone(string pattern, string expected) =>
        UrlPattern.Normalize(pattern).ShouldBe(expected);

    [Fact]
    public void An_invalid_regex_is_refused_on_parse() =>
        Should.Throw<ArgumentException>(() => UrlPattern.Parse("re:("));
}
