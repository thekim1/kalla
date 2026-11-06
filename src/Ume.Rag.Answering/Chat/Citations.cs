using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.Rag.Answering.Chat;

/// <summary>An OpenAI <c>url_citation</c> annotation over a marker of the final content.</summary>
public sealed record UrlCitation(string Url, string Title, int StartIndex, int EndIndex);

/// <summary>The delivered text, its annotations, and which prompt sources and retrieval ranks were cited.</summary>
public sealed record CitedAnswer(string Content, IReadOnlyList<UrlCitation> Annotations, IReadOnlyList<PromptSource> Cited, IReadOnlyList<int> CitedRanks);

/// <summary>
/// Markers and renumbering (chat design §9.2–§9.3): <c>[n]</c>, <c>[n, m]</c>, <c>[n][m]</c> and <c>[Källa n]</c>/<c>[Source n]</c>;
/// a prompt number 1..N becomes its display number, the order of the document's first citation; other numbers are removed.
/// Each delivered marker gets a <c>url_citation</c> with the document's link and label.
/// </summary>
public static partial class Citations
{
    public static CitedAnswer Apply(string text, IReadOnlyList<PromptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sources);
        var byNumber = sources.ToDictionary(s => s.Number);
        var display = new Dictionary<int, int>();
        var cited = new List<PromptSource>();
        var output = new StringBuilder();
        var annotations = new List<UrlCitation>();
        var last = 0;
        foreach (Match match in Marker().Matches(text))
        {
            output.Append(text, last, match.Index - last);
            last = match.Index + match.Length;
            var numbers = Number().Matches(match.Value).Select(n => int.Parse(n.Value, CultureInfo.InvariantCulture)).Where(byNumber.ContainsKey).Distinct().ToList();
            if (numbers.Count == 0)
            {
                // Invalid numbers only: removed, with the space before it.
                if (output.Length > 0 && output[^1] == ' ')
                {
                    output.Length--;
                }

                continue;
            }

            foreach (var n in numbers)
            {
                if (!display.TryGetValue(n, out var shown))
                {
                    shown = display.Count + 1;
                    display[n] = shown;
                    cited.Add(byNumber[n]);
                }

                var start = output.Length;
                output.Append(CultureInfo.InvariantCulture, $"[{shown}]");
                var source = byNumber[n];
                annotations.Add(new UrlCitation(Link(source), Label(source), start, output.Length));
            }
        }

        output.Append(text, last, text.Length - last);
        return new CitedAnswer(output.ToString(), annotations, cited, [.. cited.SelectMany(s => s.Chunks.Select(c => c.Rank)).Order()]);
    }

    /// <summary>The document link: its URL, or its download grant link, with <c>#page=</c> of the first cited page for PDFs.</summary>
    public static string Link(PromptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var link = source.Citation.Url ?? source.Citation.DownloadUrl ?? "";
        var page = source.Chunks.Where(c => c.Page is not null).Select(c => c.Page).FirstOrDefault();
        return source.Citation.MediaType == "application/pdf" && page is not null && link.Length > 0 ? $"{link}#page={page}" : link;
    }

    /// <summary>The label (knowledge §10.4): title, the heading path's last part, pages, modified date.</summary>
    public static string Label(PromptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var first = source.Chunks[0];
        var parts = new List<string> { source.Citation.Title };
        var tail = first.HeadingPath.Split('›', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (tail is not null && !string.Equals(tail, source.Citation.Title, StringComparison.Ordinal))
        {
            parts[0] += " – " + tail;
        }

        if (first.Page is { } page)
        {
            parts.Add(first.PageEnd is { } end && end != page ? $"s. {page}–{end}" : $"s. {page}");
        }

        var label = string.Join(", ", parts);
        return source.Citation.Modified is { } modified ? $"{label} ({modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})" : label;
    }

    [GeneratedRegex(@"\[(?:(?:Källa|Source)\s+)?\d+(?:\s*,\s*\d+)*\](?:\[\d+(?:\s*,\s*\d+)*\])*", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
