using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ume.Rag.Retrieval;

namespace Ume.Rag.Answering.Chat;

/// <summary>A chat refusal in the OpenAI error shape (chat design §3.6): HTTP status, <c>code</c>, English message, <c>param</c>.</summary>
public sealed class ChatException(int status, string code, string message, string? param = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Param { get; } = param;

    /// <summary><c>Retry-After</c> seconds to pass on (gateway 429/503).</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>The gateway answered from a residency above the call's ceiling (event 5003 raised); the answer was discarded.</summary>
    public bool ResidencyRefused { get; init; }
}

/// <summary>One message of the client's conversation as plain text (content parts concatenated).</summary>
public sealed record ChatMessageIn(string Role, string Text);

/// <summary>
/// A parsed <c>POST /v1/chat/completions</c> body (chat design §3.2). <see cref="Question"/> is the question's query form
/// (whitespace collapsed, up to <see cref="ChatRequestParser.MaxQuestionChars"/>): message kind, language, condense and
/// retrieval read it. <see cref="PromptQuestion"/> is its prompt form (line breaks kept), what the answer model reads (§7.1);
/// it defaults to <see cref="Question"/>.
/// </summary>
public sealed record ParsedChat(string Model, string Question, IReadOnlyList<ChatMessageIn> History, bool Stream, bool IncludeUsage, int? MaxTokens)
{
    public string PromptQuestion { get; init; } = Question;

    /// <summary>The request's <c>ume</c> object (§3.8), or null: absent, <c>null</c>, or a client that does not read it (Open WebUI).</summary>
    public UmeRequest? Ume { get; init; }
}

/// <summary>
/// The <c>ume</c> extension asked for (chat design §3.8): how many related documents to list (§9.7) and the most follow-up
/// questions to suggest (§9.8); 0 is none. Any object, <c>{}</c> included, gives the answer's <c>ume</c> with its outcome.
/// </summary>
public sealed record UmeRequest(int RelatedDocuments, int FollowUps)
{
    public const int MaxRelatedDocuments = 10;
    public const int MaxFollowUps = 5;
}

/// <summary>
/// Parses and checks the request body (chat design §3.2): unknown members are ignored, except inside <c>ume</c> (§3.8); a fixed list of members is refused;
/// text content parts only; the last message must be <c>user</c>; <c>system</c>/<c>developer</c>/<c>tool</c> messages are
/// ignored; the question gets its query form (normalised as the knowledge API normalises queries) and its prompt form.
/// </summary>
public static partial class ChatRequestParser
{
    public const int MaxMessages = 1000;
    public const int MaxMessageChars = 32_000;

    /// <summary>The longest question, in characters of its query form (chat design §3.2, §5.1); longer than 2,000 is condensed for retrieval.</summary>
    public const int MaxQuestionChars = 16_000;

    private static readonly string[] RefusedMembers = ["logprobs", "top_logprobs", "audio", "modalities", "prediction"];
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "system", "developer", "user", "assistant", "tool" };

    /// <summary>
    /// The parsed body. <paramref name="readUme"/> false (an Open WebUI client, §3.8) ignores <c>ume</c> without validating it.
    /// </summary>
    public static ParsedChat Parse(JsonElement body, bool readUme = true)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The body must be a JSON object.");
        }

        var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : throw Invalid("model is required.", "model");
        foreach (var member in RefusedMembers)
        {
            if (body.TryGetProperty(member, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            {
                throw new ChatException(400, "unsupported_parameter", $"{member} is not supported.", member);
            }
        }

        if (body.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() != 1)
        {
            throw new ChatException(400, "unsupported_parameter", "Only n = 1 is supported.", "n");
        }

        foreach (var tools in new[] { "tools", "functions" })
        {
            if (body.TryGetProperty(tools, out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0)
            {
                throw new ChatException(400, "tools_not_supported", "Tools are not supported.", tools);
            }
        }

        if (body.TryGetProperty("response_format", out var format) && format.ValueKind == JsonValueKind.Object &&
            !(format.TryGetProperty("type", out var type) && type.GetString() == "text"))
        {
            throw new ChatException(400, "unsupported_parameter", "Only the text response format is supported.", "response_format");
        }

        var ume = readUme && body.TryGetProperty("ume", out var u) ? Ume(u) : null;

        if (!body.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is < 1 or > MaxMessages)
        {
            throw Invalid($"messages must hold 1 to {MaxMessages} items.", "messages");
        }

        var parsed = new List<ChatMessageIn>();
        foreach (var message in messages.EnumerateArray())
        {
            var role = message.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
            if (!Roles.Contains(role))
            {
                throw Invalid("Each message needs a role of system, developer, user, assistant or tool.", "messages");
            }

            var text = Content(message);
            if (text.Length > MaxMessageChars)
            {
                throw Invalid($"A message is longer than {MaxMessageChars} characters.", "messages");
            }

            parsed.Add(new ChatMessageIn(role, text));
        }

        var last = parsed[^1];
        if (last.Role == "assistant")
        {
            throw new ChatException(400, "continue_not_supported", "Continuing an assistant message is not supported; ask a new question.", "messages");
        }

        if (last.Role != "user")
        {
            throw Invalid("The last message must be from the user.", "messages");
        }

        var question = QueryNormalizer.Normalize(last.Text);
        if (question.Length == 0 || QueryNormalizer.Length(question) > MaxQuestionChars)
        {
            throw new ChatException(400, question.Length == 0 ? "invalid_request" : "question_too_long", $"The question must be 1–{MaxQuestionChars} characters.",
                "messages");
        }

        var history = parsed.Take(parsed.Count - 1).Where(x => x.Role is "user" or "assistant").ToList();
        var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        var usage = body.TryGetProperty("stream_options", out var so) && so.ValueKind == JsonValueKind.Object && so.TryGetProperty("include_usage", out var iu) &&
            iu.ValueKind == JsonValueKind.True;
        int? maxTokens = null;
        foreach (var name in new[] { "max_completion_tokens", "max_tokens" })
        {
            if (body.TryGetProperty(name, out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var value) && value > 0)
            {
                maxTokens = value;
                break;
            }
        }

        return new ParsedChat(model, question, history, stream, usage, maxTokens) { PromptQuestion = PromptForm(last.Text), Ume = ume };
    }

    /// <summary>
    /// The question's prompt form (§3.2): line breaks (<c>\r\n</c> and <c>\r</c> read as <c>\n</c>) and tabs kept, other control
    /// characters become spaces, Unicode NFC, runs of more than two blank lines collapsed to two, trimmed.
    /// </summary>
    public static string PromptForm(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cleaned.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                cleaned.Append('\uFFFD'); // a lone surrogate: NFC would throw on it (as QueryNormalizer)
            }
            else if (c == '\r')
            {
                cleaned.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                cleaned.Append(c is '\n' or '\t' || !char.IsControl(c) ? c : ' ');
            }
        }

        return BlankLines().Replace(cleaned.ToString().Normalize(NormalizationForm.FormC), "\n\n\n").Trim();
    }

    /// <summary>
    /// The <c>ume</c> object (§3.8): <c>null</c> is none; not an object is 400 <c>invalid_request</c>; <c>related_documents</c>
    /// 0–10 and <c>follow_ups</c> 0–5 as integers, else 400 <c>invalid_request</c>; any other member is 400
    /// <c>unsupported_parameter</c> (the namespace is ours, so a typo fails instead of doing nothing).
    /// </summary>
    private static UmeRequest? Ume(JsonElement ume)
    {
        if (ume.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (ume.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("ume must be an object.", "ume");
        }

        var (related, followUps) = (0, 0);
        foreach (var member in ume.EnumerateObject())
        {
            switch (member.Name)
            {
                case "related_documents":
                    related = UmeCount(member, UmeRequest.MaxRelatedDocuments);
                    break;
                case "follow_ups":
                    followUps = UmeCount(member, UmeRequest.MaxFollowUps);
                    break;
                default:
                    throw new ChatException(400, "unsupported_parameter", $"ume.{member.Name} is not supported.", "ume." + member.Name);
            }
        }

        return new UmeRequest(related, followUps);
    }

    private static int UmeCount(JsonProperty member, int max) =>
        member.Value.ValueKind == JsonValueKind.Number && member.Value.TryGetInt32(out var count) && count >= 0 && count <= max
            ? count
            : throw Invalid($"ume.{member.Name} must be an integer from 0 to {max}.", "ume." + member.Name);

    /// <summary>A string, or text parts joined with a newline; any other part is refused (answered 6).</summary>
    private static string Content(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
        {
            return "";
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("content must be a string or a list of parts.", "messages");
        }

        var texts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "text")
            {
                throw new ChatException(400, "attachments_not_supported", "Only text content is supported; attachments are not.", "messages");
            }

            texts.Add(part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "");
        }

        return string.Join('\n', texts);
    }

    private static ChatException Invalid(string message, string? param = null) => new(400, "invalid_request", message, param);

    /// <summary>More than two blank lines (lines of spaces and tabs count as blank).</summary>
    [GeneratedRegex(@"\n(?:[ \t]*\n){3,}")]
    private static partial Regex BlankLines();
}
