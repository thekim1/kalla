using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// The outcome of a turn (chat design §6.2): an answer, a general-knowledge answer without sources (§10.4), the fixed
/// not-found text, or the unavailable text.
/// </summary>
public enum ChatOutcome
{
    Answered,
    AnsweredGeneral,
    NotFound,
    Unavailable,
}

/// <summary>The stored and reported names of <see cref="ChatOutcome"/> (<c>Messages.Outcome</c>, <c>rag.chat.requests{outcome}</c>).</summary>
public static class ChatOutcomes
{
    public static string Name(ChatOutcome outcome) => outcome switch
    {
        ChatOutcome.AnsweredGeneral => "answered-general",
        ChatOutcome.NotFound => "not-found",
        ChatOutcome.Unavailable => "unavailable",
        _ => "answered",
    };
}

/// <summary>A completed turn as the JSON endpoint writes it.</summary>
public sealed record ChatCompletion(Guid MessageId, string Model, string Content, IReadOnlyList<UrlCitation> Annotations, IReadOnlyList<PromptSource> Cited,
    int PromptTokens, int CompletionTokens, string FinishReason, ChatOutcome Outcome, DateTimeOffset Created)
{
    /// <summary>The plan the turn ran (for the conversation writer, chat design §12).</summary>
    public ChatPlan? Plan { get; init; }
}

/// <summary>
/// A turn decided up to the gateway call (chat design §4.1 steps 5–12): either a fixed text (<see cref="Fixed"/>, no model
/// call) or the prompt with everything the call and the citations need.
/// </summary>
public sealed record ChatPlan(Guid MessageId, string Model, DateTimeOffset Created, AssistantEntry Assistant, Residency Ceiling, AssistantChatSettings Settings,
    int? MaxTokens, string Language, string? Fixed, ChatOutcome Outcome, BuiltPrompt? Prompt, RetrievalResult? Retrieval, bool Degraded,
    PlatformChatSettings Platform, CondenseOutcome? Condense = null)
{
    /// <summary>
    /// The turn's search query (§5, §5.1): the question's query form or its condensed form; null for a conversational turn
    /// (no retrieval). The follow-up call reads it (§9.8); it is never stored or logged.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>Where <see cref="MaxTokens"/> came from (<see cref="MaxTokensSources"/>); null is left out of the request.</summary>
    public string MaxTokensSource { get; init; } = MaxTokensSources.Provider;

    /// <summary>The caveat line of a degraded answer (§10.2) or of a general-knowledge answer (§10.4), or null; written by us, first.</summary>
    public string? Caveat => Outcome == ChatOutcome.AnsweredGeneral
        ? (Language == LanguageGuess.English ? FixedTexts.GeneralCaveatEn : FixedTexts.GeneralCaveatSv) + "\n\n"
        : Degraded ? (Language == LanguageGuess.English ? FixedTexts.CaveatEn : FixedTexts.CaveatSv) + "\n\n" : null;

    /// <summary>What follows the model's text: the contact links of a general-knowledge answer (§10.4), else empty.</summary>
    public string Trailer => Outcome == ChatOutcome.AnsweredGeneral ? FixedTexts.ContactLinks(Assistant.Published?.NotFound, Platform.NotFound) : "";
}
