using System.Text;

namespace Ume.Rag.Retrieval.Text;

/// <summary>
/// Splits Swedish compounds into their parts (simplification pass §3, data-model §3.1), so a search for a part finds
/// the compound through Postgres' built-in <c>swedish</c> configuration: the worker stores
/// <c>Chunks."SearchText"</c> = text plus <see cref="PartsOf"/>, and the query side runs the same before building the
/// keyword query. Driven by the <c>sv_SE</c> Hunspell word list: a compound is a begin part, any middle parts and an end
/// part, each allowed in its position by the list's compound flags; linking forms ("bygglovs", "förskole") come from
/// the list's own suffix rules. The fewest parts win, then the longest first part; every part has at least
/// <see cref="MinPartLength"/> letters, and no part directly repeats ("bil" + "bil").
/// </summary>
public sealed class SwedishDecompounder
{
    /// <summary>Words longer than this are not split (bounds the search; Swedish words of this length are rare).</summary>
    public const int MaxWordLength = 64;

    private const string ResourcePrefix = "Ume.Rag.Retrieval.Text.sv_SE.";
    private static readonly Lazy<SwedishDecompounder> Embedded = new(LoadEmbedded);
    private readonly HunspellWordList _dictionary;

    public SwedishDecompounder(HunspellWordList dictionary, int minPartLength = 3)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentOutOfRangeException.ThrowIfLessThan(minPartLength, 1);
        _dictionary = dictionary;
        MinPartLength = minPartLength;
    }

    /// <summary>The decompounder over the embedded <c>sv_SE</c> word list (loaded once, on first use).</summary>
    public static SwedishDecompounder Default => Embedded.Value;

    public int MinPartLength { get; }

    /// <summary>
    /// The parts (dictionary stems) of <paramref name="word"/> if it is a compound of at least two parts, otherwise
    /// empty. Lower-case.
    /// </summary>
    public IReadOnlyList<string> Split(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var w = word.ToLowerInvariant();
        if (w.Length < 2 * MinPartLength || w.Length > MaxWordLength)
        {
            return [];
        }

        var tails = new Dictionary<int, List<string>?>();
        List<string>? best = null;
        for (var firstLength = w.Length - MinPartLength; firstLength >= MinPartLength; firstLength--)
        {
            if (_dictionary.StemFor(w[..firstLength], _dictionary.Begin) is not { } first)
            {
                continue;
            }

            if (Tail(w, firstLength, first, tails) is { } tail && (best is null || tail.Count + 1 < best.Count))
            {
                best = [first, .. tail];
            }
        }

        return best ?? [];
    }

    /// <summary>The distinct parts of every compound in <paramref name="text"/>, in order of appearance.</summary>
    public IReadOnlyList<string> PartsOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Words(text))
        {
            foreach (var part in Split(word))
            {
                if (seen.Add(part))
                {
                    parts.Add(part);
                }
            }
        }

        return parts;
    }

    /// <summary><paramref name="text"/> followed by the parts of its compounds: the form stored as <c>SearchText</c>.</summary>
    public string WithParts(string text)
    {
        var parts = PartsOf(text);
        return parts.Count == 0 ? text : text + "\n" + string.Join(' ', parts);
    }

    /// <summary>The best (fewest parts) split of <c>w[start..]</c> into middle parts and an end part, or null.</summary>
    private List<string>? Tail(string w, int start, string previous, Dictionary<int, List<string>?> memo)
    {
        if (!memo.TryGetValue(start, out var best))
        {
            best = BestTail(w, start, memo);
            memo[start] = best;
        }

        // Duplication check against the part before this tail.
        return best is not null && best[0] == previous ? null : best;
    }

    private List<string>? BestTail(string w, int start, Dictionary<int, List<string>?> memo)
    {
        var remaining = w.Length - start;
        if (remaining < MinPartLength)
        {
            return null;
        }

        if (_dictionary.StemFor(w[start..], _dictionary.End) is { } end)
        {
            return [end];
        }

        List<string>? best = null;
        for (var length = remaining - MinPartLength; length >= MinPartLength; length--)
        {
            if (_dictionary.StemFor(w.Substring(start, length), _dictionary.Middle) is not { } middle)
            {
                continue;
            }

            if (Tail(w, start + length, middle, memo) is { } tail && (best is null || tail.Count + 1 < best.Count))
            {
                best = [middle, .. tail];
            }
        }

        return best;
    }

    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static SwedishDecompounder LoadEmbedded()
    {
        var assembly = typeof(SwedishDecompounder).Assembly;
        using var affix = new StreamReader(assembly.GetManifestResourceStream(ResourcePrefix + "sv_SE.aff")
            ?? throw new InvalidOperationException("Embedded sv_SE.aff not found."), Encoding.UTF8);
        using var dictionary = new StreamReader(assembly.GetManifestResourceStream(ResourcePrefix + "sv_SE.dic")
            ?? throw new InvalidOperationException("Embedded sv_SE.dic not found."), Encoding.UTF8);
        return new SwedishDecompounder(HunspellWordList.Load(affix, dictionary));
    }
}
