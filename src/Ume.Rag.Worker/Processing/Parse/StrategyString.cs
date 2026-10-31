using System.Globalization;
using System.Text;

namespace Ume.Rag.Worker.Processing.Parse;

/// <summary>
/// <c>RunItems.Strategy</c>, the fact <c>parse.strategy</c> and <c>Documents.ParseStrategy</c> (processing design §3.4,
/// ≤ 500 characters): <c>parse=&lt;route&gt;(rule=&lt;name&gt;@&lt;v&gt; | profile=&lt;host&gt;@&lt;v&gt;)[&lt;strategy&gt;:&lt;pages&gt;,…;rerender:&lt;pages&gt;;missing:&lt;pages&gt;]</c>.
/// Runs are listed in page order (a strategy may appear twice); pages are compressed to ranges; a string over the limit
/// is cut and ends with <c>…</c>.
/// </summary>
public static class StrategyString
{
    public const int MaxLength = 500;

    /// <summary>A paged parser document: one entry per run of consecutive pages with the same strategy.</summary>
    public static string Paged(ParseRule rule, IReadOnlyList<PageStrategy> pages, IReadOnlyCollection<int> rerendered, IReadOnlyCollection<int> missing)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(pages);
        var runs = new List<string>();
        var ordered = pages.OrderBy(p => p.Page).ToList();
        var i = 0;
        while (i < ordered.Count)
        {
            var from = ordered[i].Page;
            var to = from;
            var strategy = ordered[i].Strategy;
            i++;
            while (i < ordered.Count && ordered[i].Strategy == strategy && ordered[i].Page == to + 1)
            {
                to = ordered[i].Page;
                i++;
            }

            runs.Add(strategy + ":" + Range(from, to));
        }

        var body = new StringBuilder(string.Join(',', runs));
        if (rerendered.Count > 0)
        {
            body.Append(";rerender:").Append(Pages(rerendered));
        }

        if (missing.Count > 0)
        {
            body.Append(";missing:").Append(Pages(missing));
        }

        return Limit("parse=auto" + RulePart(rule) + "[" + body + "]");
    }

    /// <summary>A non-paged parser document (DOCX, PPTX, XLSX, ODF, single images): <c>parse=auto[docling-docx]</c>.</summary>
    public static string Whole(ParseRule rule, string format) => Limit("parse=auto" + RulePart(rule) + "[docling-" + format + "]");

    /// <summary>HTML: <c>parse=html(profile=&lt;host&gt;@&lt;v&gt;)</c>, or <c>parse=html(preset=&lt;name&gt;@&lt;v&gt;)</c> without a profile.</summary>
    public static string Html(string? profileHost, int? profileVersion, string preset, int presetVersion) =>
        Limit(profileHost is not null && profileVersion is not null
            ? string.Create(CultureInfo.InvariantCulture, $"parse=html(profile={profileHost}@{profileVersion})")
            : string.Create(CultureInfo.InvariantCulture, $"parse=html(preset={preset}@{presetVersion})"));

    /// <summary>Text and CSV in the worker: <c>parse=text[markdown]</c>, <c>parse=csv[csv-table]</c>.</summary>
    public static string Worker(string route, string parser) => Limit($"parse={route}[{parser}]");

    /// <summary>Pages compressed to ranges: 1,2,3,5 → <c>1-3,5</c>.</summary>
    public static string Pages(IEnumerable<int> pages)
    {
        var sorted = pages.Distinct().Order().ToList();
        var parts = new List<string>();
        var i = 0;
        while (i < sorted.Count)
        {
            var from = sorted[i];
            var to = from;
            while (i + 1 < sorted.Count && sorted[i + 1] == to + 1)
            {
                to = sorted[++i];
            }

            parts.Add(Range(from, to));
            i++;
        }

        return string.Join(',', parts);
    }

    private static string RulePart(ParseRule rule) => rule.Name == ParseRules.Default.Name ? string.Empty : "(rule=" + rule.Id + ")";

    private static string Range(int from, int to) =>
        from == to ? from.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{from}-{to}");

    private static string Limit(string value) => value.Length <= MaxLength ? value : value[..(MaxLength - 1)] + "…";
}
