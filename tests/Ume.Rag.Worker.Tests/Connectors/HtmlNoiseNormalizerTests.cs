using System.Text;
using Ume.Rag.Worker.Connectors.Changes;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>Noise rules v1 (connectors design §12.2): per-request noise gives equal hashes, a real change a different one.</summary>
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

    private static string Render(string n, string token, string today, string iso, string main = "") =>
        Page.Replace("{n}", n, StringComparison.Ordinal).Replace("{token}", token, StringComparison.Ordinal)
            .Replace("{today}", today, StringComparison.Ordinal).Replace("{iso}", iso, StringComparison.Ordinal)
            .Replace("{main}", main, StringComparison.Ordinal);

    private static async Task<string> HashAsync(string html, string[]? selectors = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        return await HtmlNoiseNormalizer.HashAsync(stream, FetchDay, selectors ?? [], TestContext.Current.CancellationToken);
    }
}
