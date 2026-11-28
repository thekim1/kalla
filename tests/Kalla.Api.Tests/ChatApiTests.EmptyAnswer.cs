using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kalla.Answering.Chat;

namespace Kalla.Api.Tests;

/// <summary>
/// #1106 (chat design §6.3; Decisions "Answer length from the model"): an answer call that ends with
/// <c>finish_reason: "length"</c> and no visible text is made once more with the full output limit and the platform's lowest
/// reasoning effort (<c>condenseReasoningEffort: none</c> in <see cref="CostHost"/>); text then, the turn is answered; still
/// empty, it is <c>unavailable</c> with the unavailable text; an answer that wrote text and then stopped at the limit stays
/// answered, without a retry. The FakeGateway's <c>fake-empty-answer</c> writes nothing unless <c>reasoning_effort</c> is sent,
/// <c>fake-empty-always</c> never.
/// </summary>
public sealed partial class ChatApiTests
{
    private static Task<HttpResponseMessage> AskWithKalla(HttpClient client, string question, bool stream) =>
        client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "bygg", stream, messages = new[] { new { role = "user", content = question } }, kalla = new { } }, Ct);

    /// <summary>The answer's id, text, finish reason and <c>kalla.outcome</c>, from a stream or a JSON answer.</summary>
    private static async Task<(string Id, string Text, string? Finish, string Outcome)> EmptyAnswerReadAsync(HttpResponseMessage response, bool stream)
    {
        var wire = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, wire);
        if (!stream)
        {
            using var json = JsonDocument.Parse(wire);
            var root = json.RootElement;
            var choice = root.GetProperty("choices")[0];
            return (root.GetProperty("id").GetString()!, choice.GetProperty("message").GetProperty("content").GetString()!,
                choice.GetProperty("finish_reason").GetString(), root.GetProperty("kalla").GetProperty("outcome").GetString()!);
        }

        var events = Events(wire);
        events.Any(e => e.TryGetProperty("error", out _)).ShouldBeFalse(wire);
        var text = string.Concat(events.Where(e => e.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0)
            .Select(e => e.GetProperty("choices")[0].GetProperty("delta"))
            .Select(d => d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : ""));
        var finish = events.Select(FinishReason).LastOrDefault(f => f is not null);
        var outcome = events.Where(e => e.TryGetProperty("kalla", out var k) && k.TryGetProperty("outcome", out _))
            .ShouldHaveSingleItem().GetProperty("kalla").GetProperty("outcome").GetString()!;
        return (events[0].GetProperty("id").GetString()!, text, finish, outcome);
    }

    /// <summary>The outcomes whose turns grew between two <see cref="OutcomesAsync"/> reads, once per turn added.</summary>
    private static List<string> TurnsAdded(Dictionary<string, (int Turns, decimal CostUsd, decimal EstimatedCostUsd)> before,
        Dictionary<string, (int Turns, decimal CostUsd, decimal EstimatedCostUsd)> after) =>
        [.. after.SelectMany(a => Enumerable.Repeat(a.Key, a.Value.Turns - (before.TryGetValue(a.Key, out var b) ? b.Turns : 0)))];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_with_no_text_at_the_limit_is_retried_once_with_the_lowest_effort_and_then_answered(bool stream)
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, model: "fake-empty-answer", rerank: false);
        using var client = Client(api, db.IntegrationKey);
        var outcomesBefore = await OutcomesAsync(api);

        using var response = await AskWithKalla(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", stream);
        var (_, text, finish, outcome) = await EmptyAnswerReadAsync(response, stream);

        (outcome, finish).ShouldBe(("answered", "stop"));
        text.Trim().ShouldNotBeEmpty();
        var answers = calls.Where(c => c.Step == GatewayChatClient.AnswerStep).ToList();
        answers.Count.ShouldBe(2);
        answers[0].Sent.ReasoningEffort.ShouldBeNull();
        answers[1].Sent.ReasoningEffort.ShouldBe("none"); // Settings.Chat.CondenseReasoningEffort, the platform's lowest
        answers.ShouldAllBe(c => c.Streamed == stream);
        TurnsAdded(outcomesBefore, await OutcomesAsync(api)).ShouldBe(["answered"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_empty_twice_is_unavailable_with_the_unavailable_text(bool stream)
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, model: "fake-empty-always", rerank: false);
        using var client = Client(api, db.IntegrationKey);
        var before = await UsageAsync(api);
        var outcomesBefore = await OutcomesAsync(api);

        using var response = await AskWithKalla(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", stream);
        var (_, text, finish, outcome) = await EmptyAnswerReadAsync(response, stream);

        (outcome, finish).ShouldBe(("unavailable", "stop"));
        text.ShouldStartWith(FixedTexts.Unavailable(null, null, LanguageGuess.Swedish).Split('\n')[0]);
        calls.Count(c => c.Step == GatewayChatClient.AnswerStep).ShouldBe(2);
        // §8.3: both calls are paid for and recorded.
        Added(before, await UsageAsync(api))[GatewayChatClient.AnswerStep].Calls.ShouldBe(2);
        TurnsAdded(outcomesBefore, await OutcomesAsync(api)).ShouldBe(["unavailable"]); // the day's outcome, not answered
    }

    [Fact]
    public async Task An_answer_that_wrote_text_and_then_stopped_at_the_limit_stays_answered_without_a_retry()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, model: "fake-stream-length", rerank: false);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskWithKalla(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", stream: true);
        var (_, text, finish, outcome) = await EmptyAnswerReadAsync(response, stream: true);

        (outcome, finish).ShouldBe(("answered", "length"));
        text.Trim().ShouldNotBeEmpty();
        calls.Count(c => c.Step == GatewayChatClient.AnswerStep).ShouldBe(1);
    }
}
