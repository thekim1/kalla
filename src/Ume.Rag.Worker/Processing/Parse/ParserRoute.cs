using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing.Detect;

namespace Ume.Rag.Worker.Processing.Parse;

/// <summary>
/// The parser's <c>/v1/convert</c> as the parse route calls it. <see cref="ParserClientConverter"/> is the production one;
/// the engine (item 9) streams the Docling part into the parse output's blobs (engine design §8).
/// </summary>
public interface IParserConverter
{
    Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken);
}

/// <summary>The converter over <see cref="ParserClient"/>, without the Docling part (written by the engine once it stores parse blobs).</summary>
public sealed class ParserClientConverter(ParserClient client) : IParserConverter
{
    public async Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(file.ToArray(), writable: false);
        return await client.ConvertAsync(stream, options, doclingGzip: null, cancellationToken);
    }
}

/// <summary>
/// The parser route of <c>parse</c> (processing design §3.1–§3.4, §4.1): plans every page from the inspect signals, runs
/// the ranges in order (fixed sizes, halved on <c>timeout</c>, <c>worker_crashed</c> and <c>resource_limit</c>; a single
/// page that still fails is a missing page), requests the rest of a <c>partial</c> range, re-runs garbled pages with forced
/// OCR and re-renders empty scanned pages once at scale 3.0, joins the pages in order and matches the parse rule on the
/// joined text. Missing pages ≤ 10 % (and ≤ 20) make the document <c>degraded</c>, more end it as <c>failed</c>.
/// Non-paged formats are one request. Parser codes map per engine design §7.3.
/// </summary>
public sealed partial class ParserRoute(IParserConverter converter, IParsePlanner planner, IOptions<ParseOptions> options)
{
    public const double MaxMissingShare = 0.10;
    public const int MaxMissingPages = 20;

    private static readonly HashSet<string> Halving = new(StringComparer.Ordinal) { "timeout", "worker_crashed", "resource_limit" };
    private static readonly HashSet<string> RetryOnce = new(StringComparer.Ordinal) { "ocr_failed", "conversion_failed" };
    private static readonly HashSet<string> Requeue = new(StringComparer.Ordinal) { "busy", "not_ready", "duplicate_in_flight" };

    public async Task<ParsedDocument> ParseAsync(ReadOnlyMemory<byte> file, DetectResult detect, ParseParams parameters, string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detect);
        ArgumentNullException.ThrowIfNull(parameters);
        var extension = detect.FileExtension ?? throw new ArgumentException("A parser-bound document needs its file extension.", nameof(detect));
        var inspect = detect.Inspect;
        if (inspect is not null && inspect.Rangeable && inspect.Pages.Count > 0)
        {
            return await PagedAsync(file, detect, inspect, parameters, extension, requestId, cancellationToken);
        }

        // Non-rangeable formats (DOCX, PPTX, XLSX, ODF, single images): one request; a timeout cannot be split.
        var whole = new ParserConvertOptions(requestId, extension)
        {
            MediaType = detect.MediaType,
            DeadlineMs = options.Value.DeadlineMs,
            Ocr = new ParserOcrOptions { Mode = parameters.Ocr == "force" ? ParserOcrMode.Force : ParserOcrMode.Auto },
            Tables = new ParserTableOptions { Mode = parameters.Tables == "fast" ? ParserTableMode.Fast : ParserTableMode.Accurate },
        };
        ParserConvertResult result;
        try
        {
            result = await converter.ConvertAsync(file, whole, cancellationToken);
        }
        catch (ParserException e)
        {
            throw Map(e, splittable: false);
        }

        var collected = new Collected();
        collected.Add(result, strategy: null);
        var parsed = collected.Join(detect, []);
        var rule = ParseRules.Match(parsed.Title, string.Join('\n', parsed.Blocks.Select(b => b.Text)));
        return parsed with
        {
            ParseRule = rule.Id,
            Strategy = StrategyString.Whole(rule, FormatName(detect.MediaType, result.Input.Format)),
        };
    }

    private async Task<ParsedDocument> PagedAsync(ReadOnlyMemory<byte> file, DetectResult detect, InspectFacts inspect, ParseParams parameters,
        string extension, string requestId, CancellationToken cancellationToken)
    {
        // Strategy-level matches run before the plan; the built-in rules are text-level only, so the plan uses default.
        var plan = planner.Plan(inspect, ParseRules.Default, parameters);
        var strategies = plan.Pages.ToDictionary(p => p.Page, p => p.Strategy);
        var collected = new Collected();
        var missing = new SortedSet<int>();
        string? lastFailure = null;
        var sequence = 0;

        async Task RunAsync(IEnumerable<ParseRange> ranges)
        {
            var queue = new LinkedList<ParseRange>(ranges);
            var retried = new HashSet<(int, int)>();
            while (queue.First is { } node)
            {
                queue.RemoveFirst();
                var range = node.Value;
                var convert = RangePlanner.Options(range, plan, string.Create(CultureInfo.InvariantCulture, $"{requestId}-r{sequence++}"),
                    extension, detect.MediaType, options.Value.DeadlineMs);
                ParserConvertResult result;
                try
                {
                    result = await converter.ConvertAsync(file, convert, cancellationToken);
                }
                catch (ParserException e) when (e.Error?.Error.Code is { } code && (Halving.Contains(code) || RetryOnce.Contains(code)))
                {
                    lastFailure = code;
                    if (RetryOnce.Contains(code) && retried.Add((range.From, range.To)))
                    {
                        queue.AddFirst(range);
                    }
                    else if (range.Pages == 1)
                    {
                        missing.Add(range.From);
                    }
                    else
                    {
                        var half = range.From + (range.Pages / 2) - 1;
                        queue.AddFirst(range with { From = half + 1 });
                        queue.AddFirst(range with { To = half });
                    }

                    continue;
                }
                catch (ParserException e)
                {
                    throw Map(e, splittable: true);
                }

                collected.Add(result, range.Strategy);
                var completed = result.Input.PagesCompleted;
                if (result.Status == "partial" && completed is not null && completed.To < range.To)
                {
                    queue.AddFirst(range with { From = Math.Max(completed.To + 1, range.From) });
                }
            }
        }

        await RunAsync(RangePlanner.Ranges(plan, options.Value));

        // Post-conversion checks (§3.1), once per page; none with OCR off, where an empty page stays missing.
        var rerendered = new SortedSet<int>();
        if (parameters.Ocr != "off")
        {
            var garbled = collected.Pages.Values
                .Where(p => p.Strategy is ParseStrategies.DoclingOcr or ParseStrategies.TextLayer && p.Signals.GarbledScore >= 0.4)
                .Select(p => p.Number).ToList();
            var empty = collected.Pages.Values
                .Where(p => p.Signals.Chars < 20 && (p.Signals.ImageCoverage ?? 0) >= 0.5 && p.Strategy != ParseStrategies.DoclingOcrForce3)
                .Select(p => p.Number).Except(garbled).ToList();
            foreach (var page in garbled)
            {
                strategies[page] = ParseStrategies.DoclingOcrForce;
            }

            foreach (var page in empty)
            {
                rerendered.Add(page);
            }

            await RunAsync(Runs(garbled, ParseStrategies.DoclingOcrForce).Concat(Runs(empty, ParseStrategies.DoclingOcrForce3)));
        }
        else
        {
            foreach (var page in collected.Pages.Values.Where(p => p.Signals.Empty).Select(p => p.Number))
            {
                missing.Add(page);
            }
        }

        var pageCount = inspect.PageCount ?? plan.Pages.Count;
        if (missing.Count > 0 && (missing.Count > MaxMissingShare * pageCount || missing.Count > MaxMissingPages))
        {
            throw new StageFatalException(RunItemOutcome.Failed, "parser." + (lastFailure ?? "missing_pages"));
        }

        foreach (var page in plan.Pages.Where(p => p.Strategy == ParseStrategies.Blank))
        {
            collected.AddBlank(page.Page);
        }

        var parsed = collected.Join(detect, missing);
        var rule = ParseRules.Match(parsed.Title, string.Join('\n', parsed.Blocks.Select(b => b.Text)));
        var final = plan.Pages.Select(p => p with { Strategy = strategies[p.Page] }).ToList();
        return parsed with
        {
            ParseRule = rule.Id,
            Strategy = StrategyString.Paged(rule, final, rerendered, missing),
            MissingPages = [.. missing],
        };
    }

    /// <summary>Single-strategy runs of the given pages, each at most the strategy's range size.</summary>
    private IEnumerable<ParseRange> Runs(IEnumerable<int> pages, string strategy) =>
        RangePlanner.Ranges(new PagePlan([.. pages.Select(p => new PageStrategy(p, strategy, 0))], ParserTableMode.Accurate, 2.0), options.Value);

    /// <summary>The engine design §7.3 mapping of a parser error that the route does not handle itself.</summary>
    internal static Exception Map(ParserException e, bool splittable)
    {
        var code = e.Error?.Error.Code;
        if (code is not null && Requeue.Contains(code))
        {
            return new StageTransientException("parser." + code, e.RetryAfter ?? TimeSpan.FromSeconds(5), countsAttempt: false);
        }

        if (code is null && e.StatusCode is HttpStatusCode.ServiceUnavailable)
        {
            return new StageTransientException("parser.unavailable", e.RetryAfter ?? TimeSpan.FromSeconds(5), countsAttempt: false);
        }

        return code switch
        {
            "too_large" => new StageFatalException(RunItemOutcome.TooLarge, "parser.too_large"),
            "format_deferred" => new StageFatalException(RunItemOutcome.Unsupported, "parser.format_deferred"),
            "unsupported_format" => new StageFatalException(RunItemOutcome.Unsupported, "parser.unsupported_format"),
            "encrypted" => new StageFatalException(RunItemOutcome.Encrypted, "parser.encrypted"),
            "limit_exceeded" => new StageFatalException(RunItemOutcome.Failed, "parser.limit_exceeded", securityEvent: true),
            "timeout" or "worker_crashed" or "resource_limit" when !splittable => new StageFatalException(RunItemOutcome.Failed, "parser." + code),
            "ocr_failed" or "conversion_failed" when !splittable => new StageTransientException("parser." + code, null, countsAttempt: true),
            { } other => new StageFatalException(RunItemOutcome.Failed, "parser." + other),
            null => new StageTransientException("parser.http_" + ((int)e.StatusCode).ToString(CultureInfo.InvariantCulture), null, countsAttempt: true),
        };
    }

    private static string FormatName(string mediaType, string format) => mediaType switch
    {
        ProcessingMediaTypes.Png or ProcessingMediaTypes.Jpeg or ProcessingMediaTypes.Tiff => "image",
        _ => format,
    };

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^\s*(?:[-*+•]|\d{1,3}[.)])\s+")]
    private static partial Regex ListMarker();

    /// <summary>Block text without its Markdown heading or list marker (the parser's <c>md</c> carries them).</summary>
    internal static string PlainBlockText(string kind, string md) => kind switch
    {
        BlockKinds.Heading or BlockKinds.Title => HeadingMarker().Replace(md, string.Empty).Trim(),
        BlockKinds.ListItem => ListMarker().Replace(md, string.Empty).Trim(),
        _ => md.Trim(),
    };

    private sealed record PageResult(int Number, string? Strategy, ParserPageSignals Signals, List<DocBlock> Blocks, List<DocTable> Tables);

    /// <summary>Per-page results of all ranges; a later result for a page (re-run, re-render) replaces the earlier one.</summary>
    private sealed class Collected
    {
        public SortedDictionary<int, PageResult> Pages { get; } = [];

        private readonly List<(DocBlock Block, DocTable? Table)> _unpaged = [];

        public void Add(ParserConvertResult result, string? strategy)
        {
            var tables = result.Tables.ToDictionary(t => t.Id, ToTable, StringComparer.Ordinal);
            var heights = result.Pages.Where(p => p.Page is not null).ToDictionary(p => p.Page!.Value, p => p.Height);
            // Spreadsheets: the page is the sheet and its label the sheet name, which joins the heading path (§6.3).
            var sheets = result.Input.Format is "xlsx" or "ods"
                ? result.Pages.Where(p => p.Page is not null && p.Label is not null).ToDictionary(p => p.Page!.Value, p => p.Label!)
                : [];
            var fresh = new Dictionary<int, PageResult>();
            foreach (var page in result.Pages.Where(p => p.Page is not null))
            {
                fresh[page.Page!.Value] = new PageResult(page.Page.Value, strategy, page.Signals, [], []);
            }

            foreach (var block in result.Blocks)
            {
                var table = block.Table is not null ? tables.GetValueOrDefault(block.Table) : null;
                if (table is not null && block.Page is { } sheetPage && sheets.TryGetValue(sheetPage, out var sheet))
                {
                    table = table with { Sheet = sheet };
                }

                var doc = new DocBlock(block.Kind, PlainBlockText(block.Kind, block.Md))
                {
                    Level = block.Level,
                    LevelSource = block.LevelSource,
                    Page = block.Page,
                    PageEnd = block.PageEnd,
                    Layer = block.Layer,
                    PageEdge = block.Page is { } p && heights.GetValueOrDefault(p) is { } h && h > 0 && block.Bbox is { Count: 4 } box
                        && (Math.Min(box[1], box[3]) / h < 0.08 || Math.Max(box[1], box[3]) / h > 0.92),
                };
                if (block.Page is { } number && fresh.TryGetValue(number, out var target))
                {
                    target.Blocks.Add(doc);
                    if (table is not null)
                    {
                        target.Tables.Add(table);
                    }
                }
                else
                {
                    _unpaged.Add((doc, table));
                }
            }

            foreach (var (number, page) in fresh)
            {
                Pages[number] = page;
            }
        }

        public void AddBlank(int page) =>
            Pages.TryAdd(page, new PageResult(page, ParseStrategies.Blank, new ParserPageSignals(0, 0, 0, null, null, "none", null, 0, 0, 0, true, null), [], []));

        public ParsedDocument Join(DetectResult detect, IReadOnlyCollection<int> missing)
        {
            var blocks = new List<DocBlock>();
            var tables = new List<DocTable>();
            void Append(DocBlock block, DocTable? table)
            {
                if (table is not null)
                {
                    tables.Add(table);
                    block = block with { Table = tables.Count - 1 };
                }

                blocks.Add(block);
            }

            foreach (var page in Pages.Values)
            {
                var tableIndex = 0;
                foreach (var block in page.Blocks)
                {
                    Append(block, block.Kind == BlockKinds.Table && tableIndex < page.Tables.Count ? page.Tables[tableIndex++] : null);
                }
            }

            foreach (var (block, table) in _unpaged)
            {
                Append(block, table);
            }

            var pages = Pages.Values
                .Where(p => !missing.Contains(p.Number))
                .Select(p => new DocPage(p.Number)
                {
                    Chars = p.Signals.Chars,
                    GarbledScore = p.Signals.GarbledScore,
                    OcrP10 = p.Signals.OcrConfidence?.P10,
                    Blank = p.Strategy == ParseStrategies.Blank,
                    Strategy = p.Strategy,
                })
                .ToList();
            var title = blocks.FirstOrDefault(b => b.Kind == BlockKinds.Title && b.Layer == Layers.Body)?.Text;
            return new ParsedDocument
            {
                Title = title,
                Language = detect.Language,
                Blocks = blocks,
                Tables = tables,
                Pages = pages,
            };
        }

        private static DocTable ToTable(ParserTable table) =>
            new(table.Rows, table.Cols, table.HeaderRows, [.. table.Cells.Select(c => new DocCell(c.R, c.C, c.Rs, c.Cs, c.Header, c.Text))])
            {
                Caption = table.Caption,
            };
    }
}
