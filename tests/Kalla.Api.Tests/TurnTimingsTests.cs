using System.Diagnostics;
using System.Text.Json;
using Kalla.Answering.Chat;

namespace Kalla.Api.Tests;

/// <summary>
/// The pure parts of <c>kalla.progress</c> and <c>kalla.timings</c> (chat design §3.8, §9.10, §18.9): the two request members, the
/// step times as the answer and the <c>Server-Timing</c> header give them, and a related document without <c>cited</c> and
/// <c>number</c> as the progress chunk carries it. No assertion depends on how long anything takes.
/// </summary>
public sealed class TurnTimingsTests
{
    private static JsonElement Body(string kalla) =>
        JsonDocument.Parse($$"""{"model":"bygg","messages":[{"role":"user","content":"Behöver jag bygglov?"}],"kalla":{{kalla}}}""").RootElement.Clone();

    [Theory]
    [InlineData("""{}""", false, false)]
    [InlineData("""{"progress":true}""", true, false)]
    [InlineData("""{"timings":true}""", false, true)]
    [InlineData("""{"progress":false,"timings":false}""", false, false)]
    [InlineData("""{"progress":true,"timings":true,"related_documents":3}""", true, true)]
    public void The_members_are_booleans(string kalla, bool progress, bool timings)
    {
        var parsed = ChatRequestParser.Parse(Body(kalla)).Kalla!;
        (parsed.Progress, parsed.Timings).ShouldBe((progress, timings));
    }

    [Theory]
    [InlineData("progress", "\"true\"")]
    [InlineData("progress", "1")]
    [InlineData("progress", "null")]
    [InlineData("progress", "[]")]
    [InlineData("timings", "\"yes\"")]
    [InlineData("timings", "0")]
    [InlineData("timings", "null")]
    [InlineData("timings", "{}")]
    public void Anything_but_true_or_false_is_refused_with_its_param(string member, string value)
    {
        var refused = Should.Throw<ChatException>(() => ChatRequestParser.Parse(Body($$"""{"{{member}}":{{value}}}""")));
        (refused.Status, refused.Code, refused.Param).ShouldBe((400, "invalid_request", "kalla." + member));
    }

    [Fact]
    public void A_turn_without_condense_or_answer_call_has_only_the_steps_that_ran()
    {
        var timings = TurnTimings.Now();
        timings.Retrieved(Stopwatch.GetTimestamp());
        timings.FirstEvent(); // no answer call was opened: a fixed text has no first_token
        timings.TextEnded();

        var member = timings.Member();
        (member.Condense, member.FirstToken).ShouldBe((null, null));
        member.Retrieve.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
        member.Total.ShouldBeGreaterThanOrEqualTo(member.Retrieve.Value);
        JsonSerializer.Serialize(member).ShouldBe($$"""{"retrieve":{{member.Retrieve}},"total":{{member.Total}}}""");
    }

    [Fact]
    public void Every_step_gives_whole_non_negative_milliseconds_within_the_total()
    {
        var timings = TurnTimings.Now();
        var condense = Stopwatch.GetTimestamp();
        timings.Condensed(condense);
        timings.Retrieved(Stopwatch.GetTimestamp());
        timings.AnswerOpening();
        timings.FirstEvent();
        timings.FirstEvent(); // only the first event counts
        timings.TextEnded();

        var member = timings.Member();
        var parts = new[] { member.Condense, member.Retrieve, member.FirstToken };
        parts.ShouldAllBe(p => p != null && p >= 0);
        member.Total.ShouldBeGreaterThanOrEqualTo(parts.Sum(p => p!.Value));
        timings.Member().ShouldBe(member); // the times are fixed once the text has ended
        JsonSerializer.Serialize(member).ShouldBe(
            $$"""{"condense":{{member.Condense}},"retrieve":{{member.Retrieve}},"first_token":{{member.FirstToken}},"total":{{member.Total}}}""");
    }

    [Fact]
    public void Server_timing_lists_the_members_that_are_there_in_their_order()
    {
        new KallaTimings(840, 120, 610, 3900).ServerTiming().ShouldBe("condense;dur=840, retrieve;dur=120, first_token;dur=610, total;dur=3900");
        new KallaTimings(null, 120, null, 130).ServerTiming().ShouldBe("retrieve;dur=120, total;dur=130");
        new KallaTimings(null, null, 0, 0).ServerTiming().ShouldBe("first_token;dur=0, total;dur=0");
    }

    [Fact]
    public void A_related_document_without_cited_and_number_leaves_both_out_and_the_complete_one_keeps_them()
    {
        var document = new RelatedDocument(Guid.Empty, "Altan", "https://www.umea.se/bygga/altan", "bygg", 1, true, 1);
        JsonSerializer.Serialize(document with { Cited = null, Number = null }).ShouldBe(
            """{"document_id":"00000000-0000-0000-0000-000000000000","title":"Altan","url":"https://www.umea.se/bygga/altan","collection":"bygg","rank":1}""");
        JsonSerializer.Serialize(document with { Cited = false, Number = null }).ShouldContain("\"rank\":1,\"cited\":false}");
        JsonSerializer.Serialize(document).ShouldContain("\"rank\":1,\"cited\":true,\"number\":1}");
    }
}
