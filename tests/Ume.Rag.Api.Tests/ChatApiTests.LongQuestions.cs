extern alias fakegateway;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Answering;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Retrieval;
using Ume.Rag.TestKit;
using FakeChatScripts = fakegateway::Ume.Rag.FakeGateway.FakeChatScripts;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// Long questions through the chat role (chat design §3.2, §5.1, §6.2, §17, §18.6, §18.9; M-6) on the knowledge fixture: the
/// <c>condense-long</c> call and its output as the retrieval query, the 16,000-character limit, the prompt form reaching the
/// answer model, and the word-boundary fallback with its reason on timeout, error, residency, rejection and condense off.
/// The FakeGateway answers through a recording handler that can fail the <c>condense-long</c> call; the chat service runs on
/// a <see cref="FakeTimeProvider"/> that the handler advances past the budget, so the timeout needs no wall-clock wait.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>How the recording gateway answers the <c>condense-long</c> call.</summary>
    public enum LongFault
    {
        None,
        Timeout,
        Error,
        Residency,
        Rejected,
    }

    /// <summary>One request the chat role sent to the gateway: path, <c>x-ume-step</c>, ceiling and JSON body.</summary>
    private sealed record GatewayCall(string Path, string? Step, string? Ceiling, JsonElement Body);

    /// <summary>The FakeGateway behind a recorder; the <c>condense-long</c> call may be failed as <paramref name="fault"/> says.</summary>
    private sealed class LongGateway(ConcurrentQueue<GatewayCall> calls, LongFault fault, FakeTimeProvider time) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = request.Headers.TryGetValues(FakeChatScripts.StepHeader, out var s) ? s.Single() : null;
            var ceiling = request.Headers.TryGetValues(Infrastructure.Gateway.GatewayHeaders.MaxResidency, out var c) ? c.Single() : null;
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            calls.Enqueue(new GatewayCall(request.RequestUri!.AbsolutePath, step, ceiling, body));
            if (step != Condenser.LongStep || fault == LongFault.None)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            switch (fault)
            {
                case LongFault.Timeout:
                    time.Advance(new ChatOptions().LongQuestionCondenseTimeout); // the budget runs out while the gateway thinks
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                case LongFault.Error:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("""{"error":{"code":"provider_error","message":"Upstream failed."}}""", Encoding.UTF8, "application/json"),
                    };
                default:
                    var answer = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"choices":[{"message":{"content":"Rad ett\nRad två"},"finish_reason":"stop"}],"usage":{"prompt_tokens":900,"completion_tokens":5}}""",
                            Encoding.UTF8, "application/json"),
                    };
                    answer.Headers.Add(Infrastructure.Gateway.GatewayHeaders.Residency, fault == LongFault.Residency ? "External" : "Eu");
                    return answer;
            }
        }
    }

    /// <summary>
    /// The chat host with <c>Settings.Chat.condenseModel</c> (unless <paramref name="condenseModel"/> is false), the assistant's
    /// <paramref name="querySteps"/>, the recording gateway (the fixture's FakeGateway, or <paramref name="gateway"/>) for chat and
    /// query embeddings, and the chat service on <paramref name="time"/>.
    /// </summary>
    private WebApplicationFactory<Program> LongHost(ConcurrentQueue<GatewayCall> calls, LongFault fault, FakeTimeProvider time, CapturingLoggerProvider logs,
        string? querySteps = null, bool condenseModel = true, WebApplicationFactory<fakegateway::Program>? gateway = null)
    {
        var upstream = gateway ?? db.Gateway;
        var snapshot = Snapshot();
        var bygg = snapshot.Assistants[db.AssistantId];
        snapshot = snapshot with
        {
            ChatSettings = condenseModel ? """{"condenseModel":"ume/chat-eu","condenseReasoningEffort":"none"}""" : null,
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = bygg with { Published = bygg.Published! with { QuerySteps = querySteps ?? bygg.Published.QuerySteps } },
            },
        };
        return db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            web.ConfigureLogging(l => l.AddProvider(logs));
            web.ConfigureTestServices(services =>
            {
                services.UseGatewayCredentialCeiling();
                services.AddHttpClient(GatewayChatClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new LongGateway(calls, fault, time) { InnerHandler = upstream.Server.CreateHandler() });
                services.AddHttpClient(GatewayQueryEmbedder.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new LongGateway(calls, LongFault.None, time) { InnerHandler = upstream.Server.CreateHandler() });
                services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ChatService>(sp, (TimeProvider)time));
            });
        });
    }

    /// <summary>Records <c>kind/outcome</c> of <c>rag.chat.condense</c> from <paramref name="api"/>'s meters only.</summary>
    private static MeterListener CondenseCounter(WebApplicationFactory<Program> api, ConcurrentQueue<string> counted)
    {
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "rag.chat.condense" && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? kind = null, outcome = null;
            foreach (var tag in tags)
            {
                kind = tag.Key == "kind" ? tag.Value as string : kind;
                outcome = tag.Key == "outcome" ? tag.Value as string : outcome;
            }

            counted.Enqueue($"{kind}/{outcome}");
        });
        listener.Start();
        return listener;
    }

    private static Task<HttpResponseMessage> AskWith(HttpClient client, string question, params object[] history) =>
        client.PostAsJsonAsync("/v1/chat/completions", new { model = "bygg", messages = history.Append(new { role = "user", content = question }) }, Ct);

    private static async Task<string> AnswerAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
    }

    /// <summary>The query the turn searched with: the input of its (only) query embedding call.</summary>
    private static string Searched(IEnumerable<GatewayCall> calls) =>
        calls.Single(c => c.Path == "/v1/embeddings").Body.GetProperty("input").GetString()!;

    /// <summary><paramref name="sentence"/> repeated with a counter into a question of <paramref name="length"/> + 1 characters (query form).</summary>
    private static string Long(string sentence, int length)
    {
        var text = new StringBuilder();
        for (var i = 1; text.Length < length; i++)
        {
            text.Append(text.Length == 0 ? "" : " ").Append(sentence).Append(" (").Append(i).Append(')');
        }

        var cut = text.ToString()[..length];
        return cut.EndsWith(' ') ? cut[..^1] + "xx" : cut + "x";
    }

    [Fact]
    public async Task A_question_of_2000_characters_is_the_query_and_one_of_2001_is_condensed_on_the_first_turn()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var exact = Long("Behöver jag bygglov för en altan högre än 1,2 meter?", 1999);
        exact.Length.ShouldBe(QueryNormalizer.MaxLength);
        (await AnswerAsync(await AskWith(client, exact))).ShouldStartWith("Testsvar på");
        calls.ShouldNotContain(c => c.Step == Condenser.LongStep || c.Step == Condenser.Step);
        Searched(calls).ShouldBe(exact);

        calls.Clear();
        var longer = Long("Behöver jag bygglov för en altan högre än 1,2 meter?", 2000);
        longer.Length.ShouldBe(QueryNormalizer.MaxLength + 1);
        (await AnswerAsync(await AskWith(client, longer))).ShouldStartWith("Testsvar på");
        var condense = calls.Where(c => c.Step is Condenser.LongStep or Condenser.Step).ShouldHaveSingleItem();
        condense.Step.ShouldBe(Condenser.LongStep);
        condense.Ceiling.ShouldBe("Eu");
        (condense.Body.GetProperty("model").GetString(), condense.Body.GetProperty("max_tokens").GetInt32(), condense.Body.GetProperty("reasoning_effort").GetString())
            .ShouldBe(("ume/chat-eu", 96, "none"));
        condense.Body.TryGetProperty("temperature", out _).ShouldBeFalse();
        var user = condense.Body.GetProperty("messages")[1].GetProperty("content").GetString()!;
        user.ShouldBe("Meddelande: " + longer);
        Searched(calls).ShouldBe(FakeChatScripts.DefaultCondenseLong(user)); // the condensed query, not the question
        logs.Text.ShouldNotContain("searched by its first 2,000 characters");
    }

    [Fact]
    public async Task A_question_of_16000_characters_is_accepted_and_16001_is_refused()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var longest = Long("Behöver jag bygglov för en altan högre än 1,2 meter?", ChatRequestParser.MaxQuestionChars - 1);
        (await AnswerAsync(await AskWith(client, longest))).ShouldStartWith("Testsvar på");
        using (var refused = await AskWith(client, longest + "y"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("question_too_long");
        }
    }

    [Fact]
    public async Task A_pasted_ticket_reaches_the_answer_model_line_for_line()
    {
        // Condense and retrieval get the query form; the answer model gets every line as written (CRLF read as LF). The
        // condense-long answer is scripted to a query the fixture covers, so the turn reaches the answer model.
        const string Query = "Behöver jag bygglov för en altan högre än 1,2 meter?";
        await using var gateway = db.Gateway.WithWebHostBuilder(web => web.UseSetting(FakeChatScripts.SectionName + ":" + Condenser.LongStep, Query));
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, gateway: gateway);
        using var client = Client(api, db.IntegrationKey);

        var ticket = LongQuestionTests.Ticket();
        (await AnswerAsync(await AskWith(client, ticket.Replace("\n", "\r\n", StringComparison.Ordinal)))).ShouldStartWith("Testsvar på");
        var answer = calls.Single(c => c.Step == "chat-answer");
        var messages = answer.Body.GetProperty("messages");
        messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!.ShouldEndWith("\n\nFråga: " + ticket);
        calls.Single(c => c.Step == Condenser.LongStep).Body.GetProperty("messages")[1].GetProperty("content").GetString()
            .ShouldBe("Meddelande: " + QueryNormalizer.Normalize(ticket));
        Searched(calls).ShouldBe(Query);
    }

    public static TheoryData<string, LongFault, string?, bool> Fallbacks { get; } = new()
    {
        { "timeout", LongFault.Timeout, null, true },
        { "error", LongFault.Error, null, true },
        { "residency", LongFault.Residency, null, true },
        { "rejected", LongFault.Rejected, null, true },
        { "disabled", LongFault.None, """{"condense":{"enabled":false}}""", true },
        { "disabled", LongFault.None, null, false },
    };

    [Theory]
    [MemberData(nameof(Fallbacks))]
    public async Task Without_a_condensed_query_a_long_question_searches_its_first_2000_characters_and_answers_from_low_coverage(string reason, LongFault fault,
        string? querySteps, bool condenseModel)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var counted = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, fault, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, querySteps, condenseModel);
        using var listener = CondenseCounter(api, counted);
        using var client = Client(api, db.IntegrationKey);

        // The fixture has nothing on the moon: coverage Low. After a failed condense the turn answers from what was found (§6.2).
        var question = Long("Hur långt är det till månen i kilometer räknat från Umeå?", 2400);
        (await AnswerAsync(await AskWith(client, question))).ShouldStartWith("Testsvar på");
        Searched(calls).ShouldBe(Condenser.Fallback(question));
        calls.Count(c => c.Step == Condenser.LongStep).ShouldBe(reason == "disabled" ? 0 : 1);
        counted.ShouldBe(["long/" + reason]);

        var fallback = logs.Entries.Where(e => e.Category == typeof(ChatService).FullName && e.Message.Contains("first 2,000 characters", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        fallback.Level.ShouldBe(LogLevel.Information);
        fallback.Message.ShouldEndWith("condense " + reason);
        logs.Text.ShouldNotContain("månen"); // never the text
        if (fault == LongFault.Residency)
        {
            logs.Entries.ShouldContain(e => e.Category == Infrastructure.Security.SecurityEvents.Category && e.EventId.Id == 5003);
        }
    }

    [Fact]
    public async Task A_condensed_long_question_with_low_coverage_gets_the_not_found_text()
    {
        // The counterpart of the fallbacks: a condense that worked is no reason to answer from weak hits.
        var calls = new ConcurrentQueue<GatewayCall>();
        var counted = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var listener = CondenseCounter(api, counted);
        using var client = Client(api, db.IntegrationKey);

        (await AnswerAsync(await AskWith(client, Long("Hur långt är det till månen i kilometer räknat från Umeå?", 2400)))).ShouldBe("Inget svar i källorna.");
        counted.ShouldBe(["long/used"]);
    }

    [Fact]
    public async Task A_long_follow_up_makes_exactly_one_condense_call()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var followUp = Long("Gäller det även en altan som är högre än 1,2 meter?", 3000); // an anaphora: a follow-up condense on its own
        (await AnswerAsync(await AskWith(client, followUp, new { role = "user", content = "Behöver jag bygglov för en altan?" },
            new { role = "assistant", content = "Ja, över 1,2 meter [1]." }))).ShouldStartWith("Testsvar på");
        var condense = calls.Where(c => c.Step is Condenser.LongStep or Condenser.Step).ShouldHaveSingleItem();
        condense.Step.ShouldBe(Condenser.LongStep);
        condense.Body.GetProperty("messages")[1].GetProperty("content").GetString()
            .ShouldBe("Användare: Behöver jag bygglov för en altan?\nAssistent: Ja, över 1,2 meter.\n\nMeddelande: " + followUp);
    }
}
