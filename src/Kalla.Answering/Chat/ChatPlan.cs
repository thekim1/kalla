using Kalla.Domain;
using Kalla.Domain.Access;
using Kalla.Domain.Retrieval;

namespace Kalla.Answering.Chat;

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

/// <summary>
/// A turn's nearest hit (chat design §12.1; Decisions "Content-gap report"): the knowledge base and document of the search's first
/// result. Ids only: no title, address or text.
/// </summary>
public sealed record NearestHit(Guid CollectionId, Guid DocumentId);

/// <summary>A completed turn as the JSON endpoint writes it.</summary>
public sealed record ChatCompletion(Guid MessageId, string Model, string Content, IReadOnlyList<UrlCitation> Annotations, IReadOnlyList<PromptSource> Cited,
    int PromptTokens, int CompletionTokens, string FinishReason, ChatOutcome Outcome, DateTimeOffset Created)
{
    /// <summary>The plan the turn ran (for the conversation writer, chat design §12).</summary>
    public ChatPlan? Plan { get; init; }
}

/// <summary>
/// A turn decided up to the gateway call (chat design §4.1 steps 5–12): either a fixed text (<see cref="Fixed"/>, no model
/// call) or the prompt with everything the call and the citations need. <see cref="Settings"/> carries the turn's effective
/// general-knowledge level (§3.8: the stricter of the assistant's and the request's).
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

    /// <summary>
    /// With <c>kalla.topics</c> (§3.8): the asked topics that at least one link searched, in the request's order (the retrieval's
    /// when it ran, else the published links' narrowing); null without.
    /// </summary>
    public IReadOnlyList<string>? SearchedTopics { get; init; }

    /// <summary>
    /// The usage records of the turn's model calls (§8.3, §12.1): condense and retrieval have added theirs; the answer and the
    /// follow-ups add theirs when they answer.
    /// </summary>
    public required TurnUsage Usage { get; init; }

    /// <summary>
    /// The nearest hit of a turn that ended <c>not-found</c> or <c>answered-general</c> (§12.1; Decisions "Content-gap report"): the
    /// best-ranked retrieval result's knowledge base and document; null for any other outcome, and when the search found nothing or
    /// did not run (a request narrowed to no link, §3.8).
    /// </summary>
    public NearestHit? Nearest => Outcome is ChatOutcome.NotFound or ChatOutcome.AnsweredGeneral && Retrieval?.Results.MinBy(r => r.Rank) is { } first
        ? new NearestHit(first.CollectionId, first.DocumentId)
        : null;

    /// <summary>The turn's step times (§9.10): condense and retrieval have added theirs; the answer adds its first event and the end of its text.</summary>
    public required TurnTimings Timings { get; init; }

    /// <summary>Where <see cref="MaxTokens"/> came from (<see cref="MaxTokensSources"/>); null is left out of the request.</summary>
    public string MaxTokensSource { get; init; } = MaxTokensSources.Provider;

    /// <summary>The caveat line of a degraded answer (§10.2) or of a general-knowledge answer (§10.4), or null; written by us, first.</summary>
    public string? Caveat => Outcome == ChatOutcome.AnsweredGeneral
        ? Platform.Texts[Language == LanguageGuess.English ? PromptTexts.CaveatGeneralEn : PromptTexts.CaveatGeneralSv] + "\n\n"
        : Degraded ? Platform.Texts[Language == LanguageGuess.English ? PromptTexts.CaveatDegradedEn : PromptTexts.CaveatDegradedSv] + "\n\n" : null;

    /// <summary>What follows the model's text: the contact links of a general-knowledge answer (§10.4), else empty.</summary>
    public string Trailer => Outcome == ChatOutcome.AnsweredGeneral ? FixedTexts.ContactLinks(Assistant.Published?.NotFound, Platform.NotFound) : "";
}
