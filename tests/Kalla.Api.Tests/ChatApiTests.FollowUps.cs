extern alias fakegateway;

using System.Collections.Concurrent;
using System.Diagnostics;
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
using Kalla.Answering.Chat;
using Kalla.Domain.Access;
using Kalla.TestKit;
using FakeChatScripts = fakegateway::Kalla.FakeGateway.FakeChatScripts;

namespace Kalla.Api.Tests;

/// <summary>
/// Follow-up suggestions through the chat role (chat design §3.8, §9.8, §17, §18.6, §18.9; M-8) on the knowledge fixture: one
/// <c>follow-ups</c> call after a model answer at the condense ceiling, its output through the accept rule, the second
/// <c>ume</c> chunk before usage and <c>[DONE]</c>, the budget on fake time, the skips without a call, the failures as
/// <c>[]</c>, and nothing of the suggestions in logs, traces, metrics or the stored turn. The FakeGateway answers through a
/// recording handler that can fail, hold or script the <c>follow-ups</c> call.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>How the recording gateway answers the <c>follow-ups</c> call.</summary>
    public enum FollowUpFault
    {
        /// <summary>The FakeGateway's default answer.</summary>
        None,

        /// <summary>The budget runs out while the gateway thinks (the handler advances the fake clock).</summary>
        Timeout,

        /// <summary>The call hangs until the test advances the fake clock.</summary>
        Hang,
        Error,

        /// <summary>200 with a body that has no <c>choices</c>: still only an empty list, never a failed turn.</summary>
        Malformed,
        Residency,

        /// <summary>The scripted output, at residency <c>Eu</c>.</summary>
        Output,
    }

    private static readonly TimeSpan FollowUpBudget = new ChatOptions().FollowUpTimeout;

    /// <summary>The FakeGateway behind a recorder; the <c>follow-ups</c> call is answered as <paramref name="fault"/> says.</summary>
    private sealed class FollowUpGateway(ConcurrentQueue<GatewayCall> calls, FollowUpFault fault, FakeTimeProvider time, string? output, TaskCompletionSource started)
        : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = request.Headers.TryGetValues(FakeChatScripts.StepHeader, out var s) ? s.Single() : null;
            var ceiling = request.Headers.TryGetValues(Infrastructure.Gateway.GatewayHeaders.MaxResidency, out var c) ? c.Single() : null;
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            calls.Enqueue(new GatewayCall(request.RequestUri!.AbsolutePath, step, ceiling, body));
            if (step != FollowUps.Step || fault == FollowUpFault.None)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            switch (fault)
            {
                case FollowUpFault.Timeout:
                    time.Advance(FollowUpBudget);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                case FollowUpFault.Hang:
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                case FollowUpFault.Error:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("""{"error":{"code":"provider_error","message":"Upstream failed."}}""", Encoding.UTF8, "application/json"),
                    };
                case FollowUpFault.Malformed:
                    var malformed = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                    malformed.Headers.Add(Infrastructure.Gateway.GatewayHeaders.Residency, "Eu");
                    return malformed;
                default:
                    var answer = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            choices = new[] { new { message = new { content = output ?? "Hur söker jag bygglov?" }, finish_reason = "stop" } },
                            usage = new { prompt_tokens = 700, completion_tokens = 30 },
                        }),
                    };
                    answer.Headers.Add(Infrastructure.Gateway.GatewayHeaders.Residency, fault == FollowUpFault.Residency ? "External" : "Eu");
                    return answer;
            }
        }
    }

    /// <summary>
    /// The chat host with <c>Settings.Chat.condenseModel</c>, the snapshot as <paramref name="change"/> leaves it, the recording
    /// gateway for chat calls, extra <paramref name="settings"/>, and the chat service on <paramref name="time"/>.
    /// </summary>
    private WebApplicationFactory<Program> FollowUpHost(ConcurrentQueue<GatewayCall> calls, FakeTimeProvider time, CapturingLoggerProvider logs,
        FollowUpFault fault = FollowUpFault.None, string? output = null, TaskCompletionSource? started = null, Func<PolicySnapshot, PolicySnapshot>? change = null,
        Kalla.Domain.Residency ceiling = Kalla.Domain.Residency.External, params (string Key, string Value)[] settings)
    {
        var snapshot = Snapshot() with { ChatSettings = """{"condenseModel":"fors/chat-eu","condenseReasoningEffort":"none"}""" };
        snapshot = change?.Invoke(snapshot) ?? snapshot;
        var signal = started ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                services.UseGatewayCredentialCeiling(ceiling);
                services.AddHttpClient(GatewayChatClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FollowUpGateway(calls, fault, time, output, signal) { InnerHandler = db.Gateway.Server.CreateHandler() });
                services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ChatService>(sp, (TimeProvider)time));
            });
        });
    }

    /// <summary>The fixture's assistant changed by <paramref name="change"/>.</summary>
    private PolicySnapshot WithAssistant(PolicySnapshot snapshot, Func<AssistantEntry, AssistantEntry> change) => snapshot with
    {
        Assistants = new Dictionary<Guid, AssistantEntry> { [db.AssistantId] = change(snapshot.Assistants[db.AssistantId]) },
    };

    /// <summary>Records <c>outcome</c> of <c>rag.chat.follow_ups</c> and <c>direction/step</c> of <c>rag.chat.tokens</c> from <paramref name="api"/>'s meters only.</summary>
    private static MeterListener FollowUpCounter(WebApplicationFactory<Program> api, ConcurrentQueue<string> outcomes, ConcurrentQueue<string> tokens)
    {
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name is "rag.chat.follow_ups" or "rag.chat.tokens" && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var named = new Dictionary<string, string?>();
            foreach (var tag in tags)
            {
                named[tag.Key] = tag.Value as string;
            }

            if (instrument.Name == "rag.chat.follow_ups")
            {
                outcomes.Enqueue(named["outcome"]!);
            }
            else
            {
                tokens.Enqueue($"{named["direction"]}/{named["step"]}/{value}");
            }
        });
        listener.Start();
        return listener;
    }

    private static string[] FollowUpList(JsonElement ume) => [.. ume.GetProperty("follow_ups").EnumerateArray().Select(f => f.GetString()!)];

    private static List<JsonElement> Events(string body)
    {
        var lines = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        lines[^1].ShouldBe("data: [DONE]");
        return [.. lines.SkipLast(1).Select(l => JsonDocument.Parse(l["data: ".Length..]).RootElement.Clone())];
    }

    [Fact]
    public async Task After_an_answer_one_follow_up_call_at_the_condense_ceiling_gives_the_accepted_suggestions()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var outcomes = new ConcurrentQueue<string>();
        var tokens = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var listener = FollowUpCounter(api, outcomes, tokens);
        using var client = Client(api, db.IntegrationKey);

        using var plain = await Ask(client, AltanQuestion);
        var plainUsage = (await plain.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("usage").GetRawText();
        calls.ShouldNotContain(c => c.Step == FollowUps.Step); // not asked: no call
        calls.Clear();

        var (body, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"related_documents":2,"follow_ups":3}"""));
        Members(ume).ShouldBe(["outcome", "degraded", "related_documents", "follow_ups"]);
        var call = calls.Where(c => c.Step == FollowUps.Step).ShouldHaveSingleItem();
        calls.Select(c => c.Step).Where(s => s is not null).ShouldBe(["chat-answer", FollowUps.Step]); // after the answer
        call.Ceiling.ShouldBe("Eu");
        (call.Body.GetProperty("model").GetString(), call.Body.GetProperty("max_tokens").GetInt32(), call.Body.GetProperty("reasoning_effort").GetString(),
            call.Body.GetProperty("stream").GetBoolean()).ShouldBe(("fors/chat-eu", 3 * FollowUps.TokensPerQuestion, "none", false));
        call.Body.TryGetProperty("temperature", out _).ShouldBeFalse();
        var messages = call.Body.GetProperty("messages");
        messages[0].GetProperty("content").GetString()!.ShouldStartWith("Föreslå högst 3 korta följdfrågor");
        messages[0].GetProperty("content").GetString()!.ShouldEndWith("\nSkriv frågorna på svenska.");
        var user = messages[1].GetProperty("content").GetString()!;
        user.ShouldStartWith("Fråga: " + AltanQuestion + "\n\nSvar: ");
        user.ShouldContain("\n\nKällor:\n");
        var answered = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        answered.ShouldContain("[1]");
        user.ShouldNotContain("[1]"); // markers removed from the answer
        var titles = user[(user.IndexOf("\n\nKällor:\n", StringComparison.Ordinal) + "\n\nKällor:\n".Length)..].Split('\n');
        titles.ShouldContain("Altan och uteplats");

        // The FakeGateway's default answer through the accept rule: the first three lines.
        FollowUpList(ume).ShouldBe([.. FakeChatScripts.DefaultFollowUps(user).Split('\n').Take(3)]);
        body.GetProperty("usage").GetRawText().ShouldBe(plainUsage); // the answer call's only
        outcomes.ShouldBe([FollowUps.Used]);
        tokens.Where(t => t.Contains("/follow-ups/", StringComparison.Ordinal)).Select(t => t[..t.LastIndexOf('/')]).ShouldBe(
            ["input/follow-ups", "cached-input/follow-ups", "output/follow-ups"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_external_answer_gets_its_follow_ups_at_eu()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs,
            change: s => WithAssistant(s, a => a with { Classification = Domain.Classification.Public }));
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"follow_ups":2}"""));
        FollowUpList(ume).Length.ShouldBe(2);
        calls.Single(c => c.Step == "chat-answer").Ceiling.ShouldBe("External");
        calls.Single(c => c.Step == FollowUps.Step).Ceiling.ShouldBe("Eu"); // the condense ceiling, never wider
    }

    [Fact]
    public async Task The_stream_writes_the_follow_ups_in_a_second_ume_chunk_before_usage_and_done()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var plain = await StreamAsync(client, AltanQuestion, usage: true);
        using var response = await AskUme(client, AltanQuestion, """{"related_documents":3,"follow_ups":2}""", stream: true, usage: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var events = Events(await response.Content.ReadAsStringAsync(Ct));
        var final = events.FindIndex(e => FinishReason(e) == "stop");
        var umeChunks = events.Select((e, i) => (e, i)).Where(x => x.e.TryGetProperty("ume", out _)).ToList();
        umeChunks.Select(x => x.i).ShouldBe([final + 1, final + 2]);
        Members(umeChunks[0].e.GetProperty("ume")).ShouldBe(["outcome", "degraded", "related_documents"]);
        Members(umeChunks[1].e.GetProperty("ume")).ShouldBe(["follow_ups"]);
        FollowUpList(umeChunks[1].e.GetProperty("ume")).Length.ShouldBe(2);
        (umeChunks[1].e.GetProperty("object").GetString(), umeChunks[1].e.GetProperty("choices").GetArrayLength(), umeChunks[1].e.GetProperty("id").GetString())
            .ShouldBe(("chat.completion.chunk", 0, events[0].GetProperty("id").GetString()));
        events[final + 3].GetProperty("usage").GetRawText().ShouldBe(plain[^1].GetProperty("usage").GetRawText()); // the answer call's only
        events.Count.ShouldBe(final + 4);
    }

    [Fact]
    public async Task A_follow_up_call_that_outlasts_its_budget_ends_the_stream_with_none_once_the_budget_has_run_out()
    {
        // The first ume chunk is out before the call returns; [DONE] follows when the fake clock passes the budget, no wall-clock wait.
        var calls = new ConcurrentQueue<GatewayCall>();
        var outcomes = new ConcurrentQueue<string>();
        var tokens = new ConcurrentQueue<string>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, time, logs, FollowUpFault.Hang, started: started);
        using var listener = FollowUpCounter(api, outcomes, tokens);
        using var client = Client(api, db.IntegrationKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "bygg", stream = true, messages = new[] { new { role = "user", content = AltanQuestion } },
                ume = JsonDocument.Parse("""{"related_documents":2,"follow_ups":3}""").RootElement.Clone(),
            }),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        var first = new List<string>();
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            if (line.Length > 0)
            {
                first.Add(line);
            }

            if (line.Contains("\"ume\"", StringComparison.Ordinal))
            {
                break;
            }
        }

        var firstUme = JsonDocument.Parse(first[^1]["data: ".Length..]).RootElement.GetProperty("ume");
        Members(firstUme).ShouldBe(["outcome", "degraded", "related_documents"]);
        await started.Task.WaitAsync(Ct); // the follow-up call is out and waiting
        var start = time.GetUtcNow();
        time.Advance(FollowUpBudget);

        var rest = Events(await reader.ReadToEndAsync(Ct));
        var second = rest.ShouldHaveSingleItem().GetProperty("ume");
        Members(second).ShouldBe(["follow_ups"]);
        FollowUpList(second).ShouldBeEmpty();
        (time.GetUtcNow() - start).ShouldBe(FollowUpBudget);
        outcomes.ShouldBe(["timeout"]);
        tokens.ShouldNotContain(t => t.Contains("/follow-ups/", StringComparison.Ordinal));
    }

    public static TheoryData<FollowUpFault, string?, string> FollowUpFailures { get; } = new()
    {
        { FollowUpFault.Timeout, null, "timeout" },
        { FollowUpFault.Error, null, "error" },
        { FollowUpFault.Malformed, null, "error" },
        { FollowUpFault.Residency, null, "residency" },
        { FollowUpFault.Output, "1. Vad gäller enligt [1]?\n- Se https://www.umea.se/bygglov?\nJag kan inte hjälpa till med det.\n" + AltanQuestion, "empty" },
    };

    [Theory]
    [MemberData(nameof(FollowUpFailures))]
    public async Task A_failed_or_empty_follow_up_call_gives_none_and_the_answer_stands(FollowUpFault fault, string? output, string outcome)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var outcomes = new ConcurrentQueue<string>();
        var tokens = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, fault, output);
        using var listener = FollowUpCounter(api, outcomes, tokens);
        using var client = Client(api, db.IntegrationKey);

        var (body, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"follow_ups":3}"""));
        body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.ShouldStartWith("Testsvar på");
        FollowUpList(ume).ShouldBeEmpty();
        calls.Count(c => c.Step == FollowUps.Step).ShouldBe(1); // never retried
        outcomes.ShouldBe([outcome]);
        if (fault == FollowUpFault.Residency)
        {
            logs.Entries.ShouldContain(e => e.Category == Infrastructure.Security.SecurityEvents.Category && e.EventId.Id == 5003);
        }
    }

    [Fact]
    public async Task Lines_with_a_personal_identity_number_an_address_or_a_link_are_dropped()
    {
        // §18.6: the accept rule on the model's real output, whatever the prompt said.
        const string Output = "Gäller det för 19800101-1234?\nKan jag mejla anna.andersson@umea.se?\nRing 090-16 10 00?\nLäs www.umea.se/bygglov?\n" +
            "Hur söker jag bygglov för ett plank?";
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, FollowUpFault.Output, Output);
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"follow_ups":5}"""));
        FollowUpList(ume).ShouldBe(["Hur söker jag bygglov för ett plank?"]);
    }

    [Theory]
    [InlineData("timeout-zero")]
    [InlineData("not-found")]
    [InlineData("conversational")]
    [InlineData("on-prem")]
    public async Task Skipped_turns_make_no_follow_up_call_and_carry_none_in_their_only_ume_chunk(string kind)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        var outcomes = new ConcurrentQueue<string>();
        var tokens = new ConcurrentQueue<string>();
        using var logs = new CapturingLoggerProvider();
        Func<PolicySnapshot, PolicySnapshot>? change = null;
        (string Key, string Value)[] settings = [];
        var ceiling = Kalla.Domain.Residency.External;
        if (kind == "timeout-zero")
        {
            settings = [("Chat:FollowUpTimeout", "00:00:00")];
        }
        else if (kind == "on-prem")
        {
            // An OnPrem turn: the deployment's ceiling is OnPrem and the assistant's model runs there (no condense model until M2).
            change = s => WithAssistant(s, a => a with { Published = a.Published! with { GatewayModel = "fors/chat-onprem" } });
            ceiling = Kalla.Domain.Residency.OnPrem; // the credential key allows OnPrem only (M-17 C9)
        }

        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, change: change, ceiling: ceiling, settings: settings);
        using var listener = FollowUpCounter(api, outcomes, tokens);
        using var client = Client(api, db.IntegrationKey);

        var question = kind switch { "not-found" => "Hur långt är det till månen i kilometer?", "conversational" => "Hej!", _ => AltanQuestion };
        using var response = await AskUme(client, question, """{"follow_ups":3}""", stream: true, usage: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var events = Events(await response.Content.ReadAsStringAsync(Ct));
        var ume = events.Where(e => e.TryGetProperty("ume", out _)).ShouldHaveSingleItem().GetProperty("ume");
        Members(ume).ShouldBe(["outcome", "degraded", "follow_ups"]);
        FollowUpList(ume).ShouldBeEmpty();
        calls.ShouldNotContain(c => c.Step == FollowUps.Step);
        outcomes.ShouldBe([FollowUps.Skipped]);
        if (kind == "on-prem")
        {
            calls.Single(c => c.Step == "chat-answer").Ceiling.ShouldBe("OnPrem");
        }
    }

    [Fact]
    public async Task A_general_knowledge_answer_gets_follow_ups_from_its_own_text_without_caveat_links_or_sources()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, change: s => WithAssistant(s, a => a with
        {
            Published = a.Published! with
            {
                QuerySteps = """{"answer":{"generalKnowledge":"open"}}""",
                NotFound = """{"sv":"Inget svar i källorna.","links":[{"title":"Kontaktcenter","url":"https://www.umea.se/kontakt"}]}""",
            },
        }));
        using var client = Client(api, db.IntegrationKey);

        const string Moon = "Hur långt är det till månen i kilometer?";
        var (body, ume) = await KallaAnswerAsync(await AskUme(client, Moon, """{"follow_ups":2}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("answered-general");
        var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        content.ShouldStartWith(FixedTexts.GeneralCaveatSv);
        content.ShouldEndWith(ContactLinks);
        var user = calls.Single(c => c.Step == FollowUps.Step).Body.GetProperty("messages")[1].GetProperty("content").GetString()!;
        var answer = content[(FixedTexts.GeneralCaveatSv.Length + 2)..^ContactLinks.Length];
        user.ShouldBe("Fråga: " + Moon + "\n\nSvar: " + answer.Trim()); // no caveat, no contact links, no Källor (no documents in the prompt)
        FollowUpList(ume).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Suggestions_never_reach_logs_traces_metrics_or_the_stored_turn()
    {
        // The telemetry scan (§17, §18.9) for a marker that only the follow-up call's output carries.
        const string Marker = "zebrafinkaltan";
        const string Output = "Hur bygger jag en " + Marker + "?\nVad kostar en " + Marker + " i Umeå?";
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, FollowUpFault.Output, Output);
        using var client = Client(api, db.IntegrationKey);

        // Spans of this test's requests only: each runs under its own trace, given as traceparent (#431).
        var traces = new ConcurrentDictionary<ActivityTraceId, bool>();
        var spans = new ConcurrentQueue<string>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                traces.ContainsKey(options.TraceId) ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (traces.ContainsKey(activity.TraceId))
                {
                    spans.Enqueue(string.Join('|', new[] { activity.DisplayName, activity.OperationName, activity.StatusDescription ?? "" }
                        .Concat(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"))
                        .Concat(activity.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}").Append(e.Name)))
                        .Concat(activity.Baggage.Select(b => $"{b.Key}={b.Value}"))));
                }
            },
        };
        ActivitySource.AddActivityListener(activities);

        // Every measurement of this host's meters, with its tags.
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        var measured = new ConcurrentQueue<string>();
        using var instruments = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = new StringBuilder(instrument.Name).Append(' ').Append(value);
            foreach (var tag in tags)
            {
                text.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
            }

            measured.Enqueue(text.ToString());
        }

        instruments.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        instruments.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        instruments.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        instruments.Start();

        var ids = new List<string>();
        foreach (var stream in new[] { false, true })
        {
            var trace = ActivityTraceId.CreateRandom();
            traces[trace] = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = "bygg", stream, messages = new[] { new { role = "user", content = AltanQuestion } },
                    ume = JsonDocument.Parse("""{"related_documents":2,"follow_ups":2}""").RootElement.Clone(),
                }),
            };
            request.Headers.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
            using var response = await client.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            text.ShouldContain(Marker); // delivered to the caller
            ids.Add((stream ? Events(text)[0] : JsonDocument.Parse(text).RootElement).GetProperty("id").GetString()!);
        }

        // Nothing stored: the turns are written, without the suggestions (history mode none keeps no text at all).
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        foreach (var id in ids)
        {
            await using var command = new Npgsql.NpgsqlCommand("""SELECT row_to_json(m)::text FROM chat."Messages" m WHERE "Id" = $1""", connection)
            {
                Parameters = { new() { Value = Guid.ParseExact(id["chatcmpl-".Length..], "N") } },
            };
            var row = (string?)await command.ExecuteScalarAsync(Ct);
            row.ShouldNotBeNull();
            row.ShouldNotContain(Marker);
        }

        spans.ShouldNotBeEmpty(); // the requests' spans are seen at all
        spans.ShouldAllBe(s => !s.Contains(Marker, StringComparison.OrdinalIgnoreCase));
        measured.ShouldContain(m => m.StartsWith("rag.chat.follow_ups 1 outcome=used", StringComparison.Ordinal));
        measured.ShouldAllBe(m => !m.Contains(Marker, StringComparison.OrdinalIgnoreCase));
        logs.Entries.ShouldNotBeEmpty();
        logs.Text.ShouldNotContain(Marker, Case.Insensitive);
    }
}
