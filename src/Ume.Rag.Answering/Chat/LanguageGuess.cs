using System.Text.RegularExpressions;

namespace Ume.Rag.Answering.Chat;

/// <summary>Swedish or English (chat design §10.3): function-word scores, Swedish unless English clearly wins.</summary>
public static class LanguageGuess
{
    public const string Swedish = "sv";
    public const string English = "en";

    private static readonly HashSet<string> SwedishWords = new(StringComparer.Ordinal)
    {
        "och", "att", "det", "jag", "hur", "vad", "kan", "för", "inte", "en", "ett", "är", "som", "på", "med", "har", "om", "till", "av", "den",
        "vi", "du", "man", "när", "var", "vilka", "vilken", "måste", "får", "ska", "behöver", "finns", "min", "mitt", "mina", "från", "eller",
    };

    private static readonly HashSet<string> EnglishWords = new(StringComparer.Ordinal)
    {
        "the", "and", "how", "what", "can", "is", "are", "do", "does", "i", "you", "to", "of", "in", "for", "my", "where", "when", "which",
        "need", "have", "with", "on", "it", "this", "that", "be", "should", "must", "there", "from", "or", "who", "why",
    };

    public static string Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var swedish = 0;
        var english = 0;
        foreach (var word in Regex.Split(text.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length > 0))
        {
            swedish += SwedishWords.Contains(word) ? 1 : 0;
            swedish += word.Any(c => c is 'å' or 'ä' or 'ö') ? 2 : 0;
            english += EnglishWords.Contains(word) ? 1 : 0;
        }

        return english >= 2 && english >= 2 * swedish ? English : Swedish;
    }
}
