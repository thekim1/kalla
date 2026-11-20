using System.Text;
using Kalla.Retrieval.Text;

namespace Kalla.Retrieval;

/// <summary>
/// The query as embedded and searched (knowledge design §5.1): control characters become spaces, Unicode NFC (as the
/// chunks, processing §11), whitespace runs collapse to one space, trimmed. No lower-casing, no instruction prefix, no
/// rewriting. The normalised string is what is embedded and what the cache key is computed from.
/// </summary>
public static class QueryNormalizer
{
    /// <summary>The longest query accepted, in characters (Unicode scalar values) after normalisation.</summary>
    public const int MaxLength = 2000;

    /// <summary>The normalised form of <paramref name="query"/>; may be empty.</summary>
    public static string Normalize(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cleaned = new StringBuilder(query.Length);
        for (var i = 0; i < query.Length; i++)
        {
            var c = query[i];
            if (char.IsHighSurrogate(c) && i + 1 < query.Length && char.IsLowSurrogate(query[i + 1]))
            {
                cleaned.Append(c).Append(query[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                cleaned.Append('\uFFFD'); // a lone surrogate: NFC would throw on it
            }
            else
            {
                cleaned.Append(char.IsControl(c) ? ' ' : c);
            }
        }

        var nfc = cleaned.ToString().Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(nfc.Length);
        var pendingSpace = false;
        foreach (var c in nfc)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// The normalised query, or an <see cref="ArgumentException"/> when it is empty or longer than
    /// <see cref="MaxLength"/> characters after normalisation.
    /// </summary>
    public static string NormalizeAndValidate(string query)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The query is empty after normalisation.", nameof(query));
        }

        if (Length(normalized) > MaxLength)
        {
            throw new ArgumentException($"The query is longer than {MaxLength} characters.", nameof(query));
        }

        return normalized;
    }

    /// <summary>Length in Unicode scalar values (a surrogate pair counts once).</summary>
    public static int Length(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The keyword statement's query search text (knowledge design §5.1): the normalised query plus the compound parts
    /// <paramref name="decompounder"/> finds, the rule the worker uses for <c>Chunks."SearchText"</c>
    /// (<see cref="SwedishDecompounder.WithParts"/>). Without a decompounder (the evaluation's
    /// <c>hybrid-no-decompounder</c> variant) the normalised query alone.
    /// </summary>
    public static string SearchText(string normalizedQuery, SwedishDecompounder? decompounder) =>
        decompounder is null ? normalizedQuery : decompounder.WithParts(normalizedQuery);
}
