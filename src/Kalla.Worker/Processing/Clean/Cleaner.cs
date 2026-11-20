using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;

namespace Kalla.Worker.Processing.Clean;

/// <summary>
/// <c>clean</c> (processing design §4.2), in order on body blocks: Unicode, furniture, table of contents, dehyphenation,
/// whitespace, minimum block, Swedish specifics, language. Every rule counts its effect into a <c>clean.*</c> fact; the
/// parse-output quality facts of §8 are computed at the end. HTML boilerplate was removed by extraction, not here.
/// </summary>
public static partial class Cleaner
{
    public const int Version = 1;

    private static readonly HashSet<string> SuspendedCompoundWords = new(StringComparer.Ordinal) { "och", "eller", "samt", "till", "respektive" };

    public static ParsedDocument Clean(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rule = ParseRules.Of(document);
        var facts = new SortedDictionary<string, double>(document.Facts.ToDictionary(), StringComparer.Ordinal);
        void Count(string fact, int n)
        {
            if (n > 0)
            {
                facts[fact] = facts.GetValueOrDefault(fact) + n;
            }
        }

        // Unicode first, so the patterns below see normalised text.
        var blocks = document.Blocks.Select(b => b with { Text = Unicode(b.Text) }).ToList();
        var tables = document.Tables.Select(t => t with
        {
            Caption = t.Caption is null ? null : Unicode(t.Caption),
            Sheet = t.Sheet is null ? null : Unicode(t.Sheet),
            Cells = [.. t.Cells.Select(c => c with { Text = Unicode(c.Text) })],
        }).ToList();

        // Furniture: layer labels, then repeated edge lines on unlabelled (text-layer) pages, page numbers, rule patterns.
        var before = blocks.Count;
        blocks.RemoveAll(b => b.Layer == Layers.Furniture || b.Kind is BlockKinds.PageHeader or BlockKinds.PageFooter);
        var textLayerPages = document.Pages.Where(p => p.Strategy == ParseStrategies.TextLayer).Select(p => p.Page).ToHashSet();
        if (textLayerPages.Count >= 3)
        {
            var recurring = blocks
                .Where(b => b.PageEdge && b.Page is { } p && textLayerPages.Contains(p))
                .GroupBy(b => DigitNormalised(b.Text), StringComparer.Ordinal)
                .Where(g => g.Select(b => b.Page).Distinct().Count() >= Math.Max(3, textLayerPages.Count / 2.0))
                .Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal);
            blocks.RemoveAll(b => b.PageEdge && b.Page is { } p && textLayerPages.Contains(p) && recurring.Contains(DigitNormalised(b.Text)));
        }

        Count("clean.furnitureDropped", before - blocks.Count);
        Count("clean.pageNumbersDropped", blocks.RemoveAll(b => b.Kind is not BlockKinds.Heading && PageNumber().IsMatch(b.Text.Trim())));
        Count("clean.furnitureDropped", blocks.RemoveAll(b => b.Kind is not BlockKinds.Heading && rule.Furniture.Any(f => f.IsMatch(b.Text.Trim()))));

        // Table of contents: within a block, and across consecutive one-line blocks.
        var tocLines = 0;
        blocks = [.. blocks.Select(b =>
        {
            if (b.Kind is BlockKinds.Code or BlockKinds.Table)
            {
                return b;
            }

            var (text, dropped) = DropTocLines(b.Text);
            tocLines += dropped;
            return b with { Text = text };
        })];
        tocLines += DropTocBlocks(blocks);
        Count("clean.tocDropped", tocLines);

        // Dehyphenation and line breaks inside paragraphs, then whitespace.
        var joined = 0;
        blocks = [.. blocks.Select(b =>
        {
            if (b.Kind is BlockKinds.Code or BlockKinds.Table)
            {
                return b;
            }

            var text = Dehyphenate().Replace(b.Text, m =>
            {
                var head = m.Groups["head"].Value;
                var next = m.Groups["next"].Value;
                if (SuspendedCompoundWords.Contains(next))
                {
                    return head + "- " + next; // "kommun- och regionförbund"
                }

                if (head.All(c => char.IsUpper(c) || char.IsDigit(c)))
                {
                    return head + "-" + next; // "EU-medel"
                }

                joined++;
                return head + next;
            });
            text = SpaceRuns().Replace(text.Replace('\n', ' '), " ").Trim();
            return b with { Text = SwedishParagraph().Replace(text, "§ ") };
        })];
        Count("clean.dehyphenated", joined);

        blocks = [.. blocks.Select(b => b.Kind is BlockKinds.Code
            ? b with { Text = ManyNewlines().Replace(b.Text, "\n\n") }
            : b with { Text = SpaceRuns().Replace(b.Text, " ").Trim() })];

        // Minimum block: non-heading, non-list blocks with fewer than 3 letters or digits.
        Count("clean.minimumDropped", blocks.RemoveAll(b =>
            b.Kind is not (BlockKinds.Heading or BlockKinds.Title or BlockKinds.ListItem or BlockKinds.Table) && b.Text.Count(char.IsLetterOrDigit) < 3));
        blocks.RemoveAll(b => b.Text.Length == 0 && b.Kind != BlockKinds.Table);

        var title = document.Title is null ? null : SpaceRuns().Replace(Unicode(document.Title), " ").Trim();
        var body = string.Join('\n', blocks.Where(b => b.Kind != BlockKinds.Table).Select(b => b.Text));
        var cleaned = document with
        {
            Title = string.IsNullOrEmpty(title) ? null : title,
            Breadcrumb = [.. document.Breadcrumb.Select(c => SpaceRuns().Replace(Unicode(c), " ").Trim()).Where(c => c.Length > 0)],
            Description = document.Description is null ? null : SpaceRuns().Replace(Unicode(document.Description), " ").Trim(),
            Blocks = blocks,
            Tables = tables,
            Language = document.Language ?? LanguageDetector.Detect(body),
            Facts = facts,
        };
        return QualityFacts.ForParsed(cleaned);
    }

    /// <summary>NFC; NFKC only for the ligatures U+FB00–FB06; soft hyphen removed; NBSP, thin and narrow NBSP → space; zero-width characters removed.</summary>
    public static string Unicode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '­' or '​' or '‌' or '‍' or '⁠' or '﻿':
                    break;
                case ' ' or ' ' or ' ' or ' ':
                    builder.Append(' ');
                    break;
                case >= 'ﬀ' and <= 'ﬆ':
                    builder.Append(c.ToString().Normalize(NormalizationForm.FormKC));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string DigitNormalised(string text) => Digits().Replace(text.Trim().ToLower(CultureInfo.InvariantCulture), "0");

    private static (string Text, int Dropped) DropTocLines(string text)
    {
        var lines = text.Split('\n');
        var keep = new bool[lines.Length];
        Array.Fill(keep, true);
        var dropped = 0;
        var i = 0;
        while (i < lines.Length)
        {
            var j = i;
            while (j < lines.Length && TocLine().IsMatch(lines[j].Trim()))
            {
                j++;
            }

            if (j - i >= 4)
            {
                for (var k = i; k < j; k++)
                {
                    keep[k] = false;
                }

                dropped += j - i;
            }

            i = Math.Max(j, i + 1);
        }

        return (string.Join('\n', lines.Where((_, n) => keep[n])), dropped);
    }

    private static int DropTocBlocks(List<DocBlock> blocks)
    {
        var dropped = 0;
        var i = 0;
        while (i < blocks.Count)
        {
            var j = i;
            while (j < blocks.Count && blocks[j].Kind is not (BlockKinds.Code or BlockKinds.Table) && !blocks[j].Text.Contains('\n', StringComparison.Ordinal)
                && TocLine().IsMatch(blocks[j].Text.Trim()))
            {
                j++;
            }

            if (j - i >= 4)
            {
                blocks.RemoveRange(i, j - i);
                dropped += j - i;
                continue;
            }

            i = Math.Max(j, i + 1);
        }

        return dropped;
    }

    [GeneratedRegex(@"^(sida\s*)?\d+(\s*(av|/)\s*\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"^.{3,120}?[ .·…]{3,}\s*\d{1,4}$")]
    private static partial Regex TocLine();

    [GeneratedRegex(@"(?<head>\p{L}{2,})-\n(?<next>\p{Ll}\p{L}*)")]
    private static partial Regex Dehyphenate();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    [GeneratedRegex(@"§(?=\d)")]
    private static partial Regex SwedishParagraph();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
