using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using Kalla.Worker.Connectors.Changes;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>Noise rules v2 (connectors design §12.2): per-request noise gives equal hashes, a real change a different one.</summary>
public sealed class HtmlNoiseNormalizerTests
{
    private static readonly DateOnly FetchDay = new(2026, 10, 29);

    private const string Page = """
        <!DOCTYPE html>
        <html><head>
          <meta name="csrf-token" content="{token}">
          <meta name="description" content="Förskola i Umeå">
          <link rel="stylesheet" href="/style.css?v={n}" integrity="sha384-{n}">
          <script nonce="{n}" src="/app.js?v={n}"></script>
          <script>window.state = {n};</script>
          <script type="application/ld+json" nonce="{n}">{"@type":"WebPage","name":"Förskola"}</script>
          <style>.x{color:red}</style>
          <!-- rendered {n} -->
        </head>
        <body>
          <header>Idag är det {today}. Senast hämtad {iso}.</header>
          <div id="cookie-banner">Vi använder kakor {n}</div>
          <form><input type="hidden" name="__VIEWSTATE" value="{token}"><input type="text" name="q" value="sök"></form>
          <main><h1>Förskola</h1><p>Ansök om plats i förskola.</p>{main}</main>
          <img src="/logo.png?cb={n}" data-csrf-token="{token}" alt="Umeå kommun">
          <template><p>{n}</p></template>
        </body></html>
        """;

    [Fact]
    public async Task Per_request_noise_gives_equal_hashes()
    {
        var first = await HashAsync(Render(n: "1", token: "abc", today: "idag", iso: "2026-10-29"));
        var second = await HashAsync(Render(n: "2", token: "xyz", today: "29 oktober 2026", iso: "2026-10-29"), ["#cookie-banner"]);
        var third = await HashAsync(Render(n: "3   \n", token: "q", today: "IDAG", iso: "2026-10-29"), ["#cookie-banner"]);

        second.ShouldBe(await HashAsync(Render(n: "9", token: "r", today: "idag", iso: "2026-10-29"), ["#cookie-banner"]));
        third.ShouldBe(second);
        // Without the noise selector the cookie banner's counter is a change.
        first.ShouldNotBe(await HashAsync(Render(n: "2", token: "abc", today: "idag", iso: "2026-10-29")));
    }

    [Theory]
    [InlineData("<p>Ny paragraf.</p>")]
    [InlineData("<p>Senast uppdaterad 2026-10-29</p>")]
    public async Task A_changed_paragraph_inside_main_is_a_change(string extra)
    {
        var before = await HashAsync(Render(n: "1", token: "a", today: "idag", iso: "2026-10-29"));
        var after = await HashAsync(Render(n: "1", token: "a", today: "idag", iso: "2026-10-29", main: extra));

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Another_date_outside_main_is_a_change()
    {
        var before = await HashAsync(Render(n: "1", token: "a", today: "idag", iso: "2026-10-29"));
        var after = await HashAsync(Render(n: "1", token: "a", today: "idag", iso: "2026-10-28"));

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Structured_data_scripts_stay_significant()
    {
        var html = Render(n: "1", token: "a", today: "idag", iso: "2026-10-29");
        var changed = html.Replace("\"name\":\"Förskola\"", "\"name\":\"Fritids\"", StringComparison.Ordinal);

        (await HashAsync(changed)).ShouldNotBe(await HashAsync(html));
    }

    [Fact]
    public async Task A_visible_attribute_change_is_a_change()
    {
        var html = Render(n: "1", token: "a", today: "idag", iso: "2026-10-29");

        (await HashAsync(html.Replace("alt=\"Umeå kommun\"", "alt=\"Umeå\"", StringComparison.Ordinal))).ShouldNotBe(await HashAsync(html));
        (await HashAsync(html.Replace("value=\"sök\"", "value=\"hitta\"", StringComparison.Ordinal))).ShouldNotBe(await HashAsync(html));
    }

    [Fact]
    public async Task An_invalid_noise_selector_is_skipped() =>
        (await HashAsync(Render("1", "a", "idag", "2026-10-29"), ["[[[", "#cookie-banner"]))
            .ShouldBe(await HashAsync(Render("2", "b", "idag", "2026-10-29"), ["#cookie-banner"]));

    [Fact]
    public async Task Sitevision_ids_are_placeholders_that_stay_paired()
    {
        const string Section = """
            <section data-cid="{cid}" aria-labelledby="{a}"><h2 id="{a}">Kontakt</h2>
            <label for="{b}">Namn</label><input id="{b}" aria-describedby="{c} hint"><p id="{c}">Fyll i</p><p id="hint">Obligatoriskt</p></section>
            """;
        static string Form(string cid, string a, string b, string c) =>
            $"<html><body><main>{Section.Replace("{cid}", cid, StringComparison.Ordinal).Replace("{a}", a, StringComparison.Ordinal).Replace("{b}", b, StringComparison.Ordinal).Replace("{c}", c, StringComparison.Ordinal)}</main></body></html>";

        var first = Form("77dbb1cc-462d-515c-08ba-0a6e0ef2433e", "sv_-UX4r78YjLRO4GXm8I2y6", "sv_ysiczPPIj2eZoVmHPxfdz", "sv_M_8w7T8ml0zppQTEBXmOL");
        var second = Form("ac7df3ff-88c2-69ed-71ea-e1efc475ea09", "sv_uGknPJ4GSxCqm5ncB7ecE", "sv_g9heX83RUzuQ0yHLwZVdk", "sv_co2ZHNcVGHQDlcAdl2fXO");

        (await HashAsync(second)).ShouldBe(await HashAsync(first));
        (await HashAsync(second, rulesVersion: 1)).ShouldNotBe(await HashAsync(first, rulesVersion: 1));
        // The label now names the hint's paragraph instead of the input: the pairing is content, not noise.
        var repaired = first.Replace("<label for=\"sv_ysiczPPIj2eZoVmHPxfdz\">", "<label for=\"sv_M_8w7T8ml0zppQTEBXmOL\">", StringComparison.Ordinal);
        repaired.ShouldNotBe(first);
        (await HashAsync(repaired)).ShouldNotBe(await HashAsync(first));
        // An id that is not Sitevision's is kept.
        (await HashAsync(first.Replace("id=\"hint\"", "id=\"tips\"", StringComparison.Ordinal))).ShouldNotBe(await HashAsync(first));
    }

    [Theory]
    [MemberData(nameof(UmeaCorpusTests.HtmlPages), MemberType = typeof(UmeaCorpusTests))]
    public async Task A_second_umea_fetch_hashes_equal_under_v2_and_differently_under_v1(string file)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(UmeaCorpusTests.Pages, file), Encoding.UTF8, TestContext.Current.CancellationToken);
        var refetch = SitevisionRequestNoise.Redraw(html);

        refetch.Cids.ShouldBeGreaterThan(0, file);
        refetch.Ids.ShouldBeGreaterThan(0, file);
        refetch.Html.ShouldNotBe(html);
        (await HashAsync(refetch.Html)).ShouldBe(await HashAsync(html), file);
        (await HashAsync(refetch.Html, rulesVersion: 1)).ShouldNotBe(await HashAsync(html, rulesVersion: 1), file);
    }

    public static TheoryData<string, string, string> AcceptancePageChanges => new()
    {
        // A sentence of the main text.
        { "sentence", "Telefonsupport är öppet vardagar", "Telefonsupport är öppet helgfria vardagar" },
        // A link's target, its text unchanged.
        { "href", "href=\"/kommunochpolitik/kommunensorganisation/vibyggerumeatillsammans/digitalomstallning.4.1d6cfc7318e737fca3293c0.html\"", "href=\"/kommunochpolitik/kommunensorganisation/vibyggerumeatillsammans/digitalomstallning.4.1d6cfc7318e737fca3293c1.html\"" },
        // A list item added to the first list in main (the contact list).
        { "list item", "</ul>", "<li>Ny kontakt</li></ul>" },
    };

    [Theory]
    [MemberData(nameof(AcceptancePageChanges))]
    public async Task A_real_change_on_a_second_umea_fetch_changes_the_v2_hash(string change, string before, string after)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(UmeaCorpusTests.Pages, UmeaCorpusTests.AcceptancePage), Encoding.UTF8, TestContext.Current.CancellationToken);
        var main = html.IndexOf("<main", StringComparison.Ordinal);
        var at = html.IndexOf(before, main, StringComparison.Ordinal);
        at.ShouldBeInRange(main, html.IndexOf("</main>", StringComparison.Ordinal), change);
        var changed = SitevisionRequestNoise.Redraw(string.Concat(html.AsSpan(0, at), after, html.AsSpan(at + before.Length))).Html;

        (await HashAsync(changed)).ShouldNotBe(await HashAsync(html), change);
    }

    [Fact]
    public async Task A_hash_stored_under_v1_is_never_taken_as_unchanged()
    {
        // No Sitevision values: v1 and v2 normalise this page alike, yet the v2 hash differs from the stored v1 one.
        var html = Render(n: "1", token: "a", today: "idag", iso: "2026-10-29");
        var parser = new HtmlParser();
        using var v1Document = parser.ParseDocument(html);
        using var v2Document = parser.ParseDocument(html);
        var v1 = HtmlNoiseNormalizer.Normalize(v1Document, FetchDay, [], rulesVersion: 1);
        HtmlNoiseNormalizer.Normalize(v2Document, FetchDay, []).ShouldBe(v1);

        var storedV1 = await HashAsync(html, rulesVersion: 1);
        storedV1.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(v1))), "v1 hashes stay reproducible");
        HtmlNoiseNormalizer.RulesVersion.ShouldBe(2);
        (await HashAsync(html)).ShouldNotBe(storedV1);
        HtmlNoiseNormalizer.Hash(v1).ShouldNotBe(storedV1);
    }

    private static string Render(string n, string token, string today, string iso, string main = "") =>
        Page.Replace("{n}", n, StringComparison.Ordinal).Replace("{token}", token, StringComparison.Ordinal)
            .Replace("{today}", today, StringComparison.Ordinal).Replace("{iso}", iso, StringComparison.Ordinal)
            .Replace("{main}", main, StringComparison.Ordinal);

    private static async Task<string> HashAsync(string html, string[]? selectors = null, int rulesVersion = HtmlNoiseNormalizer.RulesVersion)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        return await HtmlNoiseNormalizer.HashAsync(stream, FetchDay, selectors ?? [], rulesVersion, TestContext.Current.CancellationToken);
    }
}
