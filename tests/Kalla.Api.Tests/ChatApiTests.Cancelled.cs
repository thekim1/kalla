extern alias fakegateway;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Answering.Chat;
using Kalla.TestKit;
using FakeGatewayOptions = fakegateway::Kalla.FakeGateway.FakeGatewayOptions;

namespace Kalla.Api.Tests;

/// <summary>
/// B-48 (chat design §8.3, §12, §18.4): a streamed answer that the client's disconnect or the idle timeout ends is recorded
/// as any turn, with <c>Outcome = cancelled</c>: the user's message and the answer's rows, the turn's usage (the aborted
/// answer at its prompt estimate) and the day's <c>Cancelled</c>; a stream that breaks off otherwise ends <c>error</c> and
/// counts in <c>Errors</c>.
/// </summary>
public sealed partial class ChatApiTests
{
    private sealed record DayCounters(long Messages, long Cancelled, long Errors);

    /// <summary><see cref="CostFake"/> whose <c>fake-stream-stall</c> stalls <paramref name="seconds"/> after its first word (the fixture's 1 s is too short here).</summary>
    private WebApplicationFactory<fakegateway::Program> Stalling(int seconds) => CostFake().WithWebHostBuilder(web =>
        web.ConfigureTestServices(services => services.PostConfigure<FakeGatewayOptions>(o => o.StallSeconds = seconds)));

    private async Task<DayCounters> CountersAsync(WebApplicationFactory<Program> api)
    {
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT coalesce(sum("Messages"), 0), coalesce(sum("Cancelled"), 0), coalesce(sum("Errors"), 0) FROM chat."AssistantDailyTotals"
            WHERE "AssistantId" = $1 AND "ClientId" = $2 AND "Day" = (now() AT TIME ZONE 'Europe/Stockholm')::date
            """, connection) { Parameters = { new() { Value = db.AssistantId }, new() { Value = db.IntegrationClient } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return new DayCounters(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>The roles and outcomes of the conversation's messages that the answer <paramref name="answerId"/> (a <c>chatcmpl-</c> id) belongs to.</summary>
    private async Task<List<(string Role, string? Outcome)>> TurnMessagesAsync(string answerId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT m."Role", m."Outcome" FROM chat."Messages" m
            WHERE m."ConversationId" = (SELECT "ConversationId" FROM chat."Messages" WHERE "Id" = $1) ORDER BY m."CreatedAt", m."Role" DESC
            """, connection) { Parameters = { new() { Value = Guid.ParseExact(answerId["chatcmpl-".Length..], "N") } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string?)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    private static string AnswerId(string sse) => Regex.Match(sse, "\"id\":\"(chatcmpl-[0-9a-f]{32})\"").Groups[1].Value;

    /// <summary>Polls <paramref name="read"/> until <paramref name="done"/> (the writer's flush runs after the request ends), at most 10 s.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value) || waited.Elapsed > TimeSpan.FromSeconds(10))
            {
                return value;
            }

            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task An_idle_timeout_records_the_turn_as_cancelled_with_its_usage_and_the_days_Cancelled()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = Stalling(10);
        await using var api = CostHost(calls, gateway, model: "fake-stream-stall", rerank: false).WithWebHostBuilder(web =>
            web.ConfigureTestServices(services => services.PostConfigure<ChatOptions>(o => o.StreamIdleTimeoutSeconds = 1)));
        using var client = Client(api, db.IntegrationKey);
        var before = await CountersAsync(api);
        var usage = await UsageAsync(api);

        using var response = await AskCost(client, "Behöver jag bygglov för en altan i trä?", stream: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sse = await response.Content.ReadAsStringAsync(Ct);
        sse.ShouldContain("stream_interrupted");

        var after = await EventuallyAsync(() => CountersAsync(api), c => c.Cancelled > before.Cancelled);
        after.ShouldBe(before with { Messages = before.Messages + 1, Cancelled = before.Cancelled + 1 });
        (await TurnMessagesAsync(AnswerId(sse))).ShouldBe([("user", "cancelled"), ("assistant", "cancelled")]); // the turn's outcome on both rows
        var answer = Added(usage, await UsageAsync(api))[GatewayChatClient.AnswerStep];
        (answer.Calls, answer.EstimatedCalls, answer.OutputTokens).ShouldBe((1, 1, 0L)); // the aborted answer at its prompt estimate
        answer.InputTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_stream_leaves_a_cancelled_turn()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = Stalling(30);
        await using var api = CostHost(calls, gateway, model: "fake-stream-stall", rerank: false);
        using var client = Client(api, db.IntegrationKey);
        var before = await CountersAsync(api);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "bygg", stream = true, messages = new[] { new { role = "user", content = "Behöver jag bygglov för en altan med tak?" } } }),
        };
        string answerId;
        // Cancelling the request's token is how TestServer's client disconnects (RequestAborted on the server).
        using var leave = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, leave.Token))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await using var body = await response.Content.ReadAsStreamAsync(leave.Token);
            using var reader = new StreamReader(body);
            var first = "";
            while (!first.Contains("\"content\":\"", StringComparison.Ordinal) || first.Contains("\"content\":\"\"", StringComparison.Ordinal))
            {
                first = await reader.ReadLineAsync(leave.Token) ?? throw new InvalidOperationException("The stream ended before its first word.");
            }

            answerId = AnswerId(first);
            await leave.CancelAsync(); // the client goes away while the model stalls
        }

        var after = await EventuallyAsync(() => CountersAsync(api), c => c.Cancelled > before.Cancelled);
        after.ShouldBe(before with { Messages = before.Messages + 1, Cancelled = before.Cancelled + 1 });
        (await TurnMessagesAsync(answerId)).ShouldBe([("user", "cancelled"), ("assistant", "cancelled")]);
    }
}
