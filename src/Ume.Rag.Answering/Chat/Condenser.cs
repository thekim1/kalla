using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ume.Rag.Domain;
using Ume.Rag.Retrieval;

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

/// <summary>Why condense did not rewrite (chat design §5, §5.1): counted, and a failed rewrite never yields the not-found text.</summary>
public enum CondenseOutcome
{
    Rewritten,
    SkippedFirstMessage,
    SkippedSelfContained,
    Disabled,
    Timeout,
    Error,
    Rejected,

    /// <summary>The answer came from a residency above the call's ceiling (event 5003) and was discarded.</summary>
    Residency,
}

/// <summary>
/// Condense (chat design §5): a follow-up that is not self-contained is rewritten into a standalone search query by one small
/// non-streaming call (<c>temperature 0</c>, ≤ 96 tokens, hard timeout), used for retrieval only. P1a runs it on the platform
/// condense model at ceiling <c>Eu</c>, never above the turn's own ceiling (P1 README §1: no on-prem models yet; a
/// <c>sensitive</c> turn therefore has no condense model and uses the raw question). A question over 2,000 characters is
/// condensed into its search query on every turn instead (§5.1, <see cref="LongMessages"/>), else cut (<see cref="Fallback"/>).
/// </summary>
public static partial class Condenser
{
    public const int MaxOutputTokens = 96;
    /// <summary>The default of <c>Chat:CondenseTimeout</c> (#287: 1,200 ms; no EU model met 600 ms).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>The default of <c>Chat:LongQuestionCondenseTimeout</c> (§5.1).</summary>
    public static readonly TimeSpan DefaultLongTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The <c>x-ume-step</c> of the follow-up condense (§5) and of the long-question condense (§5.1).</summary>
    public const string Step = "condense";
    public const string LongStep = "condense-long";

    /// <summary>How far back a long question's fallback query may be cut to end at a space (§5.1).</summary>
    private const int FallbackWordWindow = 200;
    private const int TranscriptMaxChars = 4000;

    private const string Instructions =
        "Du skriver om en följdfråga till en fristående sökfråga.\n" +
        "Använd samtalet bara för att lösa upp syftningar (det, den, där, samma, hen) och utelämnade ämnen.\n" +
        "Lägg inte till fakta, svara inte på frågan, behåll språket i användarens fråga.\n" +
        "Skriv endast den omskrivna frågan på en rad. Är frågan redan fristående: upprepa den oförändrad.";

    private const string LongInstructions =
        "Du får ett långt meddelande, ofta ett supportärende med e-posttråd eller loggar.\n" +
        "Skriv en kort sökfråga som beskriver det problem eller den fråga som ska besvaras.\n" +
        "Ta med felkoder, felmeddelanden och namn på system och program ordagrant.\n" +
        "Ta aldrig med namn på personer, personnummer, e-postadresser, telefonnummer eller andra personuppgifter.\n" +
        "Lägg inte till fakta, svara inte på frågan, behåll språket i meddelandet.\n" +
        "Skriv endast sökfrågan på en rad.";

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
        var body = Transcript(history);
        var tail = "\nFöljdfråga: " + question;
        if (body.Length + tail.Length > TranscriptMaxChars)
        {
            body = body[^Math.Max(0, TranscriptMaxChars - tail.Length)..];
        }

        return [new ChatMessageIn("system", Instructions), new ChatMessageIn("user", body + tail)];
    }

    /// <summary>
    /// The long-question condense messages (§5.1 "Input"): the §5 transcript of the last ≤ 6 messages (its cuts keep it under
    /// 4,000 characters), then <c>Meddelande: </c> and the question's query form, which is never cut.
    /// </summary>
    public static IReadOnlyList<ChatMessageIn> LongMessages(IReadOnlyList<ChatMessageIn> history, string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var body = Transcript(history);
        return [new ChatMessageIn("system", LongInstructions), new ChatMessageIn("user", (body.Length > 0 ? body + "\n" : "") + "Meddelande: " + question)];
    }

    /// <summary>
    /// A long question's search query when condense gives none (§5.1 "Fallback"): the first <see cref="QueryNormalizer.MaxLength"/>
    /// characters of the query form, cut back to the last space when one lies in their last 200 characters, else cut there.
    /// </summary>
    public static string Fallback(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var end = 0;
        var count = 0;
        foreach (var rune in question.EnumerateRunes())
        {
            if (count == QueryNormalizer.MaxLength)
            {
                break;
            }

            end += rune.Utf16SequenceLength;
            count++;
        }

        if (end == question.Length)
        {
            return question;
        }

        var space = question.LastIndexOf(' ', end - 1, Math.Min(end, FallbackWordWindow));
        return space > 0 ? question[..space] : question[..end];
    }

    /// <summary>The cleaned transcript (§5 "Input"): the last ≤ 6 messages, user ≤ 500 and assistant ≤ 600 characters without markers and links.</summary>
    private static string Transcript(IReadOnlyList<ChatMessageIn> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var transcript = new StringBuilder();
        foreach (var message in history.TakeLast(6))
        {
            var text = message.Role == "assistant" ? Links().Replace(PromptBuilder.StripMarkers(message.Text), "$1") : message.Text;
            var max = message.Role == "assistant" ? 600 : 500;
            transcript.Append(message.Role == "assistant" ? "Assistent: " : "Användare: ").Append(text.Length > max ? text[..max] : text).Append('\n');
        }

        return transcript.ToString();
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

    /// <summary>The <c>outcome</c> of <c>rag.chat.condense</c> and the reason in the logs (chat design §17).</summary>
    public static string OutcomeName(CondenseOutcome outcome) => outcome switch
    {
        CondenseOutcome.Rewritten => "used",
        CondenseOutcome.SkippedFirstMessage => "skipped",
        CondenseOutcome.SkippedSelfContained => "skipped_self_contained",
        CondenseOutcome.Disabled => "disabled",
        CondenseOutcome.Timeout => "timeout",
        CondenseOutcome.Residency => "residency",
        CondenseOutcome.Rejected => "rejected",
        _ => "error",
    };

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Links();

    [GeneratedRegex(@"^(jag kan inte|jag kan tyvärr inte|tyvärr kan jag|i cannot|i can't|i'm sorry|sorry,)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Refusal();
}
