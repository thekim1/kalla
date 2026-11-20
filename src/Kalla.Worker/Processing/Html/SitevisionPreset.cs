using AngleSharp.Html.Dom;
using Kalla.Pipelines.SiteProfiles;

namespace Kalla.Worker.Processing.Html;

/// <summary>
/// The built-in Sitevision preset (connectors design §5.6; simplification pass §13.1), hand-checked against the saved
/// umea.se pages in <c>tests/fixtures/umea/</c>. Sitevision itself contributes the <c>sv-*</c> parts (the menu and
/// archive portlets); the <c>sol-*</c> parts come from the Soleil theme umea.se uses and match nothing on other themes.
/// A host's site profile (umea.se: <c>contracts/site-profiles/v1/www.umea.se.profile.json</c>) overrides the preset.
/// </summary>
public static class SitevisionPreset
{
    public const string Name = "sitevision";

    /// <summary>The preset's version: a change of any value below is a new version (and regenerated umea.se goldens).</summary>
    public const int Version = 1;

    public static SiteProfile Profile { get; } = new()
    {
        Cms = Name,
        // Landing pages ("genomgångssida") have no main: their first .pagecontent holds the page's own heading, the rest are
        // listings. The alternatives are tried in order.
        MainRoot = "main, .pagecontent",
        Boilerplate = new SiteProfileBoilerplate
        {
            // The toolbar holds "Sidan uppdaterades …" (read as the date before stripping), the share widget and the short-URL
            // line; forms are e-mail forms whose labels are not content.
            Selectors = [".sol-toolbar", "form"],
        },
        // Menu, archive (news list) and file-list portlets (the files are documents of their own), the theme's page listing
        // ("Undersidor") and the related-news column.
        LinkBlocks = [".sv-jcrmenu-portlet", ".sv-archive-portlet", ".sv-file-portlet", ".sol-page-listing", ".sol-related-news", ".sol-article-wrapper > .sol-left"],
        // Every crumb but the first ("Startsida"); the extractor drops the last when it repeats the title.
        Breadcrumb = "nav[aria-label=\"Brödsmulor\"] li:not(:first-child)",
        DatePattern = @"Sidan uppdaterades (?<d>\d{4}-\d{2}-\d{2})",
        IdentityRule = new SiteProfileIdentityRule
        {
            // The same mapping as UrlCanonicalizer's IdentityRule.SitevisionNodeId; a preset never verifies it for a host.
            Pattern = @"^/(?:.*/)?(?:[^/]+\.)?(?<id>[0-9]+\.[0-9a-f]{10,})\.html$",
            Replacement = "/${id}.html",
        },
    };

    public static HtmlPreset Preset { get; } = new(Name, Version, Profile);

    /// <summary>
    /// The fingerprint (connectors design §5.6 <c>cms</c>): at least two of Sitevision's markers, the <c>svid…</c> element ids,
    /// the <c>sv-layout</c>/<c>sv-portlet</c> classes and resources under <c>/sitevision/</c>.
    /// </summary>
    public static bool Recognises(IHtmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var markers = 0;
        markers += document.QuerySelector("[id^=svid]") is not null ? 1 : 0;
        markers += document.QuerySelector(".sv-layout, .sv-portlet") is not null ? 1 : 0;
        markers += document.QuerySelector("script[src*='/sitevision/'], link[href*='/sitevision/']") is not null ? 1 : 0;
        return markers >= 2;
    }
}
