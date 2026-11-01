using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ume.Rag.Domain;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// The platform's chat settings (<c>Settings.Chat</c>, chat design §5, §9.4, §10.1, §4.1 step 4), read from the api's
/// snapshot with the deployment knobs (<see cref="ChatOptions"/>) as the fallback: <c>condenseModel</c>,
/// <c>allowedLinkHosts</c>, <c>messagesPerMinutePerUser</c> and the default <c>notFound</c> texts.
/// </summary>
/// <remarks><c>condenseReasoningEffort</c> is sent as <c>reasoning_effort</c> on the condense call when set (#287: a reasoning
/// model otherwise spends its 96 tokens on hidden reasoning); null sends nothing.</remarks>
public sealed record PlatformChatSettings(string? CondenseModel, IReadOnlyList<string> AllowedLinkHosts, int MessagesPerMinutePerUser, string? NotFound,
    string? CondenseReasoningEffort = null)
{
    public static PlatformChatSettings Parse(string? settingsChat, ChatOptions fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        JsonObject? json = null;
        try
        {
            json = string.IsNullOrWhiteSpace(settingsChat) ? null : JsonNode.Parse(settingsChat) as JsonObject;
        }
        catch (JsonException)
        {
            // An unreadable section falls back to the deployment knobs (the admin validates on save).
        }

        var hosts = (json?["allowedLinkHosts"] as JsonArray)?.Select(h => h is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null)
            .OfType<string>().ToList();
        return new PlatformChatSettings(
            json?["condenseModel"] is JsonValue m && m.GetValueKind() == JsonValueKind.String && m.GetValue<string>() is { Length: > 0 } model ? model : null,
            hosts is { Count: > 0 } ? hosts : fallback.AllowedLinkHosts,
            json?["messagesPerMinutePerUser"] is JsonValue r && r.GetValueKind() == JsonValueKind.Number && r.TryGetValue<int>(out var rate) ? rate : fallback.MessagesPerMinutePerUser,
            json?["notFound"] is JsonObject notFound ? notFound.ToJsonString() : null,
            json?["condenseReasoningEffort"] is JsonValue e && e.GetValueKind() == JsonValueKind.String && e.GetValue<string>() is { Length: > 0 and <= 20 } effort ? effort : null);
    }
}

/// <summary>Why condense did not rewrite (chat design §5): counted, and a failed rewrite never yields the not-found text.</summary>
public enum CondenseOutcome
{
    Rewritten,
    SkippedFirstMessage,
    SkippedSelfContained,
    Disabled,
    Timeout,
    Error,
    Rejected,
}

/// <summary>
/// Condense (chat design §5): a follow-up that is not self-contained is rewritten into a standalone search query by one small
/// non-streaming call (<c>temperature 0</c>, ≤ 96 tokens, hard timeout), used for retrieval only. P1a runs it on the platform
/// condense model at ceiling <c>Eu</c>, never above the turn's own ceiling (P1 README §1: no on-prem models yet; a
/// <c>sensitive</c> turn therefore has no condense model and uses the raw question).
/// </summary>
public static partial class Condenser
{
    public const int MaxOutputTokens = 96;
    /// <summary>The default of <c>Chat:CondenseTimeout</c> (#287: 1,200 ms; no EU model met 600 ms).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1200);

    private const string Instructions =
        "Du skriver om en följdfråga till en fristående sökfråga.\n" +
        "Använd samtalet bara för att lösa upp syftningar (det, den, där, samma, hen) och utelämnade ämnen.\n" +
        "Lägg inte till fakta, svara inte på frågan, behåll språket i användarens fråga.\n" +
        "Skriv endast den omskrivna frågan på en rad. Är frågan redan fristående: upprepa den oförändrad.";

    private static readonly HashSet<string> Anaphora = new(StringComparer.Ordinal)
    {
        "det", "den", "dem", "detta", "denna", "dessa", "där", "dit", "då", "samma", "hen", "han", "hon", "sådan", "också",
        "it", "this", "that", "these", "those", "there", "they", "same", "also",
    };

    /// <summary>Whether to condense: enabled, an earlier user message exists, and the question is not self-contained.</summary>
    public static CondenseOutcome? Skip(bool enabled, IReadOnlyList<ChatMessageIn> history, string question)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(question);
        if (!enabled)
        {
            return CondenseOutcome.Disabled;
        }

        if (!history.Any(m => m.Role == "user"))
        {
            return CondenseOutcome.SkippedFirstMessage;
        }

        var words = Regex.Split(question.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
        return words.Count >= 6 && !words.Any(Anaphora.Contains) ? CondenseOutcome.SkippedSelfContained : null;
    }

    /// <summary>The condense messages (§5 "Input"): the last ≤ 6 messages, cut and cleaned, then the question; ≤ 4,000 characters.</summary>
    public static IReadOnlyList<ChatMessageIn> Messages(IReadOnlyList<ChatMessageIn> history, string question)
    {
        ArgumentNullException.ThrowIfNull(history);
        var transcript = new StringBuilder();
        foreach (var message in history.TakeLast(6))
        {
            var text = message.Role == "assistant" ? Links().Replace(PromptBuilder.StripMarkers(message.Text), "$1") : message.Text;
            var max = message.Role == "assistant" ? 600 : 500;
            transcript.Append(message.Role == "assistant" ? "Assistent: " : "Användare: ").Append(text.Length > max ? text[..max] : text).Append('\n');
        }

        var body = transcript.ToString();
        var tail = "\nFöljdfråga: " + question;
        if (body.Length + tail.Length > 4000)
        {
            body = body[^Math.Max(0, 4000 - tail.Length)..];
        }

        return [new ChatMessageIn("system", Instructions), new ChatMessageIn("user", body + tail)];
    }

    /// <summary>§5 "Accept the output if": 2–400 characters after trim, one line, no refusal pattern, no task header.</summary>
    public static string? Accept(string output)
    {
        var text = (output ?? "").Trim().Trim('"');
        if (text.Length is < 2 or > 400 || text.Contains('\n', StringComparison.Ordinal) || text.Contains("### Task", StringComparison.OrdinalIgnoreCase) ||
            Refusal().IsMatch(text))
        {
            return null;
        }

        return text;
    }

    /// <summary>The condense ceiling: <c>Eu</c> in P1a, never above the turn's ceiling.</summary>
    public static Residency Ceiling(Residency turn) => turn < Residency.Eu ? turn : Residency.Eu;

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Links();

    [GeneratedRegex(@"^(jag kan inte|jag kan tyvärr inte|tyvärr kan jag|i cannot|i can't|i'm sorry|sorry,)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Refusal();
}
