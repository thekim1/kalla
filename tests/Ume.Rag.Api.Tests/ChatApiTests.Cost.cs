extern alias fakegateway;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Answering;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Retrieval;
using Ume.Rag.TestKit;
using FakeCatalog = fakegateway::Ume.Rag.FakeGateway.FakeCatalog;
using FakeChatScripts = fakegateway::Ume.Rag.FakeGateway.FakeChatScripts;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// Cost per step (chat design §8.3, §12.1, §12.2, §17, §18.2; B-38) on the knowledge fixture with the FakeGateway: every model
/// call of a turn (condense, query embedding, rerank, answer, follow-ups) writes its row of <c>AssistantDailyUsage</c> with the
/// FakeGateway's tokens and cost, the streamed answer's cost from the comment that never reaches the client, the estimate on
/// <c>fake-no-cost</c>, the usage of a turn whose answer call fails or whose stream breaks off, nothing for a cached query
/// embedding, and the counters <c>rag.chat.tokens</c> and <c>rag.chat.cost</c>. The FakeGateway answers through a recorder
/// that reads each answer's tokens and cost as the gateway sent them.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>The condensed query the FakeGateway scripts for the <c>condense</c> step.</summary>
    private const string CostQuery = "Behöver jag bygglov för en altan högre än 1,8 meter?";

    /// <summary>The prompt tokens every chat answer of the FakeGateway reports as cached in these tests.</summary>
    private const int CostCachedTokens = 16;

    /// <summary>The mirror's prices of the FakeGateway's chat models (<c>FakeCatalog.PriceOf</c>): cached input at its own price.</summary>
    private static readonly ModelPricesEntry ChatPrices = new(0.15m, 0.075m, 0.60m);

    /// <summary>What the FakeGateway answered one call with: its step, the tokens it reported and its cost (header or stream comment).</summary>
    private sealed record CostCall(string? Step, bool StreamCostAsked, GatewayTokens Tokens, decimal? CostUsd, bool Streamed);

    /// <summary>
    /// The FakeGateway behind a recorder: each <c>2xx</c> answer is buffered and its usage and cost read as the gateway sent them
    /// (a stream's usage chunk and its comment); the call of step <paramref name="failStep"/> is refused with 503. A model whose
    /// stream breaks off is passed on unread.
    /// </summary>
    private sealed class CostGateway(ConcurrentQueue<CostCall> calls, string? failStep) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = request.Headers.TryGetValues(FakeChatScripts.StepHeader, out var s) ? s.Single() : null;
            var asked = request.Headers.TryGetValues(GatewayHeaders.StreamCost, out var c) && c.Single() == "1";
            if (step == failStep)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"error":{"code":"provider_unavailable","message":"Upstream failed."}}""", Encoding.UTF8, "application/json"),
                };
            }

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            // A stream that breaks off or stalls is passed on unread, so rag-api sees the break or the stall as it happens (B-48).
            if (!response.IsSuccessStatusCode || body.Contains("fake-stream-broken", StringComparison.Ordinal) || body.Contains("fake-stream-stall", StringComparison.Ordinal))
            {
                return response;
            }

            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                var lines = text.Split('\n');
                var usage = lines.Last(l => l.StartsWith("data: {", StringComparison.Ordinal) && l.Contains("\"usage\":{", StringComparison.Ordinal));
                using var chunk = JsonDocument.Parse(usage["data: ".Length..]);
                var comment = lines.SingleOrDefault(l => l.StartsWith(GatewayUsageReader.CostCommentPrefix, StringComparison.Ordinal));
                calls.Enqueue(new CostCall(step, asked, GatewayUsageReader.Tokens(chunk.RootElement)!.Value,
                    comment is null ? null : decimal.Parse(comment[GatewayUsageReader.CostCommentPrefix.Length..], CultureInfo.InvariantCulture), Streamed: true));
            }
            else
            {
                using var json = JsonDocument.Parse(text);
                calls.Enqueue(new CostCall(step, asked, GatewayUsageReader.Tokens(json.RootElement)!.Value, GatewayUsageReader.CostHeader(response), Streamed: false));
            }

            return response;
        }
    }

    /// <summary>
    /// The chat host for cost: the condense model, the platform's rerank model and the assistant reranking (unless
    /// <paramref name="rerank"/> is false), the answer model <paramref name="model"/>, the mirror's prices of the chat models, and a
    /// FakeGateway that scripts the condense query and reports <see cref="CostCachedTokens"/> cached tokens, behind
    /// <see cref="CostGateway"/> for chat, rerank and the query embedding.
    /// </summary>
    private WebApplicationFactory<Program> CostHost(ConcurrentQueue<CostCall> calls, WebApplicationFactory<fakegateway::Program> gateway, string model = "ume/chat-eu",
        bool rerank = true, string? failStep = null, Guid? gatewayKeyId = null, GatewayKeyEntry? keyRow = null)
    {
        var snapshot = Snapshot();
        var bygg = snapshot.Assistants[db.AssistantId];
        snapshot = snapshot with
        {
            ChatSettings = """{"condenseModel":"ume/chat-eu","condenseReasoningEffort":"none"}""",
            RetrievalSettings = rerank ? """{"rerankModel":"ume/chat-eu"}""" : null,
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = bygg with
                {
                    GatewayKeyId = gatewayKeyId, // B-42: the key the turn runs as
                    Published = bygg.Published! with { GatewayModel = model, QuerySteps = rerank ? """{"retrieval":{"rerank":"chat"}}""" : bygg.Published.QuerySteps },
                },
            },
            // B-44: the assistant's key in the mirror (usable unless the test says otherwise), as chat's step 7 reads it.
            GatewayKeys = gatewayKeyId is { } key
                ? new Dictionary<Guid, GatewayKeyEntry> { [key] = keyRow ?? new GatewayKeyEntry(key, "Active", OwnerActive: true, Usable: true) }
                : new Dictionary<Guid, GatewayKeyEntry>(),
            ModelPrices = new Dictionary<string, ModelPricesEntry>(StringComparer.Ordinal)
            {
                ["ume/chat-eu"] = ChatPrices, [FakeCatalog.NoCostModel] = ChatPrices, ["fake-stream-broken"] = ChatPrices,
            },
        };
        return db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            web.ConfigureTestServices(services =>
            {
                foreach (var name in new[] { GatewayChatClient.HttpClientName, GatewayQueryEmbedder.HttpClientName, ChatModelReranker.HttpClientName })
                {
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new CostGateway(calls, failStep) { InnerHandler = gateway.Server.CreateHandler() });
                }
            });
        });
    }

    /// <summary>The fixture's FakeGateway with the scripted condense query and <see cref="CostCachedTokens"/> cached prompt tokens.</summary>
    private WebApplicationFactory<fakegateway::Program> CostFake() => db.Gateway.WithWebHostBuilder(web =>
    {
        web.UseSetting(FakeChatScripts.SectionName + ":" + Condenser.Step, CostQuery);
        web.UseSetting("FakeGateway:CachedInputTokens", CostCachedTokens.ToString(CultureInfo.InvariantCulture));
    });

    /// <summary>
    /// A follow-up question (condensed: it points back with "det") after one earlier turn, as a stream or JSON, with
    /// <paramref name="followUps"/> follow-up suggestions asked for (none: no <c>ume</c> member).
    /// </summary>
    private static Task<HttpResponseMessage> AskFollowUp(HttpClient client, bool stream, int followUps = 2)
    {
        var messages = new[]
        {
            new { role = "user", content = "Behöver jag bygglov för en altan?" }, new { role = "assistant", content = "Det beror på höjden." },
            new { role = "user", content = "Gäller det även en hög altan?" },
        };
        object body = followUps > 0
            ? new { model = "bygg", stream, messages, ume = JsonDocument.Parse($$"""{"follow_ups":{{followUps}}}""").RootElement.Clone() }
            : new { model = "bygg", stream, messages };
        return client.PostAsJsonAsync("/v1/chat/completions", body, Ct);
    }

    private static Task<HttpResponseMessage> AskCost(HttpClient client, string question, bool stream) =>
        client.PostAsJsonAsync("/v1/chat/completions", new { model = "bygg", stream, messages = new[] { new { role = "user", content = question } } }, Ct);

    /// <summary>One <c>AssistantDailyUsage</c> row's counters.</summary>
    private sealed record UsageRow(int Calls, long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, int EstimatedCalls, decimal EstimatedCostUsd)
    {
        public static readonly UsageRow None = new(0, 0, 0, 0, 0m, 0, 0m);

        public UsageRow Minus(UsageRow other) => new(Calls - other.Calls, InputTokens - other.InputTokens, CachedInputTokens - other.CachedInputTokens,
            OutputTokens - other.OutputTokens, CostUsd - other.CostUsd, EstimatedCalls - other.EstimatedCalls, EstimatedCostUsd - other.EstimatedCostUsd);
    }

    /// <summary>
    /// Today's (Europe/Stockholm) usage rows of the assistant, the integration client and the assistant's level (<c>internal</c>)
    /// by step, after the writer has flushed.
    /// </summary>
    private async Task<Dictionary<string, UsageRow>> UsageAsync(WebApplicationFactory<Program> api)
    {
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Step", "Calls", "InputTokens", "CachedInputTokens", "OutputTokens", "CostUsd", "EstimatedCalls", "EstimatedCostUsd", "DepartmentId"
            FROM chat."AssistantDailyUsage"
            WHERE "AssistantId" = $1 AND "ClientId" = $2 AND "Classification" = 'internal' AND "Day" = (now() AT TIME ZONE 'Europe/Stockholm')::date
            """, connection) { Parameters = { new() { Value = db.AssistantId }, new() { Value = db.IntegrationClient } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<string, UsageRow>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            reader.GetGuid(8).ShouldBe(Guid.Empty); // the assistant's department in the fixture's snapshot
            rows[reader.GetString(0)] = new UsageRow(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetDecimal(5),
                reader.GetInt32(6), reader.GetDecimal(7));
        }

        return rows;
    }

    /// <summary>The rows added between <paramref name="before"/> and <paramref name="after"/>, by step; steps that did not move are left out.</summary>
    private static Dictionary<string, UsageRow> Added(Dictionary<string, UsageRow> before, Dictionary<string, UsageRow> after) =>
        after.Select(a => (a.Key, Row: a.Value.Minus(before.GetValueOrDefault(a.Key, UsageRow.None)))).Where(x => x.Row != UsageRow.None)
            .ToDictionary(x => x.Key, x => x.Row, StringComparer.Ordinal);

    /// <summary>Today's <c>Messages</c> count of the assistant and the integration client in the daily totals.</summary>
    private async Task<long> TotalMessagesAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT coalesce(sum("Messages"), 0) FROM chat."AssistantDailyTotals"
            WHERE "AssistantId" = $1 AND "ClientId" = $2 AND "Day" = (now() AT TIME ZONE 'Europe/Stockholm')::date
            """, connection) { Parameters = { new() { Value = db.AssistantId }, new() { Value = db.IntegrationClient } } };
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>The row a call adds at the gateway's cost.</summary>
    private static UsageRow Charged(CostCall call) =>
        new(1, call.Tokens.InputTokens, call.Tokens.CachedInputTokens, call.Tokens.OutputTokens, call.CostUsd!.Value, 0, 0m);

    [Fact]
    public async Task A_turn_writes_one_usage_row_per_step_with_the_gateways_tokens_and_cost()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway);
        using var client = Client(api, db.IntegrationKey);
        var before = await UsageAsync(api);

        using var response = await AskFollowUp(client, stream: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var wire = await response.Content.ReadAsStringAsync(Ct);
        var events = Events(wire);
        events.Count(e => e.TryGetProperty("ume", out var ume) && ume.TryGetProperty("follow_ups", out var f) && f.GetArrayLength() > 0).ShouldBe(1);

        // §8.3, §9.1: every call answered with its cost; the stream was asked for its cost, and its comment never reaches the client.
        var byStep = calls.ToDictionary(c => c.Step!, StringComparer.Ordinal);
        byStep.Keys.ShouldBe([Condenser.Step, GatewayQueryEmbedder.Step, ChatModelReranker.Step, GatewayChatClient.AnswerStep, FollowUps.Step], ignoreOrder: true);
        byStep.Values.ShouldAllBe(c => c.CostUsd > 0m);
        var answer = byStep[GatewayChatClient.AnswerStep];
        (answer.Streamed, answer.StreamCostAsked).ShouldBe((true, true));
        calls.Where(c => c.Step != GatewayChatClient.AnswerStep).ShouldAllBe(c => !c.StreamCostAsked && !c.Streamed);
        wire.ShouldNotContain("ume-cost-usd");
        wire.Split('\n').ShouldNotContain(l => l.StartsWith(':'));
        answer.Tokens.CachedInputTokens.ShouldBe(CostCachedTokens);
        byStep[Condenser.Step].Tokens.CachedInputTokens.ShouldBe(CostCachedTokens);

        // One row per step with exactly the FakeGateway's tokens and cost, all from the gateway (none estimated).
        var added = Added(before, await UsageAsync(api));
        added.Keys.ShouldBe(byStep.Keys, ignoreOrder: true);
        foreach (var (step, call) in byStep)
        {
            added[step].ShouldBe(Charged(call), step);
        }

        // §12.2: the answer message's cost is the answer call's.
        var messageId = Guid.ParseExact(events[0].GetProperty("id").GetString()!["chatcmpl-".Length..], "N");
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var cost = new Npgsql.NpgsqlCommand("""SELECT "CostUsd" FROM chat."Messages" WHERE "Id" = $1""", connection) { Parameters = { new() { Value = messageId } } };
        ((decimal)(await cost.ExecuteScalarAsync(Ct))!).ShouldBe(answer.CostUsd!.Value);
    }

    [Fact]
    public async Task On_fake_no_cost_the_answer_is_estimated_from_the_mirrors_prices_and_kept_apart()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, model: FakeCatalog.NoCostModel, rerank: false);
        using var client = Client(api, db.IntegrationKey);
        var before = await UsageAsync(api);

        using (var json = await AskCost(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", stream: false))
        {
            json.StatusCode.ShouldBe(HttpStatusCode.OK, await json.Content.ReadAsStringAsync(Ct));
        }

        using (var stream = await AskCost(client, "Behöver jag bygglov för en altan?", stream: true))
        {
            stream.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await stream.Content.ReadAsStringAsync(Ct)).ShouldNotContain("ume-cost-usd");
        }

        var answers = calls.Where(c => c.Step == GatewayChatClient.AnswerStep).ToList();
        answers.Count.ShouldBe(2);
        answers.ShouldAllBe(a => a.CostUsd == null && a.Tokens.CachedInputTokens == CostCachedTokens);
        // (input − cached) × input price + cached × the cached price + output × output price, per million, as FakeCatalog.Cost prices it.
        var estimate = answers.Sum(a => Math.Round(((a.Tokens.InputTokens - a.Tokens.CachedInputTokens) * 0.15m + a.Tokens.CachedInputTokens * 0.075m +
                                                    a.Tokens.OutputTokens * 0.60m) / 1_000_000m, 8, MidpointRounding.AwayFromZero));
        estimate.ShouldBeGreaterThan(0m);
        var added = Added(before, await UsageAsync(api));
        added[GatewayChatClient.AnswerStep].ShouldBe(new UsageRow(2, answers.Sum(a => a.Tokens.InputTokens), 2 * CostCachedTokens, answers.Sum(a => a.Tokens.OutputTokens),
            0m, 2, estimate));
        added[GatewayQueryEmbedder.Step].EstimatedCalls.ShouldBe(0); // the embedding model still sends its cost
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_answer_call_still_writes_the_condense_and_query_embedding_usage(bool stream)
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, rerank: false, failStep: GatewayChatClient.AnswerStep);
        using var client = Client(api, db.IntegrationKey);
        var before = await UsageAsync(api);
        var messages = await TotalMessagesAsync();

        using var response = await AskFollowUp(client, stream, followUps: 0);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("upstream_unavailable");

        // §12.1: a usage-only turn: condense's and the embedding's rows, no answer row, no message counted.
        var added = Added(before, await UsageAsync(api));
        added.Keys.ShouldBe([Condenser.Step, GatewayQueryEmbedder.Step], ignoreOrder: true);
        added[Condenser.Step].ShouldBe(Charged(calls.Single(c => c.Step == Condenser.Step)));
        added[GatewayQueryEmbedder.Step].ShouldBe(Charged(calls.Single(c => c.Step == GatewayQueryEmbedder.Step)));
        (await TotalMessagesAsync()).ShouldBe(messages);
    }

    [Fact]
    public async Task A_cached_query_embedding_writes_nothing()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, rerank: false);
        using var client = Client(api, db.IntegrationKey);
        var before = await UsageAsync(api);

        for (var turn = 0; turn < 2; turn++)
        {
            using var response = await AskCost(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", stream: false);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        calls.Count(c => c.Step == GatewayQueryEmbedder.Step).ShouldBe(1); // the second turn's vector came from the cache
        var added = Added(before, await UsageAsync(api));
        added[GatewayQueryEmbedder.Step].ShouldBe(Charged(calls.Single(c => c.Step == GatewayQueryEmbedder.Step)));
        added[GatewayChatClient.AnswerStep].Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_stream_that_breaks_off_is_recorded_as_an_error_turn_at_the_prompt_estimate()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway, model: "fake-stream-broken", rerank: false);
        using var client = Client(api, db.IntegrationKey);
        var before = await UsageAsync(api);
        var messages = await TotalMessagesAsync();

        using var response = await AskCost(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", stream: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("stream_interrupted");

        // §8.3: neither usage chunk nor comment: the prompt estimate as input, no output, at an estimated cost; §12.1: usage-only.
        var added = Added(before, await UsageAsync(api));
        var answer = added[GatewayChatClient.AnswerStep];
        answer.InputTokens.ShouldBeGreaterThan(0);
        answer.ShouldBe(new UsageRow(1, answer.InputTokens, 0, 0, 0m, 1, Math.Round(answer.InputTokens * 0.15m / 1_000_000m, 8, MidpointRounding.AwayFromZero)));
        added.ShouldContainKey(GatewayQueryEmbedder.Step);
        // B-48: a stream that breaks off (not a disconnect or a timeout) is an error turn: its rows and one more of the day's Errors.
        (await TotalMessagesAsync()).ShouldBe(messages + 1);
    }

    [Fact]
    public async Task Every_call_of_a_turn_counts_its_tokens_and_cost_by_step_and_source()
    {
        var calls = new ConcurrentQueue<CostCall>();
        await using var gateway = CostFake();
        await using var api = CostHost(calls, gateway);
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        var tokens = new ConcurrentQueue<(string Direction, string Step, long Value)>();
        var costs = new ConcurrentQueue<(string Step, string Source, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name is "rag.chat.tokens" or "rag.chat.cost" && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        static Dictionary<string, string?> Named(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var named = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                named[tag.Key] = tag.Value as string;
            }

            return named;
        }

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var named = Named(tags);
            tokens.Enqueue((named["direction"]!, named["step"]!, value));
        });
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var named = Named(tags);
            costs.Enqueue((named["step"]!, named["source"]!, value));
        });
        listener.Start();
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskFollowUp(client, stream: false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        // §17: input, cached-input and output per step, the reranker's and the query embedding's included; the cost by source.
        foreach (var call in calls)
        {
            tokens.ShouldContain(("input", call.Step!, call.Tokens.InputTokens));
            tokens.ShouldContain(("cached-input", call.Step!, call.Tokens.CachedInputTokens));
            tokens.ShouldContain(("output", call.Step!, call.Tokens.OutputTokens));
            costs.ShouldContain((call.Step!, "gateway", (double)call.CostUsd!.Value));
        }

        tokens.Select(t => t.Step).Distinct().ShouldBe(
            [Condenser.Step, GatewayQueryEmbedder.Step, ChatModelReranker.Step, GatewayChatClient.AnswerStep, FollowUps.Step], ignoreOrder: true);
        tokens.ShouldContain(t => t.Direction == "cached-input" && t.Step == ChatModelReranker.Step && t.Value == CostCachedTokens);

        // A call without a gateway cost counts as an estimate.
        calls.Clear();
        await using var noCost = CostHost(calls, gateway, model: FakeCatalog.NoCostModel, rerank: false);
        var noCostMeters = noCost.Services.GetRequiredService<IMeterFactory>();
        var estimates = new ConcurrentQueue<(string Step, string Source)>();
        using var estimateListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "rag.chat.cost" && ReferenceEquals(instrument.Meter.Scope, noCostMeters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        estimateListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var named = Named(tags);
            estimates.Enqueue((named["step"]!, named["source"]!));
        });
        estimateListener.Start();
        using var noCostClient = Client(noCost, db.IntegrationKey);
        using var estimated = await AskCost(noCostClient, "Behöver jag bygglov för en altan?", stream: false);
        estimated.StatusCode.ShouldBe(HttpStatusCode.OK, await estimated.Content.ReadAsStringAsync(Ct));
        estimates.ShouldContain((GatewayChatClient.AnswerStep, "estimate"));
        estimates.ShouldContain((GatewayQueryEmbedder.Step, "gateway"));
    }
}
