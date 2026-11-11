using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;

namespace Ume.Rag.Api.Tests;

/// <summary>The token limits of chat design §6.3 (Decisions "Answer length from the model"), resolved without a gateway.</summary>
public sealed class AnswerLimitsTests
{
    private static AssistantChatSettings Steps(string json) => AssistantChatSettings.Parse(json);

    [Fact]
    public void Without_overrides_or_model_limits_the_context_is_16384_the_reserve_1024_and_max_tokens_is_left_out()
    {
        var limits = AnswerLimits.Resolve(AssistantChatSettings.Default, null);
        (limits.Context, limits.Output, limits.Reserve).ShouldBe((16_384, (int?)null, 1_024));
        limits.MaxTokens(3_000).ShouldBe(((int?)null, MaxTokensSources.Provider));
    }

    [Fact]
    public void A_known_model_limit_is_sent_less_the_prompt_within_the_window()
    {
        var limits = AnswerLimits.Resolve(AssistantChatSettings.Default, new ModelLimitsEntry(128_000, 16_384));
        (limits.Context, limits.Output, limits.Reserve).ShouldBe((128_000, (int?)16_384, 1_024)); // the reserve stays 1,024
        limits.MaxTokens(3_000).ShouldBe(((int?)16_384, MaxTokensSources.Model));

        // C − P below O: the room left in the window.
        var tight = AnswerLimits.Resolve(AssistantChatSettings.Default, new ModelLimitsEntry(10_000, 8_000));
        tight.MaxTokens(6_000).ShouldBe(((int?)4_000, MaxTokensSources.Model));
    }

    [Fact]
    public void An_output_limit_without_a_window_uses_the_16384_context()
    {
        var limits = AnswerLimits.Resolve(AssistantChatSettings.Default, new ModelLimitsEntry(null, 4_096));
        limits.Context.ShouldBe(16_384);
        limits.MaxTokens(2_000).ShouldBe(((int?)4_096, MaxTokensSources.Model));
    }

    [Fact]
    public void The_override_is_sent_as_set_and_is_the_reserve()
    {
        var limits = AnswerLimits.Resolve(Steps("""{"answer":{"maxOutputTokens":2000}}"""), new ModelLimitsEntry(128_000, 16_384));
        (limits.Output, limits.Reserve).ShouldBe(((int?)2_000, 2_000));
        limits.MaxTokens(3_000).ShouldBe(((int?)2_000, MaxTokensSources.Override));

        // Unknown model limit: the override still applies.
        AnswerLimits.Resolve(Steps("""{"answer":{"maxOutputTokens":700}}"""), null).MaxTokens(100).ShouldBe(((int?)700, MaxTokensSources.Override));
    }

    [Fact]
    public void Overrides_above_a_lowered_model_limit_are_lowered_to_it()
    {
        var limits = AnswerLimits.Resolve(Steps("""{"answer":{"maxOutputTokens":8000,"contextTokens":64000}}"""), new ModelLimitsEntry(32_768, 4_096));
        (limits.Context, limits.Output, limits.Reserve).ShouldBe((32_768, (int?)4_096, 4_096));
        limits.MaxTokens(1_000).ShouldBe(((int?)4_096, MaxTokensSources.Override));
    }

    [Fact]
    public void A_context_override_below_the_window_is_kept()
    {
        AnswerLimits.Resolve(Steps("""{"answer":{"contextTokens":24576}}"""), new ModelLimitsEntry(128_000, null)).Context.ShouldBe(24_576);
    }

    [Fact]
    public void The_prompt_estimate_is_the_mandatory_part_the_sources_and_the_history()
    {
        var context = new PromptContext("Bygg", "Var kortfattad.", "sv", "Vad kostar bygglov?", [new ChatMessageIn("user", "Hej"), new ChatMessageIn("assistant", "Hej!")],
            AssistantChatSettings.Default);
        var prompt = PromptBuilder.Build(PromptKind.Conversational, context);
        prompt.EstimatedTokens.ShouldBe(prompt.Messages.Sum(m => PromptBuilder.Tokens(m.Text)));
    }
}
