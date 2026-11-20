using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;

namespace Kalla.Worker.Processing.Text;

/// <summary>
/// <c>plain-text</c> (processing design §3.7): charset by BOM, strict UTF-8, Windows-1252; Markdown through Markdig
/// (headings <c>level_source: markdown</c>, lists with depth, pipe tables, fenced code); plain text as paragraphs split
/// on blank lines.
/// </summary>
public static partial class PlainTextParser
{
    public const int Version = 1;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static ParsedDocument Parse(ReadOnlySpan<byte> bytes, bool markdown)
    {
        var text = TextDecoding.Decode(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return markdown ? Markdown(text) : Plain(text);
    }

    private static ParsedDocument Plain(string text)
    {
        var blocks = BlankLines().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => new DocBlock(BlockKinds.Paragraph, p))
            .ToList();
        return new ParsedDocument
        {
            Blocks = blocks,
            Language = LanguageDetector.Detect(text),
            Strategy = StrategyString.Worker("text", "plain-text"),
        };
    }

    private static ParsedDocument Markdown(string text)
    {
        var document = Markdig.Markdown.Parse(text, Pipeline);
        var blocks = new List<DocBlock>();
        var tables = new List<DocTable>();
        Walk(document, blocks, tables, listDepth: -1);
        var title = blocks.FirstOrDefault(b => b.Kind == BlockKinds.Heading && b.Level == 1)?.Text;
        return new ParsedDocument
        {
            Title = title,
            Blocks = blocks,
            Tables = tables,
            Language = LanguageDetector.Detect(text),
            Strategy = StrategyString.Worker("text", "markdown"),
        };
    }

    private static void Walk(ContainerBlock container, List<DocBlock> blocks, List<DocTable> tables, int listDepth)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    Add(blocks, new DocBlock(BlockKinds.Heading, Inline(heading.Inline)) { Level = heading.Level, LevelSource = LevelSources.Markdown });
                    break;
                case Table table:
                    AddTable(table, blocks, tables);
                    break;
                case ListBlock list:
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        ListItem(item, blocks, tables, listDepth + 1);
                    }

                    break;
                case FencedCodeBlock or CodeBlock:
                    var code = string.Join('\n', ((LeafBlock)block).Lines.Lines.Take(((LeafBlock)block).Lines.Count).Select(l => l.ToString()));
                    Add(blocks, new DocBlock(BlockKinds.Code, code.TrimEnd()));
                    break;
                case ParagraphBlock paragraph:
                    Add(blocks, new DocBlock(BlockKinds.Paragraph, Inline(paragraph.Inline)));
                    break;
                case ContainerBlock inner:
                    Walk(inner, blocks, tables, listDepth);
                    break;
            }
        }
    }

    private static void ListItem(ListItemBlock item, List<DocBlock> blocks, List<DocTable> tables, int depth)
    {
        var own = new List<string>();
        foreach (var child in item)
        {
            if (child is ParagraphBlock paragraph)
            {
                own.Add(Inline(paragraph.Inline));
            }
        }

        Add(blocks, new DocBlock(BlockKinds.ListItem, string.Join(' ', own)) { ListDepth = depth });
        foreach (var child in item)
        {
            if (child is ListBlock nested)
            {
                foreach (var inner in nested.OfType<ListItemBlock>())
                {
                    ListItem(inner, blocks, tables, depth + 1);
                }
            }
            else if (child is not ParagraphBlock and ContainerBlock container)
            {
                Walk(container, blocks, tables, depth);
            }
        }
    }

    private static void AddTable(Table table, List<DocBlock> blocks, List<DocTable> tables)
    {
        var cells = new List<DocCell>();
        var rows = table.OfType<TableRow>().ToList();
        var headerRows = 0;
        var cols = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var c = 0;
            foreach (var cell in rows[r].OfType<TableCell>())
            {
                var text = string.Join(' ', cell.OfType<ParagraphBlock>().Select(p => Inline(p.Inline)));
                cells.Add(new DocCell(r, c, 1, Math.Max(1, cell.ColumnSpan), rows[r].IsHeader, text));
                c += Math.Max(1, cell.ColumnSpan);
            }

            cols = Math.Max(cols, c);
            if (rows[r].IsHeader && headerRows == r)
            {
                headerRows = r + 1;
            }
        }

        tables.Add(new DocTable(rows.Count, cols, headerRows, cells));
        blocks.Add(new DocBlock(BlockKinds.Table, string.Join('\n', rows.Select(row => string.Join(" | ",
            row.OfType<TableCell>().Select(cell => string.Join(' ', cell.OfType<ParagraphBlock>().Select(p => Inline(p.Inline)))))))) { Table = tables.Count - 1 });
    }

    private static void Add(List<DocBlock> blocks, DocBlock block)
    {
        if (block.Text.Length > 0)
        {
            blocks.Add(block);
        }
    }

    private static string Inline(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        void Visit(Inline node)
        {
            switch (node)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.ToString());
                    break;
                case ContainerInline container:
                    foreach (var child in container)
                    {
                        Visit(child);
                    }

                    break;
            }
        }

        Visit(inline);
        return Spaces().Replace(text.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}

/// <summary>
/// <c>csv-table</c> (processing design §3.7): delimiter sniffed among <c>;</c>, <c>,</c>, tab and <c>|</c> on the first
/// 50 lines; the first row is the header when every value is non-numeric and distinct; one table block; at most
/// <see cref="MaxCells"/> cells (parser design §7.4).
/// </summary>
public static class CsvTableParser
{
    public const int Version = 1;
    public const int MaxCells = 200_000;
    private static readonly char[] Delimiters = [';', ',', '\t', '|'];

    public static ParsedDocument Parse(ReadOnlySpan<byte> bytes)
    {
        var text = TextDecoding.Decode(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var delimiter = Sniff(lines.Take(50).ToList());
        var rows = lines.Select(l => Split(l, delimiter)).ToList();
        var cells = new List<DocCell>();
        var cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var truncated = false;
        for (var r = 0; r < rows.Count && !truncated; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (cells.Count >= MaxCells)
                {
                    truncated = true;
                    break;
                }

                cells.Add(new DocCell(r, c, 1, 1, false, rows[r][c]));
            }
        }

        var header = rows.Count > 1 && rows[0].All(v => v.Length > 0 && !double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
            && rows[0].Distinct(StringComparer.Ordinal).Count() == rows[0].Count;
        if (header)
        {
            cells = [.. cells.Select(c => c.R == 0 ? c with { Header = true } : c)];
        }

        var table = new DocTable(rows.Count, cols, header ? 1 : 0, cells);
        var facts = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (truncated)
        {
            facts["quality.truncatedCells"] = 1;
        }

        return new ParsedDocument
        {
            Blocks = rows.Count == 0 ? [] : [new DocBlock(BlockKinds.Table, string.Join('\n', lines.Take(5))) { Table = 0 }],
            Tables = rows.Count == 0 ? [] : [table],
            Strategy = StrategyString.Worker("csv", "csv-table"),
            Facts = facts,
        };
    }

    /// <summary>The delimiter that splits the sample into the most consistent number of fields (&gt; 1).</summary>
    internal static char Sniff(IReadOnlyList<string> sample)
    {
        var best = ',';
        var bestScore = -1.0;
        foreach (var delimiter in Delimiters)
        {
            var counts = sample.Select(l => Split(l, delimiter).Count).ToList();
            if (counts.Count == 0 || counts.Max() < 2)
            {
                continue;
            }

            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            var score = mode.Count() / (double)counts.Count + mode.Key / 1000.0;
            if (score > bestScore)
            {
                (best, bestScore) = (delimiter, score);
            }
        }

        return best;
    }

    /// <summary>One CSV line: double-quoted fields with doubled quotes, trimmed values.</summary>
    internal static List<string> Split(string line, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.ToString().Trim().Length == 0)
            {
                quoted = true;
                field.Clear();
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString().Trim());
        return fields;
    }
}
