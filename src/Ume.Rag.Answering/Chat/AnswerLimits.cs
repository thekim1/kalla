using Ume.Rag.Domain.Access;

namespace Ume.Rag.Answering.Chat;

/// <summary>Where the answer's <c>max_tokens</c> came from (admin design §4.9 test panel, <c>maxTokensSource</c>).</summary>
public static class MaxTokensSources
{
    /// <summary>The assistant's <c>answer.maxOutputTokens</c> (lowered to the model's limit when that is lower).</summary>
    public const string Override = "override";

    /// <summary>The model's <c>MaxOutputTokens</c> less the estimated prompt within the context window.</summary>
    public const string Model = "model";

    /// <summary>Left out: the limit is unknown, so the gateway's or the provider's default applies.</summary>
    public const string Provider = "provider";
}

/// <summary>
/// A turn's token limits (chat design §6.3; Decisions "Answer length from the model"): <see cref="Context"/> (<c>C</c>) the
/// context the prompt budget fills, <see cref="Output"/> (<c>R</c>) the output limit when known, <see cref="Reserve"/>
/// (<c>F</c>) the answer's reserve in the budget, and <see cref="MaxTokens"/> the <c>max_tokens</c> to send for an
/// estimated prompt.
/// </summary>
public sealed record AnswerLimits(int Context, int? Output, int Reserve, bool OutputOverridden, int? ModelOutput)
{
    /// <summary><c>C</c> when neither the assistant nor the model sets it.</summary>
    public const int DefaultContext = 16_384;

    /// <summary><c>F</c> without an output override: the budgets as before, the answer gets at least this much.</summary>
    public const int DefaultReserve = 1_024;

    /// <summary>
    /// <c>C</c> = the override, else the model's window <c>W</c>, else 16,384, and at most <c>W</c> when known (an override above
    /// a lowered window is lowered, never a failed chat); <c>R</c> = the override at most the model's <c>O</c>, else <c>O</c>,
    /// else unknown; <c>F</c> = <c>R</c> when the assistant set it, else 1,024.
    /// </summary>
    public static AnswerLimits Resolve(AssistantChatSettings settings, ModelLimitsEntry? model)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var window = model?.ContextWindow;
        var modelOutput = model?.MaxOutputTokens;
        var context = settings.ContextTokens ?? window ?? DefaultContext;
        if (window is { } w)
        {
            context = Math.Min(context, w);
        }

        int? output = settings.MaxOutputTokens is { } over ? (modelOutput is { } o ? Math.Min(over, o) : over) : modelOutput;
        var overridden = settings.MaxOutputTokens is not null;
        return new AnswerLimits(context, output, overridden ? output!.Value : DefaultReserve, overridden, modelOutput);
    }

    /// <summary>
    /// <c>max_tokens</c> for a prompt of <paramref name="estimatedPrompt"/> tokens (<c>P</c>): the override <c>R</c>; else the
    /// model's <c>min(O, C − P)</c>; else null, left out of the request.
    /// </summary>
    public (int? MaxTokens, string Source) MaxTokens(int estimatedPrompt) =>
        OutputOverridden ? (Output, MaxTokensSources.Override)
        : ModelOutput is { } o ? (Math.Max(1, Math.Min(o, Context - estimatedPrompt)), MaxTokensSources.Model)
        : (null, MaxTokensSources.Provider);
}
