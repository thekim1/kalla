using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ume.Rag.FakeGateway;

/// <summary>
/// Scripted JSON chat answers for the worker's evaluation steps (issue #129, main session's decision): a chat call with
/// <c>x-ume-step</c> <c>eval-generate</c> or <c>eval-judge</c> is answered from <c>FakeGateway:ChatScripts:&lt;step&gt;</c>
/// (a string: every call; an array: in order, cycling per step and process), after the usual key, ceiling and target
/// checks. Without a script, built-in answers keep a dev run of <c>generate-set</c> working. No header carries a script,
/// so a caller cannot script the fake. Every other chat call stays the echo answer.
/// </summary>
public sealed partial class FakeChatScripts(IConfiguration configuration)
{
    public const string StepHeader = "x-ume-step";
    public const string GenerateStep = "eval-generate";
    public const string JudgeStep = "eval-judge";
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

    [GeneratedRegex(@"Text:\s*(.*)\z", RegexOptions.Singleline)]
    private static partial Regex ChunkText();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();
}
