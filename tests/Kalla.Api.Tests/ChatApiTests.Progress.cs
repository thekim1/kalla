using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kalla.Answering.Chat;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// The progress chunk and the step times through the chat role (chat design §3.8, §9.10, §18.9 <i>Progress chunk</i> and
/// <i>Timings</i>; Decisions "Progress, timings and the nearest subject in the chat extension"): the chunk first, before the role
/// chunk, with the related documents not yet cited; none for a fixed text, a non-streamed answer or a refused answer call; the
/// timings present and consistent (never their values: no test here depends on how long a step takes), and the same values in
/// the JSON answer's <c>Server-Timing</c>. The wire with and without the members is pinned by the golden files.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>A stream's events up to <c>[DONE]</c>, parsed (it must end with <c>[DONE]</c>).</summary>
    private static async Task<List<JsonElement>> EventsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        lines[^1].ShouldBe("data: [DONE]");
        return [.. lines.SkipLast(1).Select(l => JsonDocument.Parse(l["data: ".Length..]).RootElement.Clone())];
    }

    private static bool IsKalla(JsonElement chunk) => chunk.TryGetProperty("kalla", out _);

    private static bool IsProgress(JsonElement chunk) => chunk.TryGetProperty("kalla", out var kalla) && kalla.TryGetProperty("progress", out _);

    /// <summary>
    /// The progress chunk on the wire (<c>sse-kalla-progress</c>): the chunk, then the stream exactly as <c>sse-kalla-cited</c>'s.
    /// </summary>
    [Fact]
    public Task The_progress_wire_matches_its_golden_file() =>
        WireMatchesAsync("sse-kalla-progress", true, true, false, AltanQuestion, null, Upstream.Fake,
            JsonDocument.Parse("""{"related_documents":5,"progress":true}""").RootElement.Clone(), update: true);

    /// <summary>
    /// The negative proof (§9.10): the members turned off, or <c>progress</c> on a non-streamed request, give the recorded answer
    /// of the request without them byte for byte (the files are never rewritten from here). The requests without <c>kalla</c> at
    /// all are the unchanged <c>sse-cited</c>, <c>json-cited</c> and the other golden files of today's wire.
    /// </summary>
    [Theory]
    [InlineData("sse-kalla-cited", true, """{"related_documents":5,"progress":false,"timings":false}""")]
    [InlineData("json-kalla-cited", false, """{"related_documents":5,"progress":true,"timings":false}""")]
    public Task Without_the_members_the_wire_is_byte_for_byte_todays(string name, bool stream, string kalla) =>
        WireMatchesAsync(name, stream, stream, false, AltanQuestion, null, Upstream.Fake, JsonDocument.Parse(kalla).RootElement.Clone(), update: false);

    [Theory]
    [InlineData("""{"progress":"yes"}""", "kalla.progress")]
    [InlineData("""{"progress":1}""", "kalla.progress")]
    [InlineData("""{"progress":null}""", "kalla.progress")]
    [InlineData("""{"timings":"true"}""", "kalla.timings")]
    [InlineData("""{"timings":0}""", "kalla.timings")]
    [InlineData("""{"timings":{}}""", "kalla.timings")]
    public async Task A_progress_or_timings_value_that_is_not_a_boolean_is_refused_before_any_model_call(string kalla, string param)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, AltanQuestion, kalla, stream: true);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("data:");
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("param").GetString()).ShouldBe(("invalid_request", param));
        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_progress_chunk_is_the_first_chunk_before_the_role_chunk_and_the_list_after_the_text_is_complete()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        var events = await EventsAsync(await AskUme(client, AltanQuestion, """{"related_documents":3,"progress":true}""", stream: true, usage: true));

        var progress = events[0];
        (progress.GetProperty("object").GetString(), progress.GetProperty("choices").GetArrayLength(), progress.GetProperty("id").GetString(),
                progress.GetProperty("model").GetString())
            .ShouldBe(("chat.completion.chunk", 0, events[1].GetProperty("id").GetString(), "bygg"));
        var kalla = progress.GetProperty("kalla");
        Members(kalla).ShouldBe(["progress", "related_documents"]);
        kalla.GetProperty("progress").GetString().ShouldBe("writing");
        events[1].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString().ShouldBe("assistant");
        events.Count(IsProgress).ShouldBe(1);

        // The entries are the list's, without cited and number; the kalla chunk after the text has them complete (§9.10).
        var early = kalla.GetProperty("related_documents").EnumerateArray().ToList();
        early.Count.ShouldBeInRange(1, 3); // the fixture's question finds two documents
        early.All(d => !d.TryGetProperty("cited", out _) && !d.TryGetProperty("number", out _)).ShouldBeTrue();
        var after = events.Skip(1).Single(IsKalla).GetProperty("kalla").GetProperty("related_documents").EnumerateArray().ToList();
        after.Select(d => (d.GetProperty("document_id").GetGuid(), d.GetProperty("rank").GetInt32()))
            .ShouldBe(early.Select(d => (d.GetProperty("document_id").GetGuid(), d.GetProperty("rank").GetInt32())));
        after.All(d => d.TryGetProperty("cited", out _)).ShouldBeTrue();
        after.Any(d => d.GetProperty("cited").GetBoolean() && d.TryGetProperty("number", out _)).ShouldBeTrue();
        Members(events.Skip(1).Single(IsKalla).GetProperty("kalla")).ShouldNotContain("progress");
    }

    [Fact]
    public async Task Without_related_documents_the_progress_chunk_has_progress_only_and_a_conversational_answer_has_one()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        var answered = await EventsAsync(await AskUme(client, AltanQuestion, """{"progress":true}""", stream: true));
        Members(answered[0].GetProperty("kalla")).ShouldBe(["progress"]);

        // A conversational message gets a model answer: it has the chunk, without sources (§9.10).
        var conversational = await EventsAsync(await AskUme(client, "Hej!", """{"progress":true,"related_documents":3}""", stream: true));
        IsProgress(conversational[0]).ShouldBeTrue();
        conversational[0].GetProperty("kalla").GetProperty("related_documents").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("off", "bge-m3", "Hur långt är det till månen i kilometer?", "not-found")]
    [InlineData("off", "fake-wrong-residency-embed", "Kvantgravitation xyzzy plutonium?", "unavailable")]
    public async Task A_fixed_text_has_no_progress_chunk(string level, string embeddingModel, string question, string outcome)
    {
        await using var api = GeneralHost(level, embeddingModel: embeddingModel);
        using var client = Client(api, db.IntegrationKey);

        var events = await EventsAsync(await AskUme(client, question, """{"progress":true,"related_documents":3}""", stream: true));
        events.ShouldNotContain(e => IsProgress(e));
        events[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString().ShouldBe("assistant");
        events.Single(IsKalla).GetProperty("kalla").GetProperty("outcome").GetString().ShouldBe(outcome);
    }

    [Fact]
    public async Task A_non_streamed_answer_ignores_progress()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"progress":true}"""));
        Members(kalla).ShouldBe(["outcome", "degraded"]);
    }

    [Fact]
    public async Task A_spent_budget_with_progress_asked_is_402_without_a_byte_of_a_stream()
    {
        // §9.10 "Why there": the chunk follows the gateway's acceptance, so the refusal of the answer call stays an HTTP error.
        await using var gateway = ActingFake(budgetExceeded: "Key");
        await using var api = await ActingHostAsync(gateway, TurnKey);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, AltanQuestion, """{"progress":true,"related_documents":3,"timings":true}""", stream: true);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("data:");
        body.ShouldNotContain("progress");
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("budget_exceeded");
    }

    [Fact]
    public async Task An_answer_call_refused_for_its_budget_with_progress_asked_is_402_without_a_byte_of_a_stream()
    {
        // Only the answer call is refused (condense and the search answered): the progress chunk would be due right after it.
        await using var api = Host().WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            services.AddHttpClient(GatewayChatClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RefusedAnswer { InnerHandler = db.Gateway.Server.CreateHandler() })));
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, AltanQuestion, """{"progress":true,"related_documents":3}""", stream: true);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("data:");
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("budget_exceeded");
    }

    /// <summary>The gateway's <c>402 budget_exceeded</c> for the answer call (<c>x-fors-step: chat-answer</c>); every other call passes.</summary>
    private sealed class RefusedAnswer : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Headers.TryGetValues(GatewayHeaders.Step, out var step) && step.Single() == GatewayChatClient.AnswerStep
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
                {
                    Content = new StringContent("""{"error":{"code":"budget_exceeded","message":"The key's budget (USD 50 per month) is used up."}}""", Encoding.UTF8,
                        "application/json"),
                })
                : base.SendAsync(request, cancellationToken);
    }

    /// <summary>The <c>timings</c> member: whole, non-negative milliseconds, <c>total</c> at least the sum of the others (§18.9).</summary>
    private static Dictionary<string, long> Timings(JsonElement kalla)
    {
        var timings = kalla.GetProperty("timings");
        var values = timings.EnumerateObject().ToDictionary(p => p.Name, p =>
        {
            p.Value.ValueKind.ShouldBe(JsonValueKind.Number);
            p.Value.TryGetInt64(out var ms).ShouldBeTrue($"{p.Name} is not a whole number: {p.Value.GetRawText()}");
            ms.ShouldBeGreaterThanOrEqualTo(0);
            return ms;
        });
        values.Keys.ShouldBeSubsetOf(["condense", "retrieve", "first_token", "total"]);
        values.ShouldContainKey("total");
        values["total"].ShouldBeGreaterThanOrEqualTo(values.Where(v => v.Key != "total").Sum(v => v.Value));
        return values;
    }

    [Fact]
    public async Task Timings_on_a_streamed_first_turn_have_no_condense_and_go_in_the_first_kalla_chunk_after_the_text()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        var events = await EventsAsync(await AskUme(client, AltanQuestion, """{"timings":true}""", stream: true));

        var final = events.FindIndex(e => FinishReason(e) == "stop");
        var kalla = events[final + 1].GetProperty("kalla");
        Timings(kalla).Keys.ToArray().ShouldBe(["retrieve", "first_token", "total"]);
        events.Take(final).ShouldNotContain(e => IsKalla(e)); // no progress chunk without progress
    }

    [Fact]
    public async Task Timings_of_a_condensed_follow_up_carry_the_condense_time()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway);
        using var client = Client(api, db.IntegrationKey);
        var messages = new[]
        {
            new { role = "user", content = "Behöver jag bygglov för en altan?" }, new { role = "assistant", content = "Det beror på höjden." },
            new { role = "user", content = "Gäller det även en hög altan?" },
        };

        using var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "bygg", messages, kalla = JsonDocument.Parse("""{"timings":true}""").RootElement.Clone() }, Ct);

        var (_, kalla) = await KallaAnswerAsync(response);
        calls.ShouldContain(c => c.Step == Condenser.Step);
        Timings(kalla).Keys.ToArray().ShouldBe(["condense", "retrieve", "first_token", "total"]);
    }

    [Fact]
    public async Task A_fixed_text_has_no_first_token_and_the_json_answer_has_the_same_values_in_server_timing()
    {
        await using var api = GeneralHost("off");
        using var client = Client(api, db.IntegrationKey);

        using var notFound = await AskUme(client, "Hur långt är det till månen i kilometer?", """{"timings":true}""");
        var (_, fixedKalla) = await KallaAnswerAsync(notFound);
        var fixedTimes = Timings(fixedKalla);
        fixedTimes.Keys.ToArray().ShouldBe(["retrieve", "total"]);
        notFound.Headers.GetValues("Server-Timing").ShouldHaveSingleItem().ShouldBe($"retrieve;dur={fixedTimes["retrieve"]}, total;dur={fixedTimes["total"]}");

        using var answered = await AskUme(client, AltanQuestion, """{"timings":true}""");
        var (_, kalla) = await KallaAnswerAsync(answered);
        var times = Timings(kalla);
        times.Keys.ToArray().ShouldBe(["retrieve", "first_token", "total"]);
        answered.Headers.GetValues("Server-Timing").ShouldHaveSingleItem()
            .ShouldBe($"retrieve;dur={times["retrieve"]}, first_token;dur={times["first_token"]}, total;dur={times["total"]}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"related_documents":2}""")]
    [InlineData("""{"timings":false,"progress":false}""")]
    public async Task Without_timings_there_is_no_timings_member_and_no_server_timing_header(string? kalla)
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        using var response = kalla is null ? await Ask(client, AltanQuestion) : await AskUme(client, AltanQuestion, kalla);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Server-Timing").ShouldBeFalse();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        if (body.TryGetProperty("kalla", out var member))
        {
            member.TryGetProperty("timings", out _).ShouldBeFalse();
        }
    }
}
