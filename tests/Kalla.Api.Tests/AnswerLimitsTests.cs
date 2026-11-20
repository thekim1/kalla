using Kalla.Answering.Chat;
using Kalla.Domain.Access;

namespace Kalla.Api.Tests;

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
    public void The_platform_ceiling_lowers_an_override_caps_the_model_limit_and_fills_an_unknown_one()
    {
        // X = Settings.Chat.MaxAnswerTokens (chat §6.3).
        var lowered = AnswerLimits.Resolve(Steps("""{"answer":{"maxOutputTokens":8000}}"""), new ModelLimitsEntry(128_000, 16_384), 2_000);
        (lowered.Output, lowered.Reserve).ShouldBe(((int?)2_000, 2_000)); // the override is capped, and so is its reserve
        lowered.MaxTokens(3_000).ShouldBe(((int?)2_000, MaxTokensSources.Ceiling));

        var capped = AnswerLimits.Resolve(AssistantChatSettings.Default, new ModelLimitsEntry(128_000, 16_384), 4_000);
        capped.Reserve.ShouldBe(1_024); // F stays 1,024 without an override
        capped.MaxTokens(3_000).ShouldBe(((int?)4_000, MaxTokensSources.Ceiling));

        var filled = AnswerLimits.Resolve(AssistantChatSettings.Default, null, 4_000);
        filled.MaxTokens(3_000).ShouldBe(((int?)4_000, MaxTokensSources.Ceiling)); // max_tokens is always sent with a ceiling

        // A ceiling above the limits changes nothing.
        AnswerLimits.Resolve(AssistantChatSettings.Default, new ModelLimitsEntry(128_000, 16_384), 100_000).MaxTokens(3_000)
            .ShouldBe(((int?)16_384, MaxTokensSources.Model));
        AnswerLimits.Resolve(Steps("""{"answer":{"maxOutputTokens":700}}"""), null, 100_000).MaxTokens(100).ShouldBe(((int?)700, MaxTokensSources.Override));
    }

    [Fact]
    public void The_platform_ceiling_is_read_from_settings_chat()
    {
        PlatformChatSettings.Parse("""{"maxAnswerTokens":4000}""", new ChatOptions()).MaxAnswerTokens.ShouldBe(4_000);
        PlatformChatSettings.Parse("{}", new ChatOptions()).MaxAnswerTokens.ShouldBeNull();
        PlatformChatSettings.Parse("""{"maxAnswerTokens":"4000"}""", new ChatOptions()).MaxAnswerTokens.ShouldBeNull();
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
