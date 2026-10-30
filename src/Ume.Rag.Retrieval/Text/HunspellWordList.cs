using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Ume.Rag.Retrieval.Text;

/// <summary>
/// The parts of a Hunspell dictionary that compound splitting needs: the stems with their flags, the suffix rules, and
/// the flags that mark where a word may stand in a compound (<c>COMPOUNDBEGIN</c>, <c>COMPOUNDMIDDLE</c>,
/// <c>COMPOUNDEND</c>, <c>ONLYINCOMPOUND</c>). Single-character flags only (no <c>FLAG</c> directive), as in
/// <c>sv_SE</c>. Prefix rules, <c>COMPOUNDRULE</c> (numbers) and replacement tables are not used.
/// </summary>
public sealed class HunspellWordList
{
    private readonly FrozenDictionary<string, string> _stems;
    private readonly FrozenDictionary<string, SuffixRule[]> _rulesByAffix;
    private readonly int _longestAffix;

    private HunspellWordList(Dictionary<string, string> stems, List<SuffixRule> rules, char begin, char middle, char end, char onlyInCompound)
    {
        _stems = stems.ToFrozenDictionary(StringComparer.Ordinal);
        _rulesByAffix = rules.GroupBy(r => r.Affix, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        _longestAffix = rules.Count == 0 ? 0 : rules.Max(r => r.Affix.Length);
        Begin = begin;
        Middle = middle;
        End = end;
        OnlyInCompound = onlyInCompound;
    }

    public char Begin { get; }
    public char Middle { get; }
    public char End { get; }
    public char OnlyInCompound { get; }

    public int StemCount => _stems.Count;

    /// <summary>Reads an <c>.aff</c> and a <c>.dic</c> file (UTF-8).</summary>
    public static HunspellWordList Load(TextReader affix, TextReader dictionary)
    {
        ArgumentNullException.ThrowIfNull(affix);
        ArgumentNullException.ThrowIfNull(dictionary);

        char begin = '\0', middle = '\0', end = '\0', onlyInCompound = '\0';
        var rules = new List<SuffixRule>();
        for (var line = affix.ReadLine(); line is not null; line = affix.ReadLine())
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].StartsWith('#'))
            {
                continue;
            }

            switch (fields[0])
            {
                case "COMPOUNDBEGIN": begin = fields[1][0]; break;
                case "COMPOUNDMIDDLE": middle = fields[1][0]; break;
                case "COMPOUNDEND": end = fields[1][0]; break;
                case "ONLYINCOMPOUND": onlyInCompound = fields[1][0]; break;
                case "FLAG": throw new NotSupportedException($"Hunspell FLAG {fields[1]} is not supported; only single-character flags.");
                // "SFX flag cross count" is a header (four fields); a rule has at least five.
                case "SFX" when fields.Length >= 5: rules.Add(SuffixRule.Parse(fields)); break;
            }
        }

        var stems = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;
        for (var line = dictionary.ReadLine(); line is not null; line = dictionary.ReadLine())
        {
            // The first line is the approximate word count.
            if (first)
            {
                first = false;
                continue;
            }

            var entry = line.Split('\t', ' ')[0].Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var stem = (slash < 0 ? entry : entry[..slash]).ToLowerInvariant();
            var flags = slash < 0 ? "" : entry[(slash + 1)..];
            // A stem may be listed more than once (e.g. "skola" as noun and verb): merge the flags.
            stems[stem] = stems.TryGetValue(stem, out var existing) ? existing + flags : flags;
        }

        return new HunspellWordList(stems, rules, begin, middle, end, onlyInCompound);
    }

    /// <summary>
    /// The stem through which <paramref name="form"/> may stand in a compound in the position of
    /// <paramref name="positionFlag"/> (<see cref="Begin"/>, <see cref="Middle"/> or <see cref="End"/>), or null. A form
    /// qualifies as a stem carrying the flag, as a suffixed form whose rule gives the flag (linking forms such as
    /// "bygglovs"), or, at the end, as an inflected form of a stem carrying the flag ("omsorgen").
    /// </summary>
    public string? StemFor(string form, char positionFlag)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (_stems.TryGetValue(form, out var flags) && flags.Contains(positionFlag, StringComparison.Ordinal))
        {
            return form;
        }

        for (var length = Math.Min(_longestAffix, form.Length - 1); length >= 0; length--)
        {
            if (!_rulesByAffix.TryGetValue(form[^length..], out var rules))
            {
                continue;
            }

            foreach (var rule in rules)
            {
                var stem = string.Concat(form.AsSpan(0, form.Length - length), rule.Strip);
                if (!_stems.TryGetValue(stem, out var stemFlags) || !stemFlags.Contains(rule.Flag, StringComparison.Ordinal) ||
                    !rule.Condition.IsMatch(stem))
                {
                    continue;
                }

                var formFlags = rule.ContinuationFlags;
                if (formFlags.Contains(positionFlag, StringComparison.Ordinal))
                {
                    return stem;
                }

                // Inflection at the end of a compound: an ordinary form (not a compound-only linking form) of a stem
                // that may end a compound.
                if (positionFlag == End && !formFlags.Contains(OnlyInCompound, StringComparison.Ordinal) &&
                    stemFlags.Contains(End, StringComparison.Ordinal))
                {
                    return stem;
                }
            }
        }

        return null;
    }

    private sealed record SuffixRule(char Flag, string Strip, string Affix, string ContinuationFlags, Regex Condition)
    {
        public static SuffixRule Parse(string[] fields)
        {
            // SFX flag strip add[/continuation] condition
            var strip = fields[2] == "0" ? "" : fields[2];
            var add = fields[3];
            var slash = add.IndexOf('/', StringComparison.Ordinal);
            var continuation = slash < 0 ? "" : add[(slash + 1)..];
            add = slash < 0 ? add : add[..slash];
            add = add == "0" ? "" : add;
            var condition = fields[4] == "." ? "" : fields[4];
            return new SuffixRule(fields[1][0], strip, add, continuation,
                new Regex(condition + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }
    }
}
