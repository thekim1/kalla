using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.Rag.Answering.Chat;

/// <summary>Not-found and unavailable texts (chat design §10.1, §10.2): the assistant's text in the language, else the platform default, plus links.</summary>
public static class FixedTexts
{
    public const string DefaultNotFoundSv = "Jag hittar inget svar på det i de källor jag har tillgång till.";
    public const string DefaultNotFoundEn = "I cannot find an answer to that in the sources I have access to.";
    public const string UnavailableSv = "Sökningen är tillfälligt otillgänglig. Försök igen om en stund.";
    public const string UnavailableEn = "Search is temporarily unavailable. Please try again shortly.";
    public const string CaveatSv = "*Sökningen fungerar just nu med begränsad precision; svaret bygger på en enklare textsökning.*";
    public const string CaveatEn = "*Search is running with reduced precision right now; this answer is based on a simpler text search.*";
    public const string GeneralCaveatSv = "*Jag hittar inget om detta i kommunens källor. Svaret bygger på allmän kunskap och kan vara fel för Umeå kommun.*";
    public const string GeneralCaveatEn = "*I found nothing about this in the municipality's sources. This answer is based on general knowledge and may not apply to Umeå kommun.*";

    /// <summary>
    /// The assistant's text in the language, else <c>Settings.Chat.notFound</c>'s (<paramref name="platformNotFound"/>), else the
    /// built-in; the assistant's links, else the platform's.
    /// </summary>
    public static string NotFound(string? assistantNotFound, string? platformNotFound, string language) =>
        Render(Text(assistantNotFound, language) ?? Text(platformNotFound, language) ?? (language == LanguageGuess.English ? DefaultNotFoundEn : DefaultNotFoundSv),
            HasLinks(assistantNotFound) ? assistantNotFound : platformNotFound);

    /// <summary>
    /// The contact links after a general-knowledge answer (§10.4): the assistant's links, else the platform's, as a Markdown
    /// list after a blank line; empty when neither has any.
    /// </summary>
    public static string ContactLinks(string? assistantNotFound, string? platformNotFound) =>
        Render("", HasLinks(assistantNotFound) ? assistantNotFound : platformNotFound);

    private static bool HasLinks(string? notFound) => Parse(notFound)?["links"] is JsonArray { Count: > 0 };

    public static string Unavailable(string? assistantNotFound, string language) =>
        Render(language == LanguageGuess.English ? UnavailableEn : UnavailableSv, assistantNotFound);

    private static string Render(string text, string? notFound)
    {
        var links = (Parse(notFound)?["links"] as JsonArray)?.Select(l => (Title: l?["title"]?.GetValue<string>(), Url: l?["url"]?.GetValue<string>()))
            .Where(l => !string.IsNullOrWhiteSpace(l.Url)).ToList() ?? [];
        return links.Count == 0 ? text : text + "\n\n" + string.Join('\n', links.Select(l => $"- [{l.Title ?? l.Url}]({l.Url})"));
    }

    private static string? Text(string? json, string language) =>
        Parse(json)?[language] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } text ? text : null;

    private static JsonObject? Parse(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
}
