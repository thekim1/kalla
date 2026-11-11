using System.Text.Json.Nodes;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// B-32 (chat design §5, §5.1, §7.2–§7.3, §9.8, §10.2, §10.4; Decisions "Chat prompt texts configurable"): the platform's
/// prompt texts from <c>Settings.Chat.prompts</c>, each at its default when absent or empty, with the locked lines kept in
/// every prompt whatever the texts say. The defaults' wording is pinned by the golden prompts (<c>Golden/prompts</c>),
/// the condense tests and <c>FollowUpsTests</c>.
/// </summary>
public sealed class PromptTextsTests
{
    /// <summary>Every member set to a recognisable text.</summary>
    private static PromptTexts Edited() => PromptTexts.Parse(JsonNode.Parse("""
        {
          "identity": "Hej, jag är {assistant}.",
          "sourcesLabel": "Kommunens texter.",
          "rules": { "off": "REGEL-OFF", "supplement": "REGEL-SUPPLEMENT", "open": "REGEL-OPEN", "fallbackGeneral": "REGEL-FALLBACK-ALLMÄN", "openGeneral": "REGEL-OPEN-ALLMÄN" },
          "conversational": "SAMTAL",
          "degraded": "FÖRENKLAD",
          "condense": "SKRIV OM",
          "condenseLong": "SKRIV OM LÅNGT",
          "followUps": "Ge {N} frågor.",
          "caveats": { "degraded": { "sv": "*SV-FÖRENKLAD*", "en": "*EN-DEGRADED*" }, "general": { "sv": "*SV-ALLMÄN*", "en": "*EN-GENERAL*" } }
        }
        """));

    [Theory]
    [InlineData(PromptKind.Sources, GeneralKnowledge.Off, "REGEL-OFF")]
    [InlineData(PromptKind.Sources, GeneralKnowledge.Supplement, "REGEL-SUPPLEMENT")]
    [InlineData(PromptKind.Sources, GeneralKnowledge.Fallback, "REGEL-SUPPLEMENT")]
    [InlineData(PromptKind.Sources, GeneralKnowledge.Open, "REGEL-OPEN")]
    [InlineData(PromptKind.General, GeneralKnowledge.Fallback, "REGEL-FALLBACK-ALLMÄN")]
    [InlineData(PromptKind.General, GeneralKnowledge.Open, "REGEL-OPEN-ALLMÄN")]
    [InlineData(PromptKind.Conversational, GeneralKnowledge.Off, "SAMTAL")]
    public void With_every_text_edited_each_frame_uses_them_and_keeps_its_locked_lines(PromptKind kind, GeneralKnowledge level, string rule)
    {
        var frame = PromptBuilder.Frame("Bygglovsassistenten", kind, level, alwaysSwedish: true, degraded: true, Edited()).Split('\n');
        frame[0].ShouldBe("Hej, jag är Bygglovsassistenten.");
        frame[1].ShouldBe(rule);
        frame.ShouldContain("FÖRENKLAD");
        frame[^1].ShouldBe(PromptTexts.Locked.LanguageSwedish);
        if (kind == PromptKind.Sources)
        {
            frame.ShouldContain(PromptTexts.Locked.Citation);
            frame.ShouldContain(PromptTexts.Locked.Injection);
        }

        if (kind != PromptKind.Conversational)
        {
            frame.ShouldContain(PromptTexts.Locked.NoInventedNumbers);
        }
    }

    [Fact]
    public void The_sources_block_keeps_its_data_note_after_an_edited_label()
    {
        var context = new PromptContext("Bygg", "", "sv", "Vad kostar bygglov?", [], AssistantChatSettings.Default, Texts: Edited());
        var document = Guid.NewGuid();
        var chunk = new RetrievedChunk(1, Guid.NewGuid(), document, [], "Text om bygglov.", "Bygglov", null, null, 1, new ChunkScores(null, null, null, null, null, null),
            null, Guid.Empty, "c", "public", new Citation(document, "Bygglov", null, "https://www.umea.se/bygglov", null, "web", "page", "text/html", "sv", null));
        var prompt = PromptBuilder.Build(PromptKind.Sources, context, [chunk]);
        prompt.Messages[^1].Text.ShouldStartWith($"<sources note=\"Kommunens texter. {PromptTexts.Locked.SourcesNote}\">");
    }

    [Fact]
    public void The_task_prompts_use_the_edited_texts_and_the_follow_up_prompt_keeps_its_locked_lines_after_them()
    {
        var texts = Edited();
        Condenser.Messages([new ChatMessageIn("user", "Bygglov?")], "Och det?", texts)[0].Text.ShouldBe("SKRIV OM");
        Condenser.LongMessages([], new string('x', 3000), texts)[0].Text.ShouldBe("SKRIV OM LÅNGT");
        FollowUps.Messages(2, LanguageGuess.English, "q", "a", [], texts)[0].Text.ShouldBe(string.Join('\n',
            ["Ge 2 frågor.", .. PromptTexts.Locked.FollowUps, PromptTexts.Locked.FollowUpsEnglish]));
    }

    [Fact]
    public void The_caveats_come_from_the_texts()
    {
        var platform = PlatformChatSettings.Parse("""{"prompts":{"caveats":{"general":{"sv":"*SV-ALLMÄN*"}}}}""", new ChatOptions());
        platform.Texts[PromptTexts.CaveatGeneralSv].ShouldBe("*SV-ALLMÄN*");
        platform.Texts[PromptTexts.CaveatGeneralEn].ShouldBe(FixedTexts.GeneralCaveatEn);
        platform.Texts[PromptTexts.CaveatDegradedSv].ShouldBe(FixedTexts.CaveatSv);
    }

    [Fact]
    public void An_absent_empty_or_non_text_member_is_its_default()
    {
        var texts = PromptTexts.Parse(JsonNode.Parse("""{"identity":"","rules":{"off":42},"conversational":"   "}"""));
        var defaults = PromptTexts.Members.ToDictionary(m => m.Path, m => m.Default);
        texts[PromptTexts.Identity].ShouldBe(defaults[PromptTexts.Identity]);
        texts[PromptTexts.RulesOff].ShouldBe(defaults[PromptTexts.RulesOff]);
        texts[PromptTexts.Conversational].ShouldBe(defaults[PromptTexts.Conversational]);
        PromptBuilder.Frame("Bygg", PromptKind.Sources, GeneralKnowledge.Off, false, false, texts)
            .ShouldBe(PromptBuilder.Frame("Bygg", PromptKind.Sources, GeneralKnowledge.Off, false, false));
    }
}
