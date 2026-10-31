using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;

namespace Ume.Rag.Worker.Processing.Html;

/// <summary>
/// A built-in preset (processing design §3.7): the site-profile fields used when a host has no profile (and for uploaded
/// HTML). <see cref="HtmlPresets.Generic"/> is the landmark fallback; the Sitevision preset is P1a item 8 and is added
/// by registering an <see cref="IHtmlPresetCatalog"/> that recognises it.
/// </summary>
public sealed record HtmlPreset(string Name, int Version, SiteProfile Profile);

/// <summary>The seam for CMS presets (item 8): picks the preset for a document without a site profile.</summary>
public interface IHtmlPresetCatalog
{
    HtmlPreset Select(IHtmlDocument document);
}

/// <summary>The presets this build knows: the generic landmark preset only (Sitevision comes with item 8).</summary>
public sealed class HtmlPresets : IHtmlPresetCatalog
{
    public static HtmlPreset Generic { get; } = new("generic", 1, new SiteProfile { MainRoot = "main, article, [role=main]" });

    public HtmlPreset Select(IHtmlDocument document) => Generic;
}

/// <summary>The site profile of the document's host, or null (then a preset applies).</summary>
public sealed record HtmlProfileVersion(string Host, int Version, SiteProfile Profile);

/// <summary>The result of HTML extraction: the parsed document plus the facts 6b's re-learning reads (§8).</summary>
public sealed record HtmlExtraction(ParsedDocument Document, string Outcome)
{
    public const string ByProfile = "profile";
    public const string ByPreset = "preset";
    public const string Fallback = "fallback";
    public const string EmptyRoot = "empty-root";
}

/// <summary>
/// HTML extraction by the host's site profile (processing design §3.7), generic over the <see cref="SiteProfile"/>
/// contract: (1) the main root by the profile's selector, else the landmarks <c>main</c>, <c>article</c>,
/// <c>role=main</c>, else <c>body</c>; (2) the profile's boilerplate selectors and phrases stripped inside the root;
/// (3) link-list blocks dropped (discovery only); (4) blocks <c>h1</c>–<c>h6</c>, <c>p</c>, <c>li</c> with nesting depth,
/// <c>table</c> (header rows, spans), <c>pre</c>/<c>code</c>, <c>figcaption</c>; (5) facts: breadcrumb (selector, else
/// <c>aria-label</c> Brödsmulor/breadcrumb, else <c>BreadcrumbList</c>; screen-reader-only text removed; the current page
/// dropped when it repeats the title), the visible date by the profile's pattern, description, keywords and title.
/// SmartReader as the last fallback is not in this build (the root falls back to <c>body</c>).
/// </summary>
public sealed partial class HtmlExtractor(IHtmlPresetCatalog presets)
{
    public const int Version = 1;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] Dropped = ["script", "style", "noscript", "template", "svg", "iframe", "object", "button", "select", "input", "textarea", "nav"];

    private static readonly string[] HiddenSelectors =
    [
        "[hidden]", "[aria-hidden=true]", ".sr-only", ".visually-hidden", ".visuallyhidden", ".screen-reader-text", ".sv-visuallyhidden", ".env-assistive-text",
    ];

    public HtmlExtraction Extract(ReadOnlyMemory<byte> html, HtmlProfileVersion? profile)
    {
        using var stream = new MemoryStream(html.ToArray(), writable: false);
        using var document = new HtmlParser().ParseDocument(stream);
        return Extract(document, profile);
    }

    public HtmlExtraction Extract(IHtmlDocument document, HtmlProfileVersion? profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        var preset = profile is null ? presets.Select(document) : null;
        var settings = profile?.Profile ?? preset!.Profile;
        var title = Title(document);
        var breadcrumb = Breadcrumb(document, settings, title);
        var date = VisibleDate(document, settings);

        var outcome = profile is not null ? HtmlExtraction.ByProfile : HtmlExtraction.ByPreset;
        var root = Select(document, settings.MainRoot);
        if (root is null)
        {
            outcome = profile is not null ? HtmlExtraction.EmptyRoot : HtmlExtraction.Fallback;
            root = Select(document, HtmlPresets.Generic.Profile.MainRoot) ?? document.Body;
        }

        var blocks = new List<DocBlock>();
        var tables = new List<DocTable>();
        var rootChars = 0;
        var keptChars = 0;
        if (root is not null)
        {
            var copy = (IElement)root.Clone(deep: true);
            rootChars = Normalize(copy.TextContent).Length;
            Strip(copy, settings);
            new BlockWalker(blocks, tables, settings.Boilerplate.Phrases).Walk(copy);
            keptChars = blocks.Sum(b => b.Text.Length);
        }

        var facts = new SortedDictionary<string, double>(StringComparer.Ordinal)
        {
            ["quality.boilerplateShare"] = rootChars == 0 ? 0 : Math.Round(Math.Max(0, rootChars - keptChars) / (double)rootChars, 4),
            ["quality.emptyRoot"] = outcome == HtmlExtraction.EmptyRoot ? 1 : 0,
        };
        var parsed = new ParsedDocument
        {
            Title = title ?? blocks.FirstOrDefault(b => b.Kind == BlockKinds.Heading && b.Level == 1)?.Text,
            Language = LanguageDetector.FromLangAttribute(document.DocumentElement.GetAttribute("lang")),
            Blocks = blocks,
            Tables = tables,
            Breadcrumb = breadcrumb,
            Description = Meta(document, "description") ?? Meta(document, "og:description"),
            Keywords = Meta(document, "keywords") is { } keywords
                ? [.. keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)]
                : [],
            VisibleDate = date,
            ParseRule = null,
            Strategy = StrategyString.Html(profile?.Host, profile?.Version, preset?.Name ?? HtmlPresets.Generic.Name, preset?.Version ?? HtmlPresets.Generic.Version),
            Facts = facts,
        };
        return new HtmlExtraction(parsed, outcome);
    }

    private static IElement? Select(IHtmlDocument document, string selector)
    {
        try
        {
            return document.QuerySelector(selector);
        }
        catch (DomException)
        {
            return null; // an invalid selector is a profile fault; the fallback takes over and emptyRoot shows it
        }
    }

    private static void Strip(IElement root, SiteProfile settings)
    {
        IEnumerable<string> selectors =
        [
            .. Dropped, .. HiddenSelectors, .. settings.Boilerplate.Selectors, .. settings.LinkBlocks,
            .. settings.Breadcrumb is { } crumb ? [crumb] : Array.Empty<string>(),
            "[aria-label=Brödsmulor]", "[aria-label=breadcrumb]", "[aria-label=Breadcrumb]",
        ];
        foreach (var selector in selectors)
        {
            IHtmlCollection<IElement> found;
            try
            {
                found = root.QuerySelectorAll(selector);
            }
            catch (DomException)
            {
                continue;
            }

            foreach (var element in found.ToList())
            {
                element.Remove();
            }
        }
    }

    private static string? Title(IHtmlDocument document)
    {
        if (Meta(document, "og:title") is { } og)
        {
            return og;
        }

        var title = Normalize(document.Title ?? string.Empty);
        if (title.Length == 0)
        {
            return document.QuerySelector("h1") is { } h1 && Normalize(h1.TextContent) is { Length: > 0 } heading ? heading : null;
        }

        // A repeated site suffix after " - " or a vertical bar ("Avgifter - Umeå kommun").
        var cut = SiteSuffix().Match(title);
        return cut.Success && cut.Index > 0 ? title[..cut.Index].Trim() : title;
    }

    private static string? Meta(IHtmlDocument document, string name)
    {
        foreach (var meta in document.QuerySelectorAll("meta"))
        {
            var key = meta.GetAttribute("name") ?? meta.GetAttribute("property");
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && Normalize(meta.GetAttribute("content") ?? string.Empty) is { Length: > 0 } content)
            {
                return content;
            }
        }

        return null;
    }

    private static List<string> Breadcrumb(IHtmlDocument document, SiteProfile settings, string? title)
    {
        IElement? element = null;
        if (settings.Breadcrumb is { } selector)
        {
            element = Select(document, selector);
        }

        element ??= document.QuerySelectorAll("[aria-label]")
            .FirstOrDefault(e => e.GetAttribute("aria-label") is { } label
                && (label.Equals("Brödsmulor", StringComparison.OrdinalIgnoreCase) || label.Equals("breadcrumb", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("breadcrumbs", StringComparison.OrdinalIgnoreCase)));
        List<string> items;
        if (element is not null)
        {
            var copy = (IElement)element.Clone(deep: true);
            foreach (var hidden in HiddenSelectors.SelectMany(s => copy.QuerySelectorAll(s)).ToList())
            {
                hidden.Remove();
            }

            var parts = copy.QuerySelectorAll("li");
            items = parts.Length > 0
                ? [.. parts.Select(p => Normalize(p.TextContent))]
                : [.. copy.QuerySelectorAll("a, span").Where(e => e.Children.Length == 0).Select(e => Normalize(e.TextContent))];
        }
        else
        {
            items = JsonLdBreadcrumb(document);
        }

        items = [.. items.Select(i => i.Trim('›', '>', '/', '|', ' ')).Where(i => i.Length > 0)];
        if (items.Count > 0 && title is not null && string.Equals(items[^1], title, StringComparison.OrdinalIgnoreCase))
        {
            items.RemoveAt(items.Count - 1);
        }

        return items;
    }

    private static List<string> JsonLdBreadcrumb(IHtmlDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                foreach (var node in Flatten(json.RootElement))
                {
                    if (node.TryGetProperty("@type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "BreadcrumbList"
                        && node.TryGetProperty("itemListElement", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        return
                        [
                            .. list.EnumerateArray()
                                .OrderBy(i => i.TryGetProperty("position", out var p) && p.TryGetInt32(out var n) ? n : int.MaxValue)
                                .Select(i => i.TryGetProperty("name", out var name) ? name.GetString() : i.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var inner) ? inner.GetString() : null)
                                .Where(n => n is not null)
                                .Select(n => Normalize(n!)),
                        ];
                    }
                }
            }
            catch (JsonException)
            {
                // Broken JSON-LD: no breadcrumb from it.
            }
        }

        return [];
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().SelectMany(Flatten))
            {
                yield return item;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            if (element.TryGetProperty("@graph", out var graph))
            {
                foreach (var item in Flatten(graph))
                {
                    yield return item;
                }
            }
        }
    }

    private static DateOnly? VisibleDate(IHtmlDocument document, SiteProfile settings)
    {
        if (settings.DatePattern is not { } pattern || document.Body is null)
        {
            return null;
        }

        try
        {
            var match = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout).Match(Normalize(document.Body.TextContent));
            return match.Success && DateOnly.TryParseExact(match.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return null; // validated at save (format: regex); a timeout means no date
        }
    }

    /// <summary>Whitespace collapsed to single spaces, NBSP included, trimmed.</summary>
    internal static string Normalize(string text) => Whitespace().Replace(text.Replace(' ', ' '), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+(?:-|–|\||｜)\s+[^-–|｜]+$")]
    private static partial Regex SiteSuffix();

    /// <summary>Walks the stripped root and emits blocks in document order.</summary>
    private sealed class BlockWalker(List<DocBlock> blocks, List<DocTable> tables, IReadOnlyList<string> phrases)
    {
        private readonly StringBuilder _loose = new();

        public void Walk(IElement root)
        {
            Children(root, 0);
            FlushLoose();
        }

        private void Children(INode parent, int listDepth)
        {
            foreach (var node in parent.ChildNodes)
            {
                switch (node)
                {
                    case IText text:
                        _loose.Append(text.Data);
                        break;
                    case IElement element:
                        Element(element, listDepth);
                        break;
                }
            }
        }

        private void Element(IElement element, int listDepth)
        {
            var name = element.LocalName;
            switch (name)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    FlushLoose();
                    Add(new DocBlock(BlockKinds.Heading, string.Empty) { Level = name[1] - '0', LevelSource = LevelSources.Html }, element.TextContent);
                    break;
                case "p" or "address" or "dt" or "dd":
                    FlushLoose();
                    Add(new DocBlock(BlockKinds.Paragraph, string.Empty), InlineText(element));
                    break;
                case "ul" or "ol":
                    FlushLoose();
                    foreach (var item in element.Children.Where(c => c.LocalName == "li"))
                    {
                        ListItem(item, listDepth);
                    }

                    break;
                case "li":
                    FlushLoose();
                    ListItem(element, listDepth);
                    break;
                case "table":
                    FlushLoose();
                    Table(element);
                    break;
                case "pre":
                    FlushLoose();
                    if (element.TextContent.Trim('\n', '\r') is { Length: > 0 } code)
                    {
                        blocks.Add(new DocBlock(BlockKinds.Code, code));
                    }

                    break;
                case "figcaption" or "caption":
                    FlushLoose();
                    Add(new DocBlock(BlockKinds.Caption, string.Empty), element.TextContent);
                    break;
                case "br":
                    _loose.Append('\n');
                    break;
                case "div" or "section" or "article" or "main" or "header" or "footer" or "aside" or "figure" or "blockquote" or "form" or "fieldset" or "details" or "summary" or "dl":
                    FlushLoose();
                    Children(element, listDepth);
                    FlushLoose();
                    break;
                default:
                    Children(element, listDepth); // inline: its text joins the loose paragraph
                    break;
            }
        }

        private void ListItem(IElement item, int depth)
        {
            var own = new StringBuilder();
            var nested = new List<IElement>();
            foreach (var node in item.ChildNodes)
            {
                if (node is IElement { LocalName: "ul" or "ol" } list)
                {
                    nested.Add(list);
                }
                else
                {
                    own.Append(node.TextContent);
                }
            }

            Add(new DocBlock(BlockKinds.ListItem, string.Empty) { ListDepth = depth }, own.ToString());
            foreach (var list in nested)
            {
                foreach (var child in list.Children.Where(c => c.LocalName == "li"))
                {
                    ListItem(child, depth + 1);
                }
            }
        }

        private void Table(IElement table)
        {
            var rows = table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table).ToList();
            var cells = new List<DocCell>();
            var occupied = new HashSet<(int, int)>();
            var headerRows = 0;
            var cols = 0;
            for (var r = 0; r < rows.Count; r++)
            {
                var c = 0;
                var allHeader = true;
                foreach (var cell in rows[r].Children.Where(e => e.LocalName is "td" or "th"))
                {
                    while (occupied.Contains((r, c)))
                    {
                        c++;
                    }

                    var rs = Math.Max(1, int.TryParse(cell.GetAttribute("rowspan"), CultureInfo.InvariantCulture, out var rowspan) ? Math.Min(rowspan, 100) : 1);
                    var cs = Math.Max(1, int.TryParse(cell.GetAttribute("colspan"), CultureInfo.InvariantCulture, out var colspan) ? Math.Min(colspan, 100) : 1);
                    var header = cell.LocalName == "th" || cell.Closest("thead") is not null;
                    allHeader &= header;
                    cells.Add(new DocCell(r, c, rs, cs, header, Normalize(cell.TextContent)));
                    for (var dr = 0; dr < rs; dr++)
                    {
                        for (var dc = 0; dc < cs; dc++)
                        {
                            occupied.Add((r + dr, c + dc));
                        }
                    }

                    c += cs;
                }

                cols = Math.Max(cols, c);
                if (allHeader && headerRows == r && rows[r].Children.Any(e => e.LocalName is "td" or "th"))
                {
                    headerRows = r + 1;
                }
            }

            if (cells.Count == 0)
            {
                return;
            }

            var caption = table.QuerySelector("caption") is { } element ? Normalize(element.TextContent) : null;
            tables.Add(new DocTable(rows.Count, cols, headerRows, cells) { Caption = string.IsNullOrEmpty(caption) ? null : caption });
            blocks.Add(new DocBlock(BlockKinds.Table, string.Join('\n', rows.Select(row => string.Join(" | ", row.Children.Select(e => Normalize(e.TextContent))))))
            {
                Table = tables.Count - 1,
            });
        }

        private static string InlineText(IElement element)
        {
            var text = new StringBuilder();
            void Visit(INode node)
            {
                foreach (var child in node.ChildNodes)
                {
                    if (child is IText t)
                    {
                        text.Append(t.Data);
                    }
                    else if (child is IElement { LocalName: "br" })
                    {
                        text.Append('\n');
                    }
                    else
                    {
                        Visit(child);
                    }
                }
            }

            Visit(element);
            return text.ToString();
        }

        private void FlushLoose()
        {
            if (_loose.Length > 0)
            {
                Add(new DocBlock(BlockKinds.Paragraph, string.Empty), _loose.ToString());
                _loose.Clear();
            }
        }

        private void Add(DocBlock block, string raw)
        {
            var lines = raw.Split('\n').Select(Normalize).Where(l => l.Length > 0).ToList();
            var text = string.Join('\n', lines);
            foreach (var phrase in phrases)
            {
                var normal = Normalize(phrase);
                if (normal.Length > 0)
                {
                    text = string.Join('\n', text.Split('\n').Select(l => Normalize(l.Replace(normal, string.Empty, StringComparison.Ordinal))).Where(l => l.Length > 0));
                }
            }

            if (text.Length > 0)
            {
                blocks.Add(block with { Text = text });
            }
        }
    }
}
