using System.ComponentModel.DataAnnotations;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing.Detect;

namespace Ume.Rag.Worker.Processing.Parse;

/// <summary>The per-page strategies of processing design §3.1 (and the metric dimension of §15).</summary>
public static class ParseStrategies
{
    public const string Blank = "blank";
    public const string TextLayer = "text-layer";
    public const string DoclingText = "docling-text";
    public const string DoclingTables = "docling-tables";
    public const string DoclingOcr = "docling-ocr";
    public const string DoclingOcrForce = "docling-ocr-force";

    /// <summary>The re-render at <c>image_scale</c> 3.0 (§3.1 post-conversion check).</summary>
    public const string DoclingOcrForce3 = "docling-ocr-force@3";
}

/// <summary>The template parameters of the parser route (engine design §3.2): <c>ocr</c> and <c>tables</c>.</summary>
public sealed record ParseParams(string Ocr = "auto", string Tables = "accurate");

/// <summary>One page's strategy and the rule (1–6) that chose it; 0 for template or parse-rule overrides.</summary>
public sealed record PageStrategy(int Page, string Strategy, int Rule);

/// <summary>The page plan of a paged document (processing design §3.1, §3.6).</summary>
public sealed record PagePlan(IReadOnlyList<PageStrategy> Pages, ParserTableMode Tables, double ImageScale);

/// <summary>A range of consecutive pages with one strategy (processing design §3.3; 1-based, inclusive).</summary>
public sealed record ParseRange(string Strategy, int From, int To)
{
    public int Pages => To - From + 1;
}

/// <summary>
/// The fixed first range sizes per strategy (processing design §3.3 rule 2, P1): <c>Worker:Parse:RangePages:&lt;strategy&gt;</c>,
/// halved only on failure. EWMA sizing is P2.
/// </summary>
public sealed class ParseOptions
{
    public const string SectionName = "Worker:Parse";

    /// <summary>Range size per strategy; missing strategies use <see cref="DefaultRangePages"/>.</summary>
    public Dictionary<string, int> RangePages { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The <c>/v1/convert</c> deadline (parser design §8: 110 s).</summary>
    [Range(1_000, 120_000)]
    public int DeadlineMs { get; set; } = 110_000;

    public static int DefaultRangePages(string strategy) => strategy switch
    {
        ParseStrategies.TextLayer or ParseStrategies.DoclingText or ParseStrategies.DoclingTables => 20,
        ParseStrategies.DoclingOcr or ParseStrategies.DoclingOcrForce => 8,
        ParseStrategies.DoclingOcrForce3 => 4,
        _ => 20,
    };

    public int RangeSize(string strategy) =>
        Math.Clamp(RangePages.TryGetValue(strategy, out var size) ? size : DefaultRangePages(strategy), 1, 20);
}

/// <summary>The planner seam of processing design §3.6.</summary>
public interface IParsePlanner
{
    PagePlan Plan(InspectFacts inspect, ParseRule rule, ParseParams parameters);
}

/// <summary>
/// The six ordered per-page rules of processing design §3.1 over the inspect signals, then the template parameters and
/// the parse rule's overrides. Thresholds live here with the parse stage's version (§3.1: changing them is a version
/// bump).
/// </summary>
public sealed class ParsePlanner : IParsePlanner
{
    public PagePlan Plan(InspectFacts inspect, ParseRule rule, ParseParams parameters)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(parameters);
        var ocr = rule.Ocr ?? parameters.Ocr;
        var tables = (rule.Tables ?? parameters.Tables) == "fast" ? ParserTableMode.Fast : ParserTableMode.Accurate;
        var tiff = string.Equals(inspect.Format, "tiff", StringComparison.Ordinal);
        var pages = new List<PageStrategy>(inspect.Pages.Count);
        foreach (var page in inspect.Pages.OrderBy(p => p.Page))
        {
            var (strategy, number) = ByRules(page);
            if (tiff && strategy is ParseStrategies.Blank or ParseStrategies.TextLayer or ParseStrategies.DoclingTables)
            {
                strategy = ParseStrategies.DoclingOcr; // frames are images: docling-ocr is the floor
            }

            if (ocr == "force")
            {
                (strategy, number) = (ParseStrategies.DoclingOcrForce, 0);
            }
            else if (ocr == "off" && number is 2 or 3 or 6)
            {
                (strategy, number) = (ParseStrategies.DoclingText, 0);
            }

            pages.Add(new PageStrategy(page.Page, strategy, number));
        }

        return new PagePlan(pages, tables, rule.ImageScale ?? 2.0);
    }

    /// <summary>Rule 4's and 5's "clean" condition; detect's <c>scanned</c> fact counts the pages that fail it.</summary>
    public static bool IsClean(InspectPageSignals page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var chars = page.TextChars ?? 0;
        var image = page.ImageCoverage ?? 0;
        var garbled = page.GarbledScore ?? 0;
        return (chars >= 200 || (chars >= 50 && image < 0.3)) && garbled < 0.2;
    }

    private static (string Strategy, int Rule) ByRules(InspectPageSignals page)
    {
        var chars = page.TextChars ?? 0;
        var bad = page.BadChars ?? 0;
        var image = page.ImageCoverage ?? 0;
        var paths = page.PathObjects ?? 0;
        var garbled = page.GarbledScore ?? 0;
        if (chars < 50 && image < 0.1 && paths < 200)
        {
            return (ParseStrategies.Blank, 1);
        }

        if (chars < 50 && paths >= 200)
        {
            return (ParseStrategies.DoclingOcrForce, 2);
        }

        if (garbled >= 0.4 || (chars > 0 && (double)bad / chars >= 0.1))
        {
            return (ParseStrategies.DoclingOcrForce, 3);
        }

        if (IsClean(page))
        {
            return paths >= 40 ? (ParseStrategies.DoclingTables, 4) : (ParseStrategies.TextLayer, 5);
        }

        return (ParseStrategies.DoclingOcr, 6);
    }
}

/// <summary>Range planning (processing design §3.3 rules 1–2): runs of one strategy cut at the strategy's fixed size.</summary>
public static class RangePlanner
{
    public static IReadOnlyList<ParseRange> Ranges(PagePlan plan, ParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        var ranges = new List<ParseRange>();
        var pages = plan.Pages.Where(p => p.Strategy != ParseStrategies.Blank).OrderBy(p => p.Page).ToList();
        var i = 0;
        while (i < pages.Count)
        {
            var strategy = pages[i].Strategy;
            var size = options.RangeSize(strategy);
            var from = pages[i].Page;
            var to = from;
            i++;
            while (i < pages.Count && pages[i].Strategy == strategy && pages[i].Page == to + 1 && to - from + 1 < size)
            {
                to = pages[i].Page;
                i++;
            }

            ranges.Add(new ParseRange(strategy, from, to));
        }

        return ranges;
    }

    /// <summary>The <c>/v1/convert</c> options of one range (parser design §7.2).</summary>
    public static ParserConvertOptions Options(ParseRange range, PagePlan plan, string requestId, string fileExtension, string? mediaType, int deadlineMs)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(plan);
        var options = new ParserConvertOptions(requestId, fileExtension)
        {
            MediaType = mediaType,
            Pages = new ParserPageRange(range.From, range.To),
            DeadlineMs = deadlineMs,
            ImageScale = plan.ImageScale,
            Tables = new ParserTableOptions { Mode = plan.Tables },
        };
        return range.Strategy switch
        {
            ParseStrategies.TextLayer => options with { Pipeline = ParserPipeline.TextLayer },
            ParseStrategies.DoclingText => options with { Ocr = new ParserOcrOptions { Mode = ParserOcrMode.Off } },
            ParseStrategies.DoclingTables => options with
            {
                Ocr = new ParserOcrOptions { Mode = ParserOcrMode.Off },
                Tables = new ParserTableOptions { Mode = ParserTableMode.Accurate }, // rule 4 is always accurate
            },
            ParseStrategies.DoclingOcr => options with { Ocr = new ParserOcrOptions { Mode = ParserOcrMode.Auto } },
            ParseStrategies.DoclingOcrForce => options with { Ocr = new ParserOcrOptions { Mode = ParserOcrMode.Force } },
            ParseStrategies.DoclingOcrForce3 => options with { Ocr = new ParserOcrOptions { Mode = ParserOcrMode.Force }, ImageScale = 3.0 },
            _ => throw new ArgumentOutOfRangeException(nameof(range), range.Strategy, "Not a requested strategy."),
        };
    }
}
