using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kalla.FakeGateway;

/// <summary>
/// Scripted JSON chat answers for the worker's evaluation steps (issue #129, main session's decision), the reranker, the
/// long-question condense and the follow-up suggestions: a chat call with <c>x-fors-step</c> <c>eval-generate</c>,
/// <c>eval-judge</c>, <c>rerank</c>, <c>condense-long</c>, <c>follow-ups</c> or <c>escalation</c> is answered from
/// <c>FakeGateway:ChatScripts:&lt;step&gt;</c> (a string: every call; an array: in order, cycling per step and process), after
/// the usual key, ceiling and target checks. Without a script, built-in answers keep a dev run of <c>generate-set</c>, a
/// reranking assistant, a long question and follow-up suggestions working (<see cref="DefaultRerank"/>: word overlap;
/// <see cref="DefaultCondenseLong"/>: the message's first words; <see cref="DefaultFollowUps"/>: questions on the source titles;
/// <see cref="DefaultEscalation"/>: the ticket's first words in one sentence; all deterministic). No header carries a script, so a caller cannot script the fake. Every other chat call stays the echo answer.
/// </summary>
public sealed partial class FakeChatScripts(IConfiguration configuration)
{
    public const string StepHeader = "x-fors-step";
    public const string GenerateStep = "eval-generate";
    public const string JudgeStep = "eval-judge";
    public const string RerankStep = "rerank";
    public const string CondenseLongStep = "condense-long";
    public const string FollowUpsStep = "follow-ups";
    public const string EscalationStep = "escalation";

    /// <summary>How many of the message's words <see cref="DefaultCondenseLong"/> keeps.</summary>
    public const int CondenseLongWords = 12;

    /// <summary>Its longest answer (a log line may be one long word), within the condense acceptance rule's 400.</summary>
    public const int CondenseLongMaxChars = 200;
    public const string SectionName = FakeGatewayOptions.SectionName + ":ChatScripts";

    /// <summary>The judge's default: every score 5 (passes the worker's minimum of 4).</summary>
    public const string DefaultJudge = """{"besvarbar": 5, "begriplig": 5, "specifik": 5}""";

    private readonly ConcurrentDictionary<string, int> _next = new(StringComparer.Ordinal);

    /// <summary>The scripted assistant content for <paramref name="step"/>, or null for the echo answer.</summary>
    public string? Answer(string? step, string userText)
    {
        if (string.IsNullOrEmpty(step))
        {
            return null;
        }

        var section = configuration.GetSection($"{SectionName}:{step}");
        // A parent key can read as "" next to its array items (in-memory and host settings), so empty is not a script.
        if (section.Value is { Length: > 0 } single)
        {
            return single;
        }

        var items = section.GetChildren()
            .OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue)
            .Select(c => c.Value)
            .OfType<string>()
            .ToList();
        if (items.Count > 0)
        {
            var index = _next.AddOrUpdate(step, 0, (_, n) => n + 1);
            return items[index % items.Count];
        }

        return step switch
        {
            GenerateStep => DefaultGeneration(userText),
            JudgeStep => DefaultJudge,
            RerankStep => DefaultRerank(userText),
            CondenseLongStep => DefaultCondenseLong(userText),
            FollowUpsStep => DefaultFollowUps(userText),
            EscalationStep => DefaultEscalation(userText),
            _ => null,
        };
    }

    /// <summary>
    /// A generation answer (processing §13.1 prompt shape <c>{"fraga", "svar", "typ"}</c>) built from the prompt's
    /// <c>Text: …</c>: "Vad gäller i praktiken för A B C?" with three content words of the chunk (8 words, ends in <c>?</c>,
    /// no deictic word, under the copied-text limit), and the chunk's first sentence as the answer.
    /// </summary>
    public static string DefaultGeneration(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        var match = ChunkText().Match(userText);
        var text = (match.Success ? match.Groups[1].Value : userText).Trim();
        var words = Word().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 5).Distinct(StringComparer.Ordinal).Take(3).ToList();
        while (words.Count < 3)
        {
            words.Add(words.Count switch { 0 => "tjänsten", 1 => "ansökan", _ => "avgiften" });
        }

        var question = $"Vad gäller i praktiken för {string.Join(' ', words)}?";
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        var answer = (end > 0 ? text[..(end + 1)] : text).Trim();
        if (answer.Length == 0)
        {
            answer = "Se kommunens information.";
        }

        return JsonSerializer.Serialize(new Dictionary<string, string> { ["fraga"] = question, ["svar"] = answer, ["typ"] = "fakta" });
    }

    /// <summary>
    /// The reranker's answer to <c>ChatModelReranker</c>'s prompt (<c>&lt;question&gt;</c>, then <c>&lt;passage id="n"&gt;</c>
    /// blocks): <c>{"scores":[{"id":n,"score":s}]}</c> for every passage, s = round(10 × the share of the question's words the
    /// passage contains), as the fake's <c>/v1/rerank</c> scores. Deterministic.
    /// </summary>
    public static string DefaultRerank(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        var question = RerankQuestion().Match(userText) is { Success: true } q ? q.Groups[1].Value : "";
        var scores = RerankPassage().Matches(userText)
            .Select(m => new Dictionary<string, int>
            {
                ["id"] = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                ["score"] = (int)Math.Round(10 * FakeContent.Overlap(question, m.Groups[2].Value), MidpointRounding.AwayFromZero),
            })
            .ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["scores"] = scores });
    }

    /// <summary>
    /// The long-question condense's answer (chat design §5.1: the transcript, then <c>Meddelande: </c> and the message): the
    /// message's first <see cref="CondenseLongWords"/> words on one line (at most <see cref="CondenseLongMaxChars"/> characters), as a
    /// search query is. Deterministic.
    /// </summary>
    public static string DefaultCondenseLong(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        var message = CondenseLongMessage().Match(userText) is { Success: true } m ? m.Groups[1].Value : userText;
        var query = string.Join(' ', Token().Matches(message).Take(CondenseLongWords).Select(t => t.Value));
        return query.Length == 0 ? "Supportärende" : query.Length > CondenseLongMaxChars ? query[..CondenseLongMaxChars] : query;
    }

    /// <summary>
    /// The follow-up call's answer (chat design §9.8: <c>Fråga: </c>, <c>Svar: </c>, then <c>Källor:</c> and one title per line):
    /// one question per line, first on each of the first two titles (cut to 60 characters), then general ones, five lines in all,
    /// without numbering. Deterministic.
    /// </summary>
    public static string DefaultFollowUps(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        var titles = FollowUpTitles().Match(userText) is { Success: true } m
            ? m.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        string[] general = ["Hur går jag vidare med det här?", "Vem kan jag kontakta om jag har fler frågor?", "Finns det något mer jag behöver tänka på?",
            "Var hittar jag mer information?", "Hur lång tid tar det?"];
        return string.Join('\n', titles.Take(2).Select(t => $"Vad mer behöver jag veta om {(t.Length > 60 ? t[..60] : t)}?").Concat(general).Take(5));
    }

    /// <summary>
    /// An escalation summary (chat design §9.9): "Användaren behöver hjälp med: " and the first 20 words of the message's
    /// <c>Ärende: </c> section, ending in a full stop. Deterministic.
    /// </summary>
    public static string DefaultEscalation(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        var ticket = EscalationTicket().Match(userText) is { Success: true } m ? m.Groups[1].Value : userText;
        var words = ticket.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(20);
        return "Användaren behöver hjälp med: " + string.Join(' ', words).TrimEnd('.', '?', '!') + ".";
    }

    [GeneratedRegex(@"Ärende: (.*?)(?:\n\n|\z)", RegexOptions.Singleline)]
    private static partial Regex EscalationTicket();

    [GeneratedRegex(@"<question>\s*(.*?)\s*</question>", RegexOptions.Singleline)]
    private static partial Regex RerankQuestion();

    [GeneratedRegex(@"<passage id=""(\d+)"">\s*(.*?)\s*</passage>", RegexOptions.Singleline)]
    private static partial Regex RerankPassage();

    [GeneratedRegex(@"Text:\s*(.*)\z", RegexOptions.Singleline)]
    private static partial Regex ChunkText();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    [GeneratedRegex(@"Meddelande: (.*)\z", RegexOptions.Singleline)]
    private static partial Regex CondenseLongMessage();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Token();

    [GeneratedRegex(@"\nKällor:\n(.*)\z", RegexOptions.Singleline)]
    private static partial Regex FollowUpTitles();
}
