using System.Text.Json;
using Kalla.Answering.Chat;

namespace Kalla.Api.Tests;

/// <summary>
/// The escalation hand-off's pure parts (chat design §3.8, §9.9, §18.9; Decisions "Escalation hand-off"): the request member,
/// the call's messages, the accept rule's cleaning and masking, the contacts, and the member's JSON.
/// </summary>
public sealed class EscalationTests
{
    private static JsonElement Body(string ume) =>
        JsonDocument.Parse($$"""{"model":"bygg","messages":[{"role":"user","content":"VPN-klienten ger fel 809."}],"ume":{{ume}}}""").RootElement.Clone();

    [Theory]
    [InlineData("""{"escalation":true}""", true)]
    [InlineData("""{"escalation":false}""", false)]
    [InlineData("""{}""", false)]
    public void The_member_is_a_boolean(string ume, bool expected) => ChatRequestParser.Parse(Body(ume)).Kalla!.Escalation.ShouldBe(expected);

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Anything_but_true_or_false_is_refused(string value)
    {
        var refused = Should.Throw<ChatException>(() => ChatRequestParser.Parse(Body($$"""{"escalation":{{value}}}""")));
        (refused.Status, refused.Code, refused.Param).ShouldBe((400, "invalid_request", "ume.escalation"));
    }

    [Theory]
    [InlineData(ChatOutcome.NotFound, true)]
    [InlineData(ChatOutcome.AnsweredGeneral, true)]
    [InlineData(ChatOutcome.Unavailable, true)]
    [InlineData(ChatOutcome.Answered, false)]
    public void Only_turns_the_sources_could_not_answer_get_the_hand_off(ChatOutcome outcome, bool applies) =>
        Escalation.Applies(outcome).ShouldBe(applies);

    [Fact]
    public void The_call_reads_the_users_message_the_condensed_query_and_the_outcome_never_the_answer()
    {
        var messages = Escalation.Messages(ChatOutcome.NotFound, LanguageGuess.Swedish, "Jag får fel 809 i VPN-klienten hemifrån.", "VPN fel 809");
        var system = messages[0].Text;
        system.ShouldStartWith("Skriv en kort sammanfattning av användarens ärende");
        foreach (var locked in PromptTexts.Locked.Escalation)
        {
            system.ShouldContain(locked);
        }

        system.ShouldEndWith("\n" + PromptTexts.Locked.EscalationSwedish);
        messages[1].Text.ShouldBe("Ärende: Jag får fel 809 i VPN-klienten hemifrån.\n\nSökfråga: VPN fel 809\n\nUtfall: Inget svar hittades i källorna.");

        // Not condensed: no Sökfråga line; English turn: English language line; each outcome has its line.
        var english = Escalation.Messages(ChatOutcome.Unavailable, LanguageGuess.English, "VPN error 809", "VPN error 809");
        english[0].Text.ShouldEndWith("\n" + PromptTexts.Locked.EscalationEnglish);
        english[1].Text.ShouldBe("Ärende: VPN error 809\n\nUtfall: Sökningen var otillgänglig.");
        Escalation.Messages(ChatOutcome.AnsweredGeneral, LanguageGuess.Swedish, "x y", null)[1].Text.ShouldEndWith("Utfall: Besvarat med allmän kunskap, utan källor.");
    }

    [Fact]
    public void A_long_message_is_cut_at_the_last_space_before_4000_characters()
    {
        var question = string.Concat(Enumerable.Repeat("fel ", 1500)); // 6,000 characters
        var user = Escalation.Messages(ChatOutcome.NotFound, LanguageGuess.Swedish, question, null)[1].Text;
        var asked = user["Ärende: ".Length..user.IndexOf("\n\nUtfall: ", StringComparison.Ordinal)];
        asked.Length.ShouldBeLessThanOrEqualTo(Escalation.QuestionMaxChars);
        asked.ShouldEndWith("fel");
    }

    [Theory]
    // Links keep their text; bare URLs and markers go.
    [InlineData("Användaren ser [felsidan](https://evil.example/x) och https://evil.example/y efter uppdateringen [1].",
        "Användaren ser felsidan och efter uppdateringen .")]
    // Personal identity and coordination numbers, phone numbers and e-mail addresses are masked.
    [InlineData("Anna 19800101-1234 och 800101-1234 ringer från 090-16 10 00 eller +46 70 123 45 67, anna@example.se.",
        "Anna […] och […] ringer från […] eller […], […].")]
    // Error codes, KB numbers and versions stay: the second line needs them.
    [InlineData("Felkoden 0x80070005 kom efter KB5034441 i klientversion 14.2.1 på Windows 11.",
        "Felkoden 0x80070005 kom efter KB5034441 i klientversion 14.2.1 på Windows 11.")]
    public void The_accept_rule_removes_links_and_masks_personal_data(string output, string expected) =>
        Escalation.Accept(output).ShouldBe(expected);

    [Fact]
    public void A_long_summary_is_cut_at_a_sentence_end_and_a_short_or_refusing_one_is_rejected()
    {
        var sentence = "Användaren kommer inte åt VPN hemifrån och har startat om datorn två gånger. ";
        var cut = Escalation.Accept(string.Concat(Enumerable.Repeat(sentence, 12)))!; // ≈ 900 characters
        cut.Length.ShouldBeLessThanOrEqualTo(Escalation.SummaryMaxChars);
        cut.ShouldEndWith("gånger.");

        Escalation.Accept("Kort text.").ShouldBeNull(); // under 20 characters
        Escalation.Accept("").ShouldBeNull();
        Escalation.Accept(null).ShouldBeNull();
        Escalation.Accept("Jag kan tyvärr inte hjälpa till med det.").ShouldBeNull(); // a refusal (§5)
    }

    [Fact]
    public void The_contacts_are_the_assistants_links_else_the_platforms()
    {
        const string Assistant = """{"sv":"Inget svar.","links":[{"title":"IT-support","url":"https://www.umea.se/it"}]}""";
        const string Platform = """{"links":[{"url":"https://www.umea.se/kontakt"}]}""";
        FixedTexts.Contacts(Assistant, Platform).ShouldBe([new EscalationContact("IT-support", "https://www.umea.se/it")]);
        FixedTexts.Contacts("""{"sv":"Inget svar."}""", Platform).ShouldBe([new EscalationContact("https://www.umea.se/kontakt", "https://www.umea.se/kontakt")]);
        FixedTexts.Contacts(null, null).ShouldBeEmpty();
    }

    [Fact]
    public void The_member_is_written_as_null_or_with_a_null_summary_whatever_the_serializer_leaves_out()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        JsonSerializer.Serialize(new KallaLater(null, new EscalationMember(null)), options).ShouldBe("""{"escalation":null}""");
        var route = new EscalationMember(new EscalationAnswer("not-found", [new EscalationContact("IT", "https://www.umea.se/it")], null));
        JsonSerializer.Serialize(new KallaLater(null, route), options)
            .ShouldBe("""{"escalation":{"reason":"not-found","contacts":[{"title":"IT","url":"https://www.umea.se/it"}],"summary":null}}""");
        JsonSerializer.Serialize(new KallaLater(["Hur?"], null), options).ShouldBe("""{"follow_ups":["Hur?"]}""");
    }
}
