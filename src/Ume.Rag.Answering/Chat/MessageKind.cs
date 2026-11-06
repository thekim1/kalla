using System.Text.RegularExpressions;

namespace Ume.Rag.Answering.Chat;

/// <summary>Conversational messages (chat design §6.2): short, and no content word; they skip retrieval and the gate.</summary>
public static class MessageKind
{
    private static readonly HashSet<string> Conversational = new(StringComparer.Ordinal)
    {
        "hej", "hejsan", "tjena", "hallå", "tack", "tackar", "ok", "okej", "bra", "toppen", "perfekt", "hello", "hi", "hey", "thanks", "thank", "you", "okay",
        "great", "good", "god", "morgon", "dag", "kväll", "så", "mycket", "jaha", "ja", "nej", "yes", "no", "bye", "hejdå",
    };

    public static bool IsConversational(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (question.Length > 60)
        {
            return false;
        }

        var words = Regex.Split(question.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length > 0).ToList();
        return words.Count > 0 && words.All(Conversational.Contains);
    }
}
