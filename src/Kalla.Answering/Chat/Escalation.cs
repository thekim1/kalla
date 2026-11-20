using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kalla.Answering.Chat;

/// <summary>One contact link of the escalation route (chat design §9.9): the assistant's not-found links, else the platform's.</summary>
public sealed record EscalationContact(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url);

/// <summary>
/// The escalation hand-off (chat design §9.9): the turn's outcome as the <c>reason</c>, the contact links, and the hand-off
/// <c>summary</c> (null when skipped, timed out, failed or rejected). Never stored or logged.
/// </summary>
public sealed record EscalationAnswer(string Reason, IReadOnlyList<EscalationContact> Contacts, string? Summary);

/// <summary>
/// The <c>kalla.escalation</c> member's value (§3.8): the hand-off object, or JSON <c>null</c> for a turn that needs none (an
/// <c>answered</c> or conversational turn). A wrapper, so the member is written as <c>null</c> by serializers that leave out null
/// members (the stream's), and so <c>summary</c> is written as <c>null</c> too; the names are fixed whatever the naming policy.
/// </summary>
[JsonConverter(typeof(EscalationMemberConverter))]
public sealed record EscalationMember(EscalationAnswer? Value);

public sealed class EscalationMemberConverter : JsonConverter<EscalationMember>
{
    public override EscalationMember Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The escalation member is written only.");

    public override void Write(Utf8JsonWriter writer, EscalationMember value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value?.Value is not { } escalation)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("reason", escalation.Reason);
        writer.WriteStartArray("contacts");
        foreach (var contact in escalation.Contacts)
        {
            writer.WriteStartObject();
            writer.WriteString("title", contact.Title);
            writer.WriteString("url", contact.Url);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (escalation.Summary is { } summary)
        {
            writer.WriteString("summary", summary);
        }
        else
        {
            writer.WriteNull("summary");
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// The escalation hand-off's summary (chat design §9.9, Decisions "Escalation hand-off"): one non-streaming call to the platform
/// condense model at the condense ceiling (<c>x-fors-step: escalation</c>, <c>max_tokens</c> 200) from the user's last message,
/// the search query when it was condensed, and one line naming the outcome; never the answer text. The output passes
/// <see cref="Accept"/>: links and markers removed, personal identity numbers, e-mail addresses and phone numbers masked.
/// </summary>
public static partial class Escalation
{
    /// <summary>The <c>x-fors-step</c> of the call (opaque to the gateway, gateway-changes §7.1).</summary>
    public const string Step = "escalation";

    /// <summary>The default of <c>Chat:EscalationTimeout</c> (§9.9; 0 switches the summary off).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public const int MaxTokens = 200;
    public const int QuestionMaxChars = 4000;
    public const int SummaryMaxChars = 600;
    public const int SummaryMinChars = 20;

    /// <summary>What a masked value is replaced by.</summary>
    public const string Mask = "[…]";

    /// <summary>The <c>summary</c> tag of <c>rag.chat.escalation</c> (§17); the call's failures use the condense outcome names.</summary>
    public const string Used = "used";
    public const string Rejected = "rejected";
    public const string Skipped = "skipped";

    /// <summary>Whether a turn with <paramref name="outcome"/> gets the hand-off object; any other gets <c>"escalation": null</c>.</summary>
    public static bool Applies(ChatOutcome outcome) => outcome is ChatOutcome.NotFound or ChatOutcome.AnsweredGeneral or ChatOutcome.Unavailable;

    /// <summary>
    /// The call's messages (§9.9 "Input", "Language"): the system prompt (the editable block, the locked lines, the language
    /// line) and the user message <c>Ärende: </c> + the user's last message (≤ 4,000 characters, cut at the last space),
    /// <c>Sökfråga: </c> + the search query when it differs from it, and <c>Utfall: </c> + the outcome's fixed line.
    /// </summary>
    public static IReadOnlyList<ChatMessageIn> Messages(ChatOutcome outcome, string language, string question, string? query, PromptTexts? texts = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        var editable = (texts ?? PromptTexts.Default)[PromptTexts.Escalation].TrimEnd('\n', '\r');
        var system = string.Join('\n', [editable, .. PromptTexts.Locked.Escalation,
            language == LanguageGuess.English ? PromptTexts.Locked.EscalationEnglish : PromptTexts.Locked.EscalationSwedish]);
        var asked = Cut(question.Trim(), QuestionMaxChars);
        var user = new StringBuilder("Ärende: ").Append(asked);
        if (query is { Length: > 0 } && !string.Equals(query.Trim(), question.Trim(), StringComparison.Ordinal))
        {
            user.Append("\n\nSökfråga: ").Append(query.Trim());
        }

        user.Append("\n\nUtfall: ").Append(outcome switch
        {
            ChatOutcome.NotFound => "Inget svar hittades i källorna.",
            ChatOutcome.AnsweredGeneral => "Besvarat med allmän kunskap, utan källor.",
            _ => "Sökningen var otillgänglig.",
        });
        return [new ChatMessageIn("system", system), new ChatMessageIn("user", user.ToString())];
    }

    /// <summary>
    /// The accept rule (§9.9 "Accept"): trimmed; Markdown links reduced to their text, bare URLs and citation markers removed;
    /// e-mail addresses, personal identity and coordination numbers and phone numbers replaced by <see cref="Mask"/>; cut to
    /// 600 characters at the last sentence end or space. Error codes, KB numbers and versions are kept. Null when shorter than
    /// 20 characters or a refusal.
    /// </summary>
    public static string? Accept(string? output)
    {
        var text = (output ?? "").Trim();
        text = MarkdownLink().Replace(text, "$1");
        text = BareUrl().Replace(text, "");
        text = Marker().Replace(text, "");
        text = Email().Replace(text, Mask);
        text = PersonalNumber().Replace(text, Mask);
        text = Phone().Replace(text, Mask);
        text = Spaces().Replace(text, " ").Trim();
        if (text.Length > SummaryMaxChars)
        {
            var head = text[..SummaryMaxChars];
            var end = Math.Max(Math.Max(head.LastIndexOf(". ", StringComparison.Ordinal), head.LastIndexOf("! ", StringComparison.Ordinal)),
                head.LastIndexOf("? ", StringComparison.Ordinal));
            text = end > 0 ? head[..(end + 1)] : head.LastIndexOf(' ') is > 0 and var space ? head[..space] : head;
        }

        return text.Length < SummaryMinChars || Condenser.IsRefusal(text) ? null : text;
    }

    /// <summary>The first <paramref name="max"/> characters, cut at the last space before them when there is one.</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var space = text.LastIndexOf(' ', max - 1);
        return space > 0 ? text[..space] : text[..max];
    }

    [GeneratedRegex(@"!?\[([^\]\n]*)\]\([^)\n]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\[\s*\d[^\]]*\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex Email();

    /// <summary>Personal identity and coordination numbers: 10 or 12 digits, with or without the <c>-</c>/<c>+</c> separator.</summary>
    [GeneratedRegex(@"(?<![\w-])(?:19|20)?\d{6}[-+]?\d{4}(?![\w-])")]
    private static partial Regex PersonalNumber();

    /// <summary>Phone numbers: a leading 0 or +46 and 6–11 more digits, spaces and hyphens allowed.</summary>
    [GeneratedRegex(@"(?<![\w+])(?:\+46|0)(?:[\s-]*\d){6,11}(?![\w])")]
    private static partial Regex Phone();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
