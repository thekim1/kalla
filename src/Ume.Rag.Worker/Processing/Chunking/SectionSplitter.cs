using System.Globalization;
using System.Text.RegularExpressions;
using Ume.Rag.Worker.Processing.Parse;

namespace Ume.Rag.Worker.Processing.Chunking;

/// <summary>A section of the split (processing design §5.1); <see cref="Blocks"/> may be empty (a heading that only contributes to paths).</summary>
public sealed record Section(int Ordinal, int Level, IReadOnlyList<string> HeadingPath, string? Heading, IReadOnlyList<DocBlock> Blocks, int? PageStart, int? PageEnd);

/// <summary>
/// <c>Split by headings</c> (processing design §5, part of <c>chunk</c>): the title is level 0 and the first element of
/// every path unless the first heading repeats it; a heading opens a section at its level and closes every open section
/// at that level or deeper; blocks before the first heading belong to the level-0 section. <c>layout</c> and
/// <c>font_size</c> levels are re-levelled from the heading text by Swedish numbering (§5.2); implausible headings are
/// demoted; levels deeper than the parse rule's <c>maxLevel</c> become bold paragraphs.
/// </summary>
public static partial class SectionSplitter
{
    public static IReadOnlyList<Section> Split(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rule = ParseRules.Of(document);
        var title = document.Title ?? document.Blocks.FirstOrDefault(b => b.Kind == BlockKinds.Title)?.Text;
        var blocks = Relevel(document.Blocks.Where(b => b.Kind != BlockKinds.Title).ToList(), rule);

        var sections = new List<Section>();
        var open = new List<(int Level, string Heading)>();
        var current = new List<DocBlock>();
        int currentLevel = 0;
        string? currentHeading = null;
        var firstHeading = true;

        IReadOnlyList<string> Path() =>
            [.. (title is null ? Array.Empty<string>() : [title]), .. open.Select(o => o.Heading)];

        void Close()
        {
            var pages = current.Where(b => b.Page is not null).ToList();
            sections.Add(new Section(sections.Count, currentLevel, Path(), currentHeading, [.. current],
                pages.Count == 0 ? null : pages.Min(b => b.Page), pages.Count == 0 ? null : pages.Max(b => b.PageEnd ?? b.Page)));
            current.Clear();
        }

        foreach (var block in blocks)
        {
            if (block.Kind != BlockKinds.Heading)
            {
                current.Add(block);
                continue;
            }

            var level = block.Level ?? 1;
            if (firstHeading && title is not null && string.Equals(block.Text, title, StringComparison.OrdinalIgnoreCase))
            {
                firstHeading = false;
                continue; // repeats the title: the level-0 section goes on
            }

            firstHeading = false;
            if (current.Count > 0 || currentHeading is not null)
            {
                Close();
            }

            open.RemoveAll(o => o.Level >= level);
            open.Add((level, block.Text));
            currentLevel = level;
            currentHeading = block.Text;
        }

        if (current.Count > 0 || currentHeading is not null || sections.Count == 0)
        {
            Close();
        }

        return sections;
    }

    /// <summary>Re-levelling, plausibility and <c>maxLevel</c> (§5.2); trusted levels pass through.</summary>
    internal static List<DocBlock> Relevel(List<DocBlock> blocks, ParseRule rule)
    {
        var result = new List<DocBlock>(blocks.Count);
        var lastNumberedLevel = 0;
        var chapterLevel = 0;
        var lastNumber = new Dictionary<string, int[]>(StringComparer.Ordinal); // parent number → last number at that depth
        foreach (var block in blocks)
        {
            if (block.Kind != BlockKinds.Heading)
            {
                result.Add(block);
                continue;
            }

            var text = block.Text.Trim();
            if (text.Length > 200 || (text.EndsWith('.') && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 12))
            {
                result.Add(block with { Kind = BlockKinds.Paragraph, Level = null });
                continue;
            }

            int level;
            if (LevelSources.IsTrusted(block.LevelSource))
            {
                level = block.Level ?? 1;
            }
            else if (Chapter().IsMatch(text) || Appendix().IsMatch(text) || Roman().IsMatch(text))
            {
                level = 1;
                lastNumberedLevel = chapterLevel = 1;
            }
            else if (Numbered().Match(text) is { Success: true } numbered)
            {
                var parts = numbered.Groups["n"].Value.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                var parent = string.Join('.', parts[..^1]);
                var backwards = lastNumber.TryGetValue(parent, out var previous) && previous.Length == parts.Length && previous[^1] > parts[^1];
                if (backwards)
                {
                    level = block.Level ?? 1; // a list item misread as a heading keeps its layout level
                }
                else
                {
                    level = parts.Length;
                    lastNumber[parent] = parts;
                    lastNumberedLevel = chapterLevel = level;
                }
            }
            else if (Paragraph().IsMatch(text))
            {
                level = rule.ParagraphItemsAreSections ? 1 : chapterLevel + 1;
                lastNumberedLevel = level;
            }
            else
            {
                level = lastNumberedLevel + 1;
            }

            result.Add(level > rule.MaxLevel
                ? block with { Kind = BlockKinds.Paragraph, Text = "**" + text + "**", Level = null }
                : block with { Level = level, Text = text });
        }

        return result;
    }

    [GeneratedRegex(@"^((Kapitel|KAPITEL)\s+\d+|\d+\s+kap\.)")]
    private static partial Regex Chapter();

    [GeneratedRegex(@"^Bilaga(\s+\d+)?\b")]
    private static partial Regex Appendix();

    [GeneratedRegex(@"^(?<n>\d{1,2}(\.\d{1,2}){0,4})\.?\s+\p{Lu}")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^§\s*\d+\s*[a-z]?\b")]
    private static partial Regex Paragraph();

    [GeneratedRegex(@"^([IVX]{1,5}|[A-ZÅÄÖ])\.\s")]
    private static partial Regex Roman();
}

/// <summary>
/// The Swedish sentence splitter of processing design §6.2 rule 1: a boundary after <c>.</c>, <c>!</c>, <c>?</c> or
/// <c>:</c> followed by space and an upper-case letter or digit, except after the abbreviation list, single capitals as
/// initials and <c>§</c> references.
/// </summary>
public static partial class SentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "t.ex.", "bl.a.", "m.m.", "s.k.", "kap.", "nr.", "ca.", "dvs.", "o.s.v.", "fr.o.m.", "t.o.m.", "jfr.", "resp.", "enl.",
    };

    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sentences = new List<string>();
        var start = 0;
        foreach (Match m in Boundary().Matches(text))
        {
            var end = m.Index + 1;
            var wordStart = text.LastIndexOfAny([' ', '\n', '\t', '('], m.Index) + 1;
            var word = text[wordStart..end].ToLower(CultureInfo.InvariantCulture);
            if (Abbreviations.Contains(word) || Initial().IsMatch(text[wordStart..end]) || AfterParagraphSign(text, wordStart))
            {
                continue;
            }

            if (text[start..end].Trim() is { Length: > 0 } sentence)
            {
                sentences.Add(sentence);
            }

            start = end;
        }

        if (text[start..].Trim() is { Length: > 0 } last)
        {
            sentences.Add(last);
        }

        return sentences;
    }

    /// <summary>"enligt § 4. Det" and "4 §." keep together: the word before the period is a number next to a § sign.</summary>
    private static bool AfterParagraphSign(string text, int wordStart)
    {
        var before = text[..wordStart].TrimEnd();
        return before.EndsWith('§') || text[wordStart..].StartsWith('§');
    }

    [GeneratedRegex(@"[.!?:](?=\s+[\p{Lu}\d])")]
    private static partial Regex Boundary();

    [GeneratedRegex(@"^\p{Lu}\.$")]
    private static partial Regex Initial();
}
