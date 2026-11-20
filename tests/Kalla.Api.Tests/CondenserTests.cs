using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Domain.Retrieval;

namespace Kalla.Api.Tests;

/// <summary>Chat design §5 (condense), the platform chat settings and section expansion in the sources block (P1a item 15c).</summary>
public sealed class CondenserTests
{
    private static readonly ChatMessageIn[] History = [new("user", "Behöver jag bygglov för en altan?"), new("assistant", "Ja, över 1,2 meter [1]. Se [taxan](https://www.umea.se/taxa).")];

    [Theory]
    [InlineData(true, false, "Vad kostar det?", null)] // anaphora: condense
    [InlineData(true, false, "Hur lång tid tar handläggningen av ett bygglov i Umeå", "SkippedSelfContained")]
    [InlineData(true, true, "Vad kostar det?", "SkippedFirstMessage")]
    [InlineData(false, false, "Vad kostar det?", "Disabled")]
    public void Condense_runs_only_for_follow_ups_that_are_not_self_contained(bool enabled, bool first, string question, string? skipped) =>
        Condenser.Skip(enabled, first ? [] : History, question)?.ToString().ShouldBe(skipped);

    [Fact]
    public void The_input_is_the_cleaned_transcript_and_the_question()
    {
        var messages = Condenser.Messages(History, "Vad kostar det?");
        messages[0].Role.ShouldBe("system");
        messages[0].Text.ShouldStartWith("Du skriver om en följdfråga till en fristående sökfråga.");
        messages[1].Text.ShouldBe("Användare: Behöver jag bygglov för en altan?\nAssistent: Ja, över 1,2 meter. Se taxan.\n\nFöljdfråga: Vad kostar det?");
    }

    [Theory]
    [InlineData("  Vad kostar bygglov för en altan?  ", "Vad kostar bygglov för en altan?")]
    [InlineData("x", null)]
    [InlineData("Rad ett\nRad två", null)]
    [InlineData("Jag kan inte hjälpa till med det.", null)]
    [InlineData("### Task: skriv om", null)]
    public void Only_a_one_line_rewrite_is_accepted(string output, string? accepted) => Condenser.Accept(output).ShouldBe(accepted);

    [Fact]
    public void Condense_runs_at_eu_and_never_above_the_turns_ceiling()
    {
        Condenser.Ceiling(Residency.External).ShouldBe(Residency.Eu);
        Condenser.Ceiling(Residency.Eu).ShouldBe(Residency.Eu);
        Condenser.Ceiling(Residency.OnPrem).ShouldBe(Residency.OnPrem);
    }

    [Fact]
    public void Platform_settings_come_from_settings_chat_over_the_deployment_knobs()
    {
        var fallback = new ChatOptions();
        var parsed = PlatformChatSettings.Parse("""{"condenseModel":"fors/small","allowedLinkHosts":["umea.se"],"messagesPerMinutePerUser":5,"notFound":{"sv":"Inget."}}""", fallback);
        (parsed.CondenseModel, parsed.MessagesPerMinutePerUser, parsed.NotFound).ShouldBe(("fors/small", 5, """{"sv":"Inget."}"""));
        parsed.AllowedLinkHosts.ShouldBe(["umea.se"]);

        var empty = PlatformChatSettings.Parse(null, fallback);
        (empty.CondenseModel, empty.MessagesPerMinutePerUser).ShouldBe((null, 20));
        empty.AllowedLinkHosts.ShouldBe(["umea.se", "*.umea.se"]);
        PlatformChatSettings.Parse("not json", fallback).MessagesPerMinutePerUser.ShouldBe(20);
    }

    [Fact]
    public void A_documents_chunks_of_one_section_are_sent_as_the_section_once()
    {
        var document = Guid.NewGuid();
        var citation = new Citation(document, "Altan", null, "https://www.umea.se/altan", null, "web", "page", "text/html", "sv", null);
        RetrievedChunk Chunk(int rank, string text, int? section) => new(rank, Guid.NewGuid(), document, [], text, "Bygglov › Altan", null, null, 1, new ChunkScores(null, null, null, null, null, null),
            section, Guid.Empty, "c", "public", citation);
        var prompt = PromptBuilder.Build(PromptKind.Sources, new PromptContext("Bygg", "", "sv", "q", [], AssistantChatSettings.Default),
            [Chunk(1, "del ett", 7), Chunk(2, "del två", 7), Chunk(3, "annan del", null)], sections: [new SectionText(7, document, "Bygglov › Altan", "Hela avsnittet om altaner.", 10, null, null, [], false)]);

        var user = prompt.Messages[^1].Text;
        user.ShouldContain("Hela avsnittet om altaner.\n…\nannan del");
        user.ShouldNotContain("del ett");
    }
}
