using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.Rag.Answering.Chat;

/// <summary>A piece of the delivered answer: a document's first citation (emitted before the text that cites it), or text.</summary>
public abstract record AnswerPiece
{
    /// <summary>The first citation of a document: its display number and prompt source.</summary>
    public sealed record FirstCitation(int Display, PromptSource Source) : AnswerPiece;

    /// <summary>Delivered text and the <c>url_citation</c>s of its markers, indexed over the whole delivered content.</summary>
    public sealed record Text(string Content, IReadOnlyList<UrlCitation> Annotations) : AnswerPiece;
}

/// <summary>
/// The answer transducer of chat design §9.2–§9.4, for streams and whole texts alike: model text goes in delta by delta;
/// what comes out has its <c>[n]</c> markers renumbered by first citation (invalid numbers removed), images and active HTML
/// removed, links to hosts that are not allowed de-linked, and bare URLs to such hosts wrapped in code. A construct still
/// open at the end of a delta is held back (≤ 2,048 characters; beyond that it is let through as text), so a marker or a
/// link split across deltas is handled whole.
/// </summary>
public sealed partial class AnswerTransducer
{
    public const int MaxHeld = 2048;

    private readonly Dictionary<int, PromptSource> _byNumber;
    private readonly Dictionary<int, int> _display = [];
    private readonly List<PromptSource> _cited = [];
    private readonly HashSet<string> _allowedHosts;
    private readonly StringBuilder _held = new();
    private int _delivered;
    private char _lastDelivered;

    public AnswerTransducer(IReadOnlyList<PromptSource> sources, IEnumerable<string> allowedLinkHosts)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(allowedLinkHosts);
        _byNumber = sources.ToDictionary(s => s.Number);
        // Links to the cited documents' own hosts are always allowed (§9.4).
        _allowedHosts = allowedLinkHosts.Concat(sources.Select(s => Host(s.Citation.Url ?? s.Citation.DownloadUrl)).OfType<string>())
            .Select(h => h.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The documents cited so far, in display order.</summary>
    public IReadOnlyList<PromptSource> Cited => _cited;

    /// <summary>The retrieval ranks of the cited documents' chunks (for <c>MarkCited</c>, §9.5).</summary>
    public IReadOnlyList<int> CitedRanks => [.. _cited.SelectMany(s => s.Chunks.Select(c => c.Rank)).Order()];

    /// <summary>Counts of what the filter changed, by kind (<c>image</c>, <c>html</c>, <c>link</c>, <c>url</c>, <c>invalid_marker</c>).</summary>
    public Dictionary<string, int> Filtered { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<AnswerPiece> Feed(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        _held.Append(delta);
        var text = _held.ToString();
        var safe = SafeLength(text);
        if (safe == 0)
        {
            return [];
        }

        _held.Remove(0, safe);
        return Process(text[..safe]);
    }

    public IReadOnlyList<AnswerPiece> Flush()
    {
        var text = _held.ToString();
        _held.Clear();
        return text.Length == 0 ? [] : Process(text);
    }

    /// <summary>
    /// How much of <paramref name="text"/> can be decided now: up to the start of the last construct that may still be open
    /// (<c>[</c>, <c>!</c> before <c>[</c>, <c>&lt;</c>, or a URL at the very end), unless it is held longer than <see cref="MaxHeld"/>.
    /// </summary>
    private static int SafeLength(string text)
    {
        // The first construct that is still open decides; everything before it is final.
        var cut = text.Length;
        for (var open = text.IndexOfAny(['[', '<', '!']); open >= 0; open = text.IndexOfAny(['[', '<', '!'], open + 1))
        {
            if (!Closed(text, open))
            {
                cut = open;
                break;
            }
        }

        // A space before a held construct stays with it, so a removed marker takes its space along (§9.2).
        if (cut > 0 && cut < text.Length && text[cut - 1] == ' ')
        {
            cut--;
        }

        // A URL running to the end may continue in the next delta.
        var url = TrailingUrl().Match(text);
        if (url.Success && url.Index < cut)
        {
            cut = url.Index;
        }

        return text.Length - cut > MaxHeld ? text.Length : cut;
    }

    /// <summary>Whether the construct starting at <paramref name="at"/> is complete within <paramref name="text"/>.</summary>
    private static bool Closed(string text, int at)
    {
        var rest = text.AsSpan(at);
        switch (rest[0])
        {
            case '<':
                return rest.IndexOf('>') >= 0;
            case '!':
                // "!" ends the text: an image may follow; "!x": not an image; "![": the bracket decides.
                return rest.Length >= 2 && (rest[1] != '[' || Closed(text, at + 1));
            default: // '['
                var close = rest.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }

                // "[text]" followed by "(" is a link: closed only with its ")"; at the very end "(" may still come.
                var after = rest[(close + 1)..];
                if (after.Length == 0)
                {
                    return !char.IsLetter(rest.Length > 1 ? rest[1] : ' ') && IsMarkerText(rest[..(close + 1)]);
                }

                return after[0] != '(' || Balanced(after);
        }
    }

    private static bool IsMarkerText(ReadOnlySpan<char> bracketed) => MarkerOnly().IsMatch(bracketed);

    /// <summary>Whether the link target starting with "(" is closed by its matching ")" (one nested pair, as in <c>alert(1)</c>).</summary>
    private static bool Balanced(ReadOnlySpan<char> target)
    {
        var depth = 0;
        foreach (var c in target)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The filter is escape-by-default (#203 review): images and listed tags are removed; a Markdown link is kept only when its
    /// target is http(s) to an allowed host (protected by a placeholder through the rest); then every remaining <c>&lt;</c> is
    /// escaped (no HTML, no autolinks, no attributes), every remaining <c>](</c> broken (nothing unvalidated becomes a link,
    /// whatever its shape: nested brackets, titles, spaces, long text), and line-leading reference definitions escaped.
    /// </summary>
    private List<AnswerPiece> Process(string text)
    {
        text = text.Replace(Placeholder, "", StringComparison.Ordinal);
        text = Images().Replace(text, m => Count("image", $"[{m.Groups["alt"].Value}]"));
        text = ActiveHtml().Replace(text, m => Count("html", ""));
        var kept = new List<string>();
        text = Links().Replace(text, m =>
        {
            var url = m.Groups["url"].Value.Trim();
            if (Unsafe(url) || !Allowed(url))
            {
                return Count("link", m.Groups["text"].Value);
            }

            kept.Add(m.Value);
            return $"{Placeholder}{kept.Count - 1}{Placeholder}";
        });
        if (text.Contains('<', StringComparison.Ordinal))
        {
            Count("escaped_html", "");
            text = text.Replace("<", "&lt;", StringComparison.Ordinal);
        }

        text = BareUrls().Replace(text, m => Allowed(m.Value) ? m.Value : Count("url", $"`{m.Value}`"));
        if (text.Contains("](", StringComparison.Ordinal))
        {
            Count("broken_link", "");
            text = text.Replace("](", "]\\(", StringComparison.Ordinal);
        }

        if (_lastDelivered == ']' && text.StartsWith('('))
        {
            text = "\\" + text; // "]" ended the previous piece: "(" here would complete an unvalidated link
        }

        text = ReferenceDefinition().Replace(text, m => Count("reference", m.Groups["indent"].Value + "\\["));
        text = Restore().Replace(text, m => kept[int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture)]);

        var pieces = new List<AnswerPiece>();
        var output = new StringBuilder();
        var annotations = new List<UrlCitation>();
        var last = 0;
        foreach (Match match in Marker().Matches(text))
        {
            output.Append(text, last, match.Index - last);
            last = match.Index + match.Length;
            var numbers = Number().Matches(match.Value).Select(n => int.Parse(n.Value, CultureInfo.InvariantCulture)).Where(_byNumber.ContainsKey).Distinct().ToList();
            if (numbers.Count == 0)
            {
                Count("invalid_marker", "");
                if (output.Length > 0 && output[^1] == ' ')
                {
                    output.Length--;
                }

                continue;
            }

            foreach (var n in numbers)
            {
                if (!_display.TryGetValue(n, out var shown))
                {
                    shown = _display.Count + 1;
                    _display[n] = shown;
                    _cited.Add(_byNumber[n]);
                    // The first citation goes out before the text that holds it (Open WebUI numbers sources in emission order).
                    if (output.Length > 0)
                    {
                        pieces.Add(Piece(output, annotations));
                    }

                    pieces.Add(new AnswerPiece.FirstCitation(shown, _byNumber[n]));
                }

                var start = _delivered + output.Length;
                output.Append(CultureInfo.InvariantCulture, $"[{shown}]");
                annotations.Add(new UrlCitation(Citations.Link(_byNumber[n]), Citations.Label(_byNumber[n]), start, _delivered + output.Length));
            }
        }

        output.Append(text, last, text.Length - last);
        if (output.Length > 0 || annotations.Count > 0)
        {
            pieces.Add(Piece(output, annotations));
        }

        return pieces;
    }

    private AnswerPiece.Text Piece(StringBuilder output, List<UrlCitation> annotations)
    {
        var piece = new AnswerPiece.Text(output.ToString(), [.. annotations]);
        if (output.Length > 0)
        {
            _lastDelivered = output[^1];
        }

        _delivered += output.Length;
        output.Clear();
        annotations.Clear();
        return piece;
    }

    private string Count(string kind, string replacement)
    {
        Filtered[kind] = Filtered.GetValueOrDefault(kind) + 1;
        return replacement;
    }

    private bool Allowed(string url) => Host(url) is { } host && (_allowedHosts.Contains(host) || _allowedHosts.Any(a => a.StartsWith("*.", StringComparison.Ordinal) &&
        (host.EndsWith(a[1..], StringComparison.Ordinal) || host == a[2..])));

    private static bool Unsafe(string url) =>
        url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase);

    private static string? Host(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.Host.ToLowerInvariant() : null;

    /// <summary>Marks a validated link while the escaping runs; removed from the model text first, so it cannot be forged.</summary>
    private const string Placeholder = "\u0001";

    [GeneratedRegex(@"\u0001(?<i>\d+)\u0001")]
    private static partial Regex Restore();

    [GeneratedRegex(@"(?m)^(?<indent>[ \t]{0,3})\[(?=[^\]\n]*\]:)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"!\[(?<alt>[^\]]{0,500})\]\((?:[^()\s]|\([^()\s]{0,500}\)){0,2000}\)", RegexOptions.CultureInvariant)]
    private static partial Regex Images();

    [GeneratedRegex(@"</?\s*(img|picture|svg|iframe|object|embed|script|style|link|meta|video|audio|source|form|a)\b[^>]{0,2000}>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveHtml();

    [GeneratedRegex(@"\[(?<text>[^\]\[]{1,500})\]\((?<url>(?:[^()\s]|\([^()\s]{0,500}\)){1,2000})\)", RegexOptions.CultureInvariant)]
    private static partial Regex Links();

    [GeneratedRegex(@"(?<![(`<\w])https?://[^\s)\]>`]+", RegexOptions.CultureInvariant)]
    private static partial Regex BareUrls();

    [GeneratedRegex(@"https?://\S*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingUrl();

    [GeneratedRegex(@"\[(?:(?:Källa|Source)\s+)?\d+(?:\s*,\s*\d+)*\](?:\[\d+(?:\s*,\s*\d+)*\])*", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^\[(?:(?:Källa|Source)\s+)?\d+(?:\s*,\s*\d+)*\]$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerOnly();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
