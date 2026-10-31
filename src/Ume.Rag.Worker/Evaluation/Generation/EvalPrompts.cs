using System.Text.Json;

namespace Ume.Rag.Worker.Evaluation.Generation;

/// <summary>A generated candidate as the model wrote it.</summary>
public sealed record GeneratedCandidate(string Question, string Answer, string? Type);

/// <summary>
/// The Swedish prompts of processing design §13.1, versioned as <see cref="PromptVersion"/> (stored in each question's
/// <c>Origin</c>), and the readers of their JSON answers. A change to a prompt bumps the version.
/// </summary>
public static class EvalPrompts
{
    public const string PromptVersion = "eval-sv-1";
    public const double GenerateTemperature = 0.3;
    public const double JudgeTemperature = 0;

    public static IReadOnlyList<string> Types { get; } = ["fakta", "procedur", "definition", "tabell"];

    public const string GenerateSystem = "Du skriver testfrågor för en sökfunktion hos Umeå kommun.";

    public const string JudgeSystem = "Du granskar testfrågor för en sökfunktion hos Umeå kommun.";

    public static string GenerateUser(string title, string headingPath, string text) => $$"""
        Dokument: {{title}}. Avsnitt: {{headingPath}}. Text: {{text}}.
        Skriv en fråga som en invånare eller medarbetare skulle ställa och som texten besvarar.
        Regler: fristående (inga "texten", "dokumentet", "ovan"), 6–25 ord, på svenska, citera inte texten ordagrant,
        inga personuppgifter. Svara med JSON: {"fraga": "...", "svar": "...", "typ": "fakta|procedur|definition|tabell"}.
        """;

    /// <summary>The judge's prompt (filter 3, and the re-check of a changed document, §13.3).</summary>
    public static string JudgeUser(string title, string headingPath, string text, string question, string answer) => $$"""
        Dokument: {{title}}. Avsnitt: {{headingPath}}. Text: {{text}}.
        Fråga: {{question}}
        Förväntat svar: {{answer}}
        Bedöm frågan från 1 till 5 på tre sätt: besvarbar (texten besvarar frågan), begriplig (frågan går att förstå utan
        texten), specifik (frågan gäller just detta innehåll). Svara med JSON: {"besvarbar": 1, "begriplig": 1, "specifik": 1}.
        """;

    /// <summary>The candidate, or null when the answer is not the asked JSON with a question and an answer.</summary>
    public static GeneratedCandidate? ReadCandidate(string content)
    {
        if (Object(content) is not { } root)
        {
            return null;
        }

        using (root)
        {
            var question = Text(root.RootElement, "fraga");
            var answer = Text(root.RootElement, "svar");
            if (question is null || answer is null)
            {
                return null;
            }

            var type = Text(root.RootElement, "typ");
            return new GeneratedCandidate(question, answer, type is not null && Types.Contains(type, StringComparer.Ordinal) ? type : null);
        }
    }

    /// <summary>The judge's ratings, or null when any is missing or outside 1–5.</summary>
    public static JudgeScores? ReadScores(string content)
    {
        if (Object(content) is not { } root)
        {
            return null;
        }

        using (root)
        {
            return Score(root.RootElement, "besvarbar") is { } answerable && Score(root.RootElement, "begriplig") is { } understandable
                && Score(root.RootElement, "specifik") is { } specific
                ? new JudgeScores(answerable, understandable, specific)
                : null;
        }
    }

    /// <summary>The JSON object of an answer; a Markdown code fence around it is tolerated.</summary>
    private static JsonDocument? Object(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            text = start >= 0 && end > start ? text[start..(end + 1)] : string.Empty;
        }

        try
        {
            var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } s && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : null;

    private static int? Score(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        && Math.Round(number) is var rounded && rounded is >= 1 and <= 5
            ? (int)rounded
            : null;
}
