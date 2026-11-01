using Ume.Rag.Pipelines.Sources;

namespace Ume.Rag.Pipelines.Tests;

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

    [Fact]
    public void An_invalid_regex_is_refused_on_parse() =>
        Should.Throw<ArgumentException>(() => UrlPattern.Parse("re:("));
}
