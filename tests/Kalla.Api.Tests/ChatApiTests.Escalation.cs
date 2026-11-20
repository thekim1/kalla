extern alias fakegateway;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Kalla.Answering.Chat;
using Kalla.TestKit;
using FakeChatScripts = fakegateway::Kalla.FakeGateway.FakeChatScripts;

namespace Kalla.Api.Tests;

/// <summary>
/// The escalation hand-off through the chat role (chat design §3.8, §9.9, §17, §18.9; Decisions "Escalation hand-off") on the
/// knowledge fixture: the route for not-found and general-knowledge turns, <c>null</c> for an answered one, one
/// <c>escalation</c> call at the condense ceiling from the user's message, its output through the accept rule, the second
/// <c>ume</c> chunk shared with the follow-ups (both calls at once), the skips without a call, the failures as a null summary,
/// and nothing of the summary in logs or the stored turn.
/// </summary>
public sealed partial class ChatApiTests
{
    private const string Moon = "Hur långt är det till månen i kilometer?";

    private const string HelpLinks = """{"sv":"Inget svar i källorna.","links":[{"title":"IT-support, 090-16 10 00","url":"https://www.umea.se/it"}]}""";

    /// <summary>How the recording gateway answers the <c>escalation</c> call.</summary>
    public enum EscalationFault
    {
        /// <summary>The FakeGateway's default answer (the ticket's first words).</summary>
        None,

        /// <summary>The scripted output.</summary>
        Output,

        /// <summary>The budget runs out while the gateway thinks (the handler advances the fake clock).</summary>
        Timeout,

        /// <summary>The <c>escalation</c> and <c>follow-ups</c> calls each wait until the other has started (30 s at most).</summary>
        Together,
    }

    private static readonly TimeSpan EscalationBudget = new ChatOptions().EscalationTimeout;

    /// <summary>The FakeGateway behind a recorder; the <c>escalation</c> call is answered as <paramref name="fault"/> says.</summary>
    private sealed class EscalationGateway(ConcurrentQueue<GatewayCall> calls, EscalationFault fault, FakeTimeProvider time, string? output,
        TaskCompletionSource escalationStarted, TaskCompletionSource followUpsStarted) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = request.Headers.TryGetValues(FakeChatScripts.StepHeader, out var s) ? s.Single() : null;
            var ceiling = request.Headers.TryGetValues(Infrastructure.Gateway.GatewayHeaders.MaxResidency, out var c) ? c.Single() : null;
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            calls.Enqueue(new GatewayCall(request.RequestUri!.AbsolutePath, step, ceiling, body));
            if (fault == EscalationFault.Together && step is Escalation.Step or FollowUps.Step)
            {
                // Each call goes on only once the other one has started: run one after the other, the first would wait here
                // until the 30 s ran out and the test would fail on its missing result.
                (step == Escalation.Step ? escalationStarted : followUpsStarted).TrySetResult();
                await (step == Escalation.Step ? followUpsStarted : escalationStarted).Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return await base.SendAsync(request, cancellationToken);
            }

            if (step != Escalation.Step || fault == EscalationFault.None)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            if (fault == EscalationFault.Timeout)
            {
                time.Advance(EscalationBudget);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }

            var answer = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { message = new { content = output }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 300, completion_tokens = 60 },
                }),
            };
            answer.Headers.Add(Infrastructure.Gateway.GatewayHeaders.Residency, "Eu");
            return answer;
        }
    }

    /// <summary>
    /// The chat host with <c>Settings.Chat.condenseModel</c> (and <paramref name="chatSettings"/>' members), the fixture's assistant
    /// with the contact links <paramref name="notFound"/> and general-knowledge level <paramref name="generalKnowledge"/>, the
    /// recording gateway, extra <paramref name="settings"/>, and the chat service on <paramref name="time"/>.
    /// </summary>
    private WebApplicationFactory<Program> EscalationHost(ConcurrentQueue<GatewayCall> calls, FakeTimeProvider time, CapturingLoggerProvider logs,
        EscalationFault fault = EscalationFault.None, string? output = null, string? notFound = HelpLinks, string generalKnowledge = "off",
        string chatSettings = "", bool condenseModel = true, string embeddingModel = "bge-m3", params (string Key, string Value)[] settings)
    {
        var platform = condenseModel ? $$"""{"condenseModel":"ume/chat-eu","condenseReasoningEffort":"none"{{chatSettings}}}""" : "{}";
        var snapshot = Snapshot(embeddingModel) with { ChatSettings = platform };
        snapshot = WithAssistant(snapshot, a => a with
        {
            Published = a.Published! with { QuerySteps = "{\"answer\":{\"generalKnowledge\":\"" + generalKnowledge + "\"}}", NotFound = notFound ?? "" },
        });
        var escalationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            web.ConfigureLogging(l => l.AddProvider(logs));
            web.ConfigureTestServices(services =>
            {
                services.UseGatewayCredentialCeiling(Kalla.Domain.Residency.External);
                services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
                    new EscalationGateway(calls, fault, time, output, escalationStarted, followUpsStarted) { InnerHandler = db.Gateway.Server.CreateHandler() });
                services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ChatService>(sp, (TimeProvider)time));
            });
        });
    }

    /// <summary>Records <c>reason/summary</c> of <c>rag.chat.escalation</c> from <paramref name="api"/>'s meters only.</summary>
    private static MeterListener EscalationCounter(WebApplicationFactory<Program> api, ConcurrentQueue<string> counted)
    {
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "rag.chat.escalation" && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var named = new Dictionary<string, string?>();
            foreach (var tag in tags)
            {
                named[tag.Key] = tag.Value as string;
            }

            counted.Enqueue($"{named["reason"]}/{named["summary"]}");
        });
        listener.Start();
        return listener;
    }

    private static (string Title, string Url)[] Contacts(JsonElement escalation) =>
        [.. escalation.GetProperty("contacts").EnumerateArray().Select(c => (c.GetProperty("title").GetString()!, c.GetProperty("url").GetString()!))];

    [Fact]
    public async Task A_not_found_turn_gets_the_route_and_a_summary_from_one_call_at_the_condense_ceiling()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var counted = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, EscalationFault.Output,
            "Användaren undrar hur långt det är till månen. Se [källan](https://evil.example/m) [1] eller ring 070-123 45 67.");
        using var listener = EscalationCounter(api, counted);
        using var client = Client(api, db.IntegrationKey);

        var (body, ume) = await KallaAnswerAsync(await AskUme(client, Moon, """{"escalation":true}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("not-found");
        Members(ume).ShouldBe(["outcome", "degraded", "escalation"]);
        var escalation = ume.GetProperty("escalation");
        Members(escalation).ShouldBe(["reason", "contacts", "summary"]);
        escalation.GetProperty("reason").GetString().ShouldBe("not-found");
        Contacts(escalation).ShouldBe([("IT-support, 090-16 10 00", "https://www.umea.se/it")]); // the link's title is the assistant's own
        // The accept rule: the link keeps its text, the marker goes, the phone number is masked.
        escalation.GetProperty("summary").GetString().ShouldBe("Användaren undrar hur långt det är till månen. Se källan eller ring […].");

        var call = calls.Where(c => c.Step == Escalation.Step).ShouldHaveSingleItem();
        call.Ceiling.ShouldBe("Eu");
        (call.Body.GetProperty("model").GetString(), call.Body.GetProperty("max_tokens").GetInt32(), call.Body.GetProperty("stream").GetBoolean())
            .ShouldBe(("ume/chat-eu", Escalation.MaxTokens, false));
        var messages = call.Body.GetProperty("messages");
        messages[0].GetProperty("content").GetString()!.ShouldEndWith("\n" + PromptTexts.Locked.EscalationSwedish);
        var user = messages[1].GetProperty("content").GetString()!;
        user.ShouldBe("Ärende: " + Moon + "\n\nUtfall: Inget svar hittades i källorna."); // no answer text, no search query (not condensed)
        calls.ShouldNotContain(c => c.Step == "chat-answer"); // the not-found text needs no answer call
        body.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBe(0); // the summary's tokens are not the answer's usage
        counted.ShouldBe(["not-found/" + Escalation.Used]);
    }

    [Fact]
    public async Task An_answered_turn_gets_escalation_null_and_no_call()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"escalation":true}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("answered");
        ume.GetProperty("escalation").ValueKind.ShouldBe(JsonValueKind.Null);
        calls.ShouldNotContain(c => c.Step == Escalation.Step);

        // Not asked (false or absent): no member at all.
        var (_, without) = await KallaAnswerAsync(await AskUme(client, Moon, """{"escalation":false}"""));
        Members(without).ShouldBe(["outcome", "degraded"]);
        calls.ShouldNotContain(c => c.Step == Escalation.Step);
    }

    [Fact]
    public async Task A_general_knowledge_stream_writes_follow_ups_and_escalation_in_one_second_chunk_from_calls_run_together()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, EscalationFault.Together, generalKnowledge: "fallback");
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, Moon, """{"follow_ups":2,"escalation":true}""", stream: true, usage: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var events = Events(await response.Content.ReadAsStringAsync(Ct));
        var final = events.FindIndex(e => FinishReason(e) == "stop");
        var umeChunks = events.Select((e, i) => (e, i)).Where(x => x.e.TryGetProperty("ume", out _)).ToList();
        umeChunks.Select(x => x.i).ShouldBe([final + 1, final + 2]);
        events[final + 3].TryGetProperty("usage", out _).ShouldBeTrue();
        umeChunks[0].e.GetProperty("ume").GetProperty("outcome").GetString().ShouldBe("answered-general");
        Members(umeChunks[0].e.GetProperty("ume")).ShouldBe(["outcome", "degraded"]);
        var later = umeChunks[1].e.GetProperty("ume");
        Members(later).ShouldBe(["follow_ups", "escalation"]);
        FollowUpList(later).Length.ShouldBe(2);
        var escalation = later.GetProperty("escalation");
        escalation.GetProperty("reason").GetString().ShouldBe("answered-general");
        // The FakeGateway's default summary: the ticket's first words.
        escalation.GetProperty("summary").GetString().ShouldBe("Användaren behöver hjälp med: Hur långt är det till månen i kilometer.");
        calls.Single(c => c.Step == Escalation.Step).Body.GetProperty("messages")[1].GetProperty("content").GetString()!
            .ShouldEndWith("Utfall: Besvarat med allmän kunskap, utan källor.");
    }

    [Theory]
    [InlineData("timeout-zero")]
    [InlineData("no-condense-model")]
    public async Task A_skipped_summary_makes_no_call_and_the_route_comes_in_the_only_ume_chunk(string kind)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var counted = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        (string, string)[] settings = kind == "timeout-zero" ? [("Chat:EscalationTimeout", "00:00:00")] : [];
        await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, condenseModel: kind != "no-condense-model", settings: settings);
        using var listener = EscalationCounter(api, counted);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, Moon, """{"escalation":true}""", stream: true, usage: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var ume = Events(await response.Content.ReadAsStringAsync(Ct)).Where(e => e.TryGetProperty("ume", out _)).ShouldHaveSingleItem().GetProperty("ume");
        var escalation = ume.GetProperty("escalation");
        escalation.GetProperty("reason").GetString().ShouldBe("not-found");
        Contacts(escalation).Length.ShouldBe(1);
        escalation.GetProperty("summary").ValueKind.ShouldBe(JsonValueKind.Null);
        calls.ShouldNotContain(c => c.Step == Escalation.Step);
        counted.ShouldBe(["not-found/" + Escalation.Skipped]);
    }

    [Fact]
    public async Task Without_the_assistants_links_the_platforms_are_the_contacts_and_without_either_there_are_none()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using (var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, notFound: """{"sv":"Inget svar."}""",
            chatSettings: ""","notFound":{"links":[{"title":"Kontaktcenter","url":"https://www.umea.se/kontakt"}]}"""))
        {
            using var client = Client(api, db.IntegrationKey);
            var (_, ume) = await KallaAnswerAsync(await AskUme(client, Moon, """{"escalation":true}"""));
            Contacts(ume.GetProperty("escalation")).ShouldBe([("Kontaktcenter", "https://www.umea.se/kontakt")]);
        }

        await using (var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, notFound: null))
        {
            using var client = Client(api, db.IntegrationKey);
            var (_, ume) = await KallaAnswerAsync(await AskUme(client, Moon, """{"escalation":true}"""));
            Contacts(ume.GetProperty("escalation")).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task An_unavailable_turn_ends_with_the_same_links_as_its_contacts_the_assistants_else_the_platforms()
    {
        // The embedding answer is discarded (residency above the profile's ceiling): keyword only; a question no keyword matches
        // has no results, so the turn is unavailable (§10.2).
        const string question = "Kvantgravitation xyzzy plutonium?";
        const string platformLinks = ""","notFound":{"links":[{"title":"Kontaktcenter","url":"https://www.umea.se/kontakt"}]}""";
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        (string? NotFound, string Title, string Url)[] cases =
        [
            ("""{"sv":"Inget svar."}""", "Kontaktcenter", "https://www.umea.se/kontakt"), // no links of its own: the platform's
            (HelpLinks, "IT-support, 090-16 10 00", "https://www.umea.se/it"), // its own links win
        ];
        foreach (var (notFound, title, url) in cases)
        {
            await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, notFound: notFound,
                chatSettings: platformLinks, embeddingModel: "fake-wrong-residency-embed");
            using var client = Client(api, db.IntegrationKey);

            var (body, ume) = await KallaAnswerAsync(await AskUme(client, question, """{"escalation":true}"""));
            ume.GetProperty("outcome").GetString().ShouldBe("unavailable");
            var escalation = ume.GetProperty("escalation");
            escalation.GetProperty("reason").GetString().ShouldBe("unavailable");
            Contacts(escalation).ShouldBe([(title, url)]);
            body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                .ShouldBe(FixedTexts.UnavailableSv + $"\n\n- [{title}]({url})");
        }
    }

    [Fact]
    public async Task A_timed_out_summary_is_null_and_the_json_answer_waits_no_longer_than_the_budget()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var counted = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, EscalationFault.Timeout);
        using var listener = EscalationCounter(api, counted);
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, Moon, """{"escalation":true}"""));
        var escalation = ume.GetProperty("escalation");
        escalation.GetProperty("summary").ValueKind.ShouldBe(JsonValueKind.Null);
        Contacts(escalation).Length.ShouldBe(1); // the route still comes
        counted.ShouldBe(["not-found/timeout"]);
    }

    [Fact]
    public async Task The_summary_never_reaches_the_logs()
    {
        // The telemetry scan (§17, §18.9) for a marker only the escalation call's output carries.
        const string Marker = "zebrafinkmanen";
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = EscalationHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, EscalationFault.Output,
            "Användaren frågar om avståndet till " + Marker + " och vill ha hjälp.");
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, Moon, """{"escalation":true}"""));
        ume.GetProperty("escalation").GetProperty("summary").GetString()!.ShouldContain(Marker);
        logs.Text.ShouldNotContain(Marker);
    }
}
