using System.Globalization;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>URL normalisation rules v1 (connectors design §3.1), RFC 3986 §6 vectors, IDN and the Sitevision identity rule.</summary>
public sealed class UrlCanonicalizerTests
{
    [Theory]
    // Rule 1: scheme lower-case; http and https stay different identities.
    [InlineData("HTTPS://www.umea.se/", "https://www.umea.se/")]
    [InlineData("http://www.umea.se/", "http://www.umea.se/")]
    // Rule 2: host lower-case, trailing dot removed, IDN to A-label.
    [InlineData("https://WWW.Umea.SE./a", "https://www.umea.se/a")]
    [InlineData("https://Umeå.SE./", "https://xn--ume-wla.se/")]
    [InlineData("https://xn--ume-wla.se/", "https://xn--ume-wla.se/")]
    // Rule 3: default ports removed, others kept.
    [InlineData("https://www.umea.se:443/", "https://www.umea.se/")]
    [InlineData("http://www.umea.se:80/", "http://www.umea.se/")]
    [InlineData("https://www.umea.se:8443/", "https://www.umea.se:8443/")]
    [InlineData("http://www.umea.se:443/", "http://www.umea.se:443/")]
    [InlineData("https://www.umea.se:/", "https://www.umea.se/")]
    // Rule 4: empty path, dot segments (RFC 3986 §5.2.4), duplicate slashes kept.
    [InlineData("https://www.umea.se", "https://www.umea.se/")]
    [InlineData("https://www.umea.se/a/./b/../c", "https://www.umea.se/a/c")]
    [InlineData("https://www.umea.se/a/b/c/./../../g", "https://www.umea.se/a/g")]
    [InlineData("https://www.umea.se/../../a", "https://www.umea.se/a")]
    [InlineData("https://www.umea.se/a/..", "https://www.umea.se/")]
    [InlineData("https://www.umea.se/a//b", "https://www.umea.se/a//b")]
    [InlineData("https://www.umea.se/a/%2E%2E/b", "https://www.umea.se/b")]
    // Rule 5: unreserved escapes decoded, other hex upper-cased, non-ASCII as UTF-8, reserved escapes kept.
    [InlineData("https://www.umea.se/%7euser", "https://www.umea.se/~user")]
    [InlineData("https://www.umea.se/%41%62c", "https://www.umea.se/Abc")]
    [InlineData("https://www.umea.se/a%2fb", "https://www.umea.se/a%2Fb")]
    [InlineData("https://www.umea.se/a%3Fb", "https://www.umea.se/a%3Fb")]
    [InlineData("https://www.umea.se/förskola", "https://www.umea.se/f%C3%B6rskola")]
    [InlineData("https://www.umea.se/f%c3%b6rskola", "https://www.umea.se/f%C3%B6rskola")]
    [InlineData("https://www.umea.se/a b", "https://www.umea.se/a%20b")]
    [InlineData("https://www.umea.se/100%", "https://www.umea.se/100%25")]
    [InlineData("https://www.umea.se/?q=a b&r=ö", "https://www.umea.se/?q=a%20b&r=%C3%B6")]
    [InlineData("https://www.umea.se/😀", "https://www.umea.se/%F0%9F%98%80")]
    // Rule 6: fragment removed, also #!.
    [InlineData("https://www.umea.se/a#top", "https://www.umea.se/a")]
    [InlineData("https://www.umea.se/#!/route", "https://www.umea.se/")]
    // Rule 7: tracking parameters dropped, the rest sorted by name (stable for repeated names), empty ? dropped.
    [InlineData("https://www.umea.se/?b=2&utm_source=x&a=1", "https://www.umea.se/?a=1&b=2")]
    [InlineData("https://www.umea.se/?UTM_Medium=x&gclid=1&fbclid=2&_ga=3&mc_cid=4", "https://www.umea.se/")]
    [InlineData("https://www.umea.se/?ASPSESSIONIDQQ=1&PHPSESSID=2&x=1", "https://www.umea.se/?x=1")]
    [InlineData("https://www.umea.se/?b=2&a=z&b=1&a=y", "https://www.umea.se/?a=z&a=y&b=2&b=1")]
    [InlineData("https://www.umea.se/?", "https://www.umea.se/")]
    [InlineData("https://www.umea.se/?&&a=1&", "https://www.umea.se/?a=1")]
    [InlineData("https://www.umea.se/?q=a+b", "https://www.umea.se/?q=a+b")]
    [InlineData("https://www.umea.se/?q=%2F", "https://www.umea.se/?q=%2F")]
    // Rule 8: path session tokens.
    [InlineData("https://www.umea.se/p;jsessionid=AB12", "https://www.umea.se/p")]
    [InlineData("https://www.umea.se/p;JSESSIONID=AB12?x=1", "https://www.umea.se/p?x=1")]
    [InlineData("https://www.umea.se/(S(abcdefgh))/page.aspx", "https://www.umea.se/page.aspx")]
    [InlineData("https://www.umea.se/(A(xyz)S(abc))/page.aspx", "https://www.umea.se/page.aspx")]
    // Rule 9: trailing slash and index.html are left alone by default.
    [InlineData("https://www.umea.se/a/", "https://www.umea.se/a/")]
    [InlineData("https://www.umea.se/a", "https://www.umea.se/a")]
    [InlineData("https://www.umea.se/a/index.html", "https://www.umea.se/a/index.html")]
    // IPv4 literals in every inet_aton form are one identity; IPv6 is compressed and lower-case.
    [InlineData("http://2130706433/", "http://127.0.0.1/")]
    [InlineData("http://0x7f.1/", "http://127.0.0.1/")]
    [InlineData("http://0177.0.0.1/", "http://127.0.0.1/")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:8080/", "http://[::1]:8080/")]
    [InlineData("http://[FE80::1]/", "http://[fe80::1]/")]
    public void Applies_the_rules(string input, string expected) =>
        UrlCanonicalizer.Canonicalize(input).Value.ShouldBe(expected);

    [Theory]
    [InlineData("ftp://www.umea.se/", UrlCanonicalizer.RefusedScheme)]
    [InlineData("javascript:alert(1)", UrlCanonicalizer.RefusedScheme)]
    [InlineData("mailto:info@umea.se", UrlCanonicalizer.RefusedScheme)]
    [InlineData("https://user:pass@www.umea.se/", UrlCanonicalizer.RefusedUserInfo)]
    [InlineData("https://@www.umea.se/", UrlCanonicalizer.RefusedUserInfo)]
    [InlineData("https://www.umea.se:99999/", UrlCanonicalizer.RefusedPort)]
    [InlineData("https://www.umea.se:0/", UrlCanonicalizer.RefusedPort)]
    [InlineData("https://www.umea.se:8a/", UrlCanonicalizer.RefusedPort)]
    [InlineData("https:///path", UrlCanonicalizer.RefusedHost)]
    [InlineData("https://under_score.umea.se/", UrlCanonicalizer.RefusedHost)]
    [InlineData("https://www.ume%61.se/", UrlCanonicalizer.RefusedHost)]
    [InlineData("https://[::1/", UrlCanonicalizer.RefusedHost)]
    [InlineData("/relative/path", UrlCanonicalizer.RefusedSyntax)]
    public void Refuses_what_cannot_be_an_identity(string input, string reason)
    {
        var result = UrlCanonicalizer.Canonicalize(input);
        result.IsValid.ShouldBeFalse();
        result.Refusal.ShouldBe(reason);
    }

    [Fact]
    public void Drops_the_profiles_and_sources_ignore_parameters_case_insensitively()
    {
        var options = new CanonicalizerOptions { IgnoreQueryParams = ["sv.target", "SV.12.Portlet"] };

        UrlCanonicalizer.Canonicalize("https://www.umea.se/a?sv.target=12&sv.12.portlet=x&page=2", options).Value
            .ShouldBe("https://www.umea.se/a?page=2");
    }

    [Fact]
    public void A_trailing_star_in_the_ignore_parameters_is_a_prefix()
    {
        var options = new CanonicalizerOptions { IgnoreQueryParams = ["sv.*", "open.*"] };

        UrlCanonicalizer.Canonicalize("https://www.umea.se/a?sv.target=12&SV.12.abc.route=%2Fx&open.12.abc=true&svar=ja&page=2", options).Value
            .ShouldBe("https://www.umea.se/a?page=2&svar=ja");
    }

    [Fact]
    public void Drops_directory_index_names_only_when_the_source_opts_in()
    {
        var options = new CanonicalizerOptions { DirectoryIndexNames = ["index.html", "default.aspx"] };

        UrlCanonicalizer.Canonicalize("https://www.umea.se/a/index.html", options).Value.ShouldBe("https://www.umea.se/a/");
        UrlCanonicalizer.Canonicalize("https://www.umea.se/a/Default.aspx?x=1", options).Value.ShouldBe("https://www.umea.se/a/?x=1");
        UrlCanonicalizer.Canonicalize("https://www.umea.se/a/index.html.bak", options).Value.ShouldBe("https://www.umea.se/a/index.html.bak");
    }

    [Theory]
    [InlineData("https://www.umea.se/kommunochpolitik/sidor/forskola.4.88db09b1724ee319ca45d.html", "https://www.umea.se/4.88db09b1724ee319ca45d.html")]
    [InlineData("https://www.umea.se/4.88db09b1724ee319ca45d.html", "https://www.umea.se/4.88db09b1724ee319ca45d.html")]
    [InlineData("https://www.umea.se/a/sida.4.88db09b1724ee319ca45d.html?sv.x=1&b=2", "https://www.umea.se/4.88db09b1724ee319ca45d.html?b=2&sv.x=1")]
    [InlineData("https://www.umea.se/kommunochpolitik/sidor/forskola.html", "https://www.umea.se/kommunochpolitik/sidor/forskola.html")]
    [InlineData("https://www.umea.se/download/18.4c2.1234/file.pdf", "https://www.umea.se/download/18.4c2.1234/file.pdf")]
    public void Maps_Sitevision_pages_to_their_node_id_when_the_rule_is_verified(string input, string expected)
    {
        var options = new CanonicalizerOptions { IdentityRule = IdentityRule.SitevisionNodeId };

        UrlCanonicalizer.Canonicalize(input, options).Value.ShouldBe(expected);
    }

    [Fact]
    public void Without_the_verified_rule_a_Sitevision_path_keeps_its_own_identity() =>
        UrlCanonicalizer.Canonicalize("https://www.umea.se/sidor/forskola.4.88db09b1724ee319ca45d.html").Value
            .ShouldBe("https://www.umea.se/sidor/forskola.4.88db09b1724ee319ca45d.html");

    [Fact]
    public void Is_idempotent()
    {
        string[] inputs =
        [
            "HTTPS://Umeå.SE./a/./b/../c%7e?b=2&utm_source=x&a=1#top", "https://www.umea.se/a b/ö?q=a%2fb", "http://0x7f.1:8080/(S(x))/p;jsessionid=1",
        ];
        foreach (var input in inputs)
        {
            var once = UrlCanonicalizer.Canonicalize(input).Value!;
            UrlCanonicalizer.Canonicalize(once).Value.ShouldBe(once, input);
        }
    }

    [Theory]
    [InlineData("https://www.umea.se/a/b", "../c", "https://www.umea.se/c")]
    [InlineData("https://www.umea.se/a/b", "//other.umea.se/x#y", "https://other.umea.se/x")]
    [InlineData("https://www.umea.se/a/b", "?utm_source=x&q=1", "https://www.umea.se/a/b?q=1")]
    [InlineData("https://www.umea.se/a/b", "HTTP://WWW.UMEA.SE:80/%7e", "http://www.umea.se/~")]
    [InlineData("https://www.umea.se/a/b", " /trimmed ", "https://www.umea.se/trimmed")]
    public void Resolves_links_and_locations_before_canonicalising(string baseUrl, string reference, string expected) =>
        UrlCanonicalizer.Resolve(new Uri(baseUrl), reference).Value.ShouldBe(expected);

    [Fact]
    public void Identity_hash_is_sha256_of_the_canonical_string() =>
        Convert.ToHexStringLower(UrlCanonicalizer.IdentityHash("https://www.umea.se/"))
            .ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("https://www.umea.se/"u8)));

    /// <summary>
    /// to-verify #27: what .NET 10 does on its own, and why the rules do not lean on it. <see cref="Uri"/> keeps
    /// <c>%7e</c> as sent and keeps <c>%2F</c>; <see cref="IdnMapping"/> maps <c>ß</c> non-transitionally (UTS 46 as
    /// §3.1 rule 2 asks), rejects underscores with STD3 rules, and lower-cases nothing by itself in A-labels it returns.
    /// </summary>
    [Fact]
    public void Records_what_System_Uri_and_IdnMapping_do_in_dotnet_10()
    {
        var idn = new IdnMapping { UseStd3AsciiRules = true, AllowUnassigned = false };
        idn.GetAscii("faß.de").ShouldBe("xn--fa-hia.de");
        idn.GetAscii("umeå.se").ShouldBe("xn--ume-wla.se");
        Should.Throw<ArgumentException>(() => idn.GetAscii("under_score.se"));

        var uri = new Uri("https://Umeå.SE/%7e/a%2Fb/../c");
        uri.IdnHost.ShouldBe("xn--ume-wla.se");
        uri.AbsolutePath.ShouldBe("/~/c");
        new Uri("https://www.umea.se/a%2Fb/c").AbsolutePath.ShouldBe("/a%2Fb/c");
        UrlCanonicalizer.Canonicalize("https://Umeå.SE/%7e/a%2Fb/../c").Value.ShouldBe("https://xn--ume-wla.se/~/c");
    }
}
