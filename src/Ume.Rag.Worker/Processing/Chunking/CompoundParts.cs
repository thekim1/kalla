using Ume.Rag.Retrieval.Text;

namespace Ume.Rag.Worker.Processing.Chunking;

/// <summary>
/// <see cref="SwedishDecompounder.PartsOf"/> with each word's <see cref="SwedishDecompounder.Split"/> memoised, for one
/// chunking run: the prefix repeats in every chunk of a section and the overlap repeats the previous chunk's words. Words
/// are the same runs of <see cref="char.IsLetter(char)"/> as <see cref="SwedishDecompounder.PartsOf"/> uses.
/// </summary>
internal sealed class CompoundParts(SwedishDecompounder decompounder)
{
    private readonly Dictionary<string, IReadOnlyList<string>> _splits = new(StringComparer.Ordinal);

    /// <summary>The distinct parts of every compound in <paramref name="texts"/> joined by a non-letter, in order of appearance.</summary>
    public List<string> Of(params ReadOnlySpan<string> texts)
    {
        var splits = _splits.GetAlternateLookup<ReadOnlySpan<char>>();
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && char.IsLetter(text[i]))
                {
                    if (start < 0)
                    {
                        start = i;
                    }

                    continue;
                }

                if (start < 0)
                {
                    continue;
                }

                var word = text.AsSpan(start, i - start);
                start = -1;
                if (!splits.TryGetValue(word, out var split))
                {
                    var key = word.ToString();
                    split = decompounder.Split(key);
                    _splits[key] = split;
                }

                foreach (var part in split)
                {
                    if (seen.Add(part))
                    {
                        parts.Add(part);
                    }
                }
            }
        }

        return parts;
    }
}
