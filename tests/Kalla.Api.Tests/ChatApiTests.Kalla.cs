using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kalla.Answering.Chat;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// The <c>kalla</c> extension through the chat role (chat design §3.8, §9.7, §17, §18.6, §18.9; M-7) on the knowledge fixture:
/// refusals and Open WebUI's ignored member, the outcome of every turn kind, related documents from the turn's own retrieval
/// (no extra embedding, gateway call or SQL statement), the stream's chunk order and the metrics. The wire itself is pinned by
/// the <c>*-ume-*</c> golden files.
/// </summary>
public sealed partial class ChatApiTests
{
    private const string AltanQuestion = "Behöver jag bygglov för en altan högre än 1,2 meter?";

    private static Task<HttpResponseMessage> AskUme(HttpClient client, string question, string kalla, bool stream = false, bool usage = false) =>
        client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "bygg", stream, stream_options = usage ? new { include_usage = true } : null, messages = new[] { new { role = "user", content = question } },
            kalla = JsonDocument.Parse(kalla).RootElement.Clone(),
        }, Ct);

    /// <summary>The JSON answer's <c>kalla</c> member (it must be there).</summary>
    private static async Task<(JsonElement Body, JsonElement Kalla)> KallaAnswerAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body, body.GetProperty("kalla"));
    }

    private static string[] Members(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [InlineData("\"x\"", "invalid_request", "kalla")]
    [InlineData("""{"related_documents":11}""", "invalid_request", "kalla.related_documents")]
    [InlineData("""{"follow_ups":6}""", "invalid_request", "kalla.follow_ups")]
    [InlineData("""{"general_knowledge":"loose"}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"foo":1}""", "unsupported_parameter", "kalla.foo")]
    public async Task An_invalid_ume_from_an_integration_client_is_refused_before_any_model_call(string kalla, string code, string param)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, AltanQuestion, kalla);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("type").GetString(), error.GetProperty("param").GetString())
            .ShouldBe((code, "invalid_request_error", param));
        calls.ShouldBeEmpty(); // §3.8: refused with the rest of the body, before any embedding or model call
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("""{"foo":1,"related_documents":99}""")]
    [InlineData("""{"related_documents":5}""")]
    [InlineData("""{"general_knowledge":"loose"}""")]
    [InlineData("""{"topics":[1]}""")]
    [InlineData("""{"topics":["okänt"]}""")]
    public async Task Open_webui_requests_ignore_ume_without_validating_it(string kalla)
    {
        await using var api = WireHost(null, openWebUi: true, Upstream.Fake);
        using var client = Client(api, db.IntegrationKey);
        AsOpenWebUiUser(client);

        using var response = await AskUme(client, AltanQuestion, kalla);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("kalla", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_ume_or_with_null_the_answer_has_no_ume_member()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        using (var absent = await Ask(client, AltanQuestion))
        {
            (await absent.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("kalla", out _).ShouldBeFalse();
        }

        using var none = await AskUme(client, AltanQuestion, "null");
        none.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await none.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("kalla", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_ume_gives_the_outcome_and_degraded_only()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, "{}"));
        Members(kalla).ShouldBe(["outcome", "degraded"]);
        (kalla.GetProperty("outcome").GetString(), kalla.GetProperty("degraded").GetBoolean()).ShouldBe(("answered", false));
    }

    [Fact]
    public async Task Related_documents_list_each_retrieved_document_once_and_match_the_annotations()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var (body, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"related_documents":10}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("answered");
        var related = kalla.GetProperty("related_documents").EnumerateArray().ToList();
        related.Select(d => d.GetProperty("document_id").GetGuid()).ShouldBeUnique();
        related.Select(d => d.GetProperty("rank").GetInt32()).ShouldBe(Enumerable.Range(1, related.Count));

        // The crawled page links by its own URL; the uploaded PDF by its download grant.
        var altan = related.Single(d => d.GetProperty("document_id").GetGuid() == db.Altan.Id);
        (altan.GetProperty("title").GetString(), altan.GetProperty("url").GetString()).ShouldBe(("Altan och uteplats", "https://www.umea.se/bygga/altan"));
        var upload = related.Single(d => d.GetProperty("document_id").GetGuid() == db.Upload.Id);
        upload.GetProperty("title").GetString().ShouldBe("taxa-bygglov-2026.pdf");
        upload.GetProperty("url").GetString()!.ShouldStartWith($"/v1/documents/{db.Upload.Id}/content?grant=g1.");

        // cited and number agree with the answer's markers and annotations.
        var message = body.GetProperty("choices")[0].GetProperty("message");
        var content = message.GetProperty("content").GetString()!;
        var annotations = message.GetProperty("annotations").EnumerateArray().Select(a => a.GetProperty("url_citation")).ToList();
        annotations.ShouldNotBeEmpty();
        foreach (var annotation in annotations)
        {
            var marker = content[annotation.GetProperty("start_index").GetInt32()..annotation.GetProperty("end_index").GetInt32()];
            var entry = related.Single(d => d.GetProperty("url").GetString() == annotation.GetProperty("url").GetString());
            entry.GetProperty("cited").GetBoolean().ShouldBeTrue();
            marker.ShouldBe($"[{entry.GetProperty("number").GetInt32()}]");
        }

        foreach (var uncited in related.Where(d => !d.GetProperty("cited").GetBoolean()))
        {
            uncited.TryGetProperty("number", out _).ShouldBeFalse();
            annotations.ShouldNotContain(a => a.GetProperty("url").GetString() == uncited.GetProperty("url").GetString());
        }

        // Titles and URLs never in the logs (§9.7, §17).
        logs.Text.ShouldNotContain("taxa-bygglov-2026.pdf");
        logs.Text.ShouldNotContain("grant=g1.");

        // N limits the list to the best-ranked documents.
        var (_, one) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"related_documents":1}"""));
        one.GetProperty("related_documents").EnumerateArray().ShouldHaveSingleItem().GetProperty("document_id").GetGuid()
            .ShouldBe(related[0].GetProperty("document_id").GetGuid());
    }

    [Theory]
    [InlineData("off", "not-found")]
    [InlineData("fallback", "answered-general")]
    public async Task Low_coverage_lists_nothing(string level, string outcome)
    {
        await using var api = GeneralHost(level);
        using var client = Client(api, db.IntegrationKey);
        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, "Hur långt är det till månen i kilometer?", """{"related_documents":5}"""));
        (kalla.GetProperty("outcome").GetString(), kalla.GetProperty("degraded").GetBoolean()).ShouldBe((outcome, false));
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("fallback", "{}", "answered-general")]
    [InlineData("fallback", """{"general_knowledge":null}""", "answered-general")]
    [InlineData("fallback", """{"general_knowledge":"open"}""", "answered-general")]
    [InlineData("fallback", """{"general_knowledge":"supplement"}""", "not-found")]
    [InlineData("fallback", """{"general_knowledge":"off"}""", "not-found")]
    [InlineData("off", """{"general_knowledge":"fallback"}""", "not-found")]
    [InlineData("off", """{"general_knowledge":"open"}""", "not-found")]
    public async Task A_requested_general_knowledge_level_only_tightens_the_assistants(string level, string kalla, string outcome)
    {
        // #726 (§3.8): the turn runs at the stricter of kalla.general_knowledge and the assistant's level; absent or null is the assistant's.
        await using var api = GeneralHost(level);
        using var client = Client(api, db.IntegrationKey);
        var before = await WrittenAsync(api, null);

        var (body, answer) = await KallaAnswerAsync(await AskUme(client, "Hur långt är det till månen i kilometer?", kalla));
        answer.GetProperty("outcome").GetString().ShouldBe(outcome);
        var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        if (outcome == "not-found")
        {
            content.ShouldBe("Inget svar i källorna." + ContactLinks);
            body.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBe(0); // no model call
        }
        else
        {
            content.ShouldStartWith(FixedTexts.GeneralCaveatSv + "\n\n");
        }

        var after = await WrittenAsync(api, body.GetProperty("id").GetString());
        var general = outcome == "answered-general" ? 1L : 0L;
        (after.Outcome, after.NotFound - before.NotFound, after.General - before.General).ShouldBe((outcome, 1L - general, general));
    }

    [Fact]
    public async Task The_frame_is_the_effective_levels()
    {
        // #726 (§7.3): with an open assistant, the answer call's frame is the level the request tightened it to.
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, """{"answer":{"generalKnowledge":"open"}}""");
        using var client = Client(api, db.IntegrationKey);
        var name = Snapshot().Assistants[db.AssistantId].Name;

        async Task<string> SystemAsync(string question, string asked)
        {
            calls.Clear();
            using var response = await AskUme(client, question, $$"""{"general_knowledge":"{{asked}}"}""");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return calls.Single(c => c.Step == GatewayChatClient.AnswerStep).Body.GetProperty("messages")[0].GetProperty("content").GetString()!;
        }

        foreach (var level in new[] { GeneralKnowledge.Open, GeneralKnowledge.Fallback, GeneralKnowledge.Supplement, GeneralKnowledge.Off })
        {
            (await SystemAsync(AltanQuestion, GeneralKnowledgeLevels.ToValue(level))).ShouldStartWith(PromptBuilder.Frame(name, PromptKind.Sources, level, false, false));
        }

        // Without sources (coverage Low): fallback's general frame, not open's.
        (await SystemAsync("Hur långt är det till månen i kilometer?", GeneralKnowledgeLevels.Fallback))
            .ShouldStartWith(PromptBuilder.Frame(name, PromptKind.General, GeneralKnowledge.Fallback, false, false));
    }

    [Fact]
    public async Task Search_unavailable_lists_nothing()
    {
        await using var api = GeneralHost("off", embeddingModel: "fake-wrong-residency-embed");
        using var client = Client(api, db.IntegrationKey);
        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, "Kvantgravitation xyzzy plutonium?", """{"related_documents":5}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("unavailable");
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_degraded_answer_says_so_and_lists_its_keyword_results()
    {
        await using var api = GeneralHost("off", embeddingModel: "fake-wrong-residency-embed");
        using var client = Client(api, db.IntegrationKey);
        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"related_documents":5}"""));
        (kalla.GetProperty("outcome").GetString(), kalla.GetProperty("degraded").GetBoolean()).ShouldBe(("answered", true));
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_conversational_turn_is_answered_and_lists_nothing()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, "Hej!", """{"related_documents":5}"""));
        (kalla.GetProperty("outcome").GetString(), kalla.GetProperty("degraded").GetBoolean()).ShouldBe(("answered", false));
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Low_coverage_after_a_failed_condense_lists_what_the_answer_was_built_from()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.Error, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, Long("Hur långt är det till månen i kilometer räknat från Umeå?", 2400), """{"related_documents":5}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("answered");
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_stream_writes_one_ume_chunk_after_the_final_chunk_and_before_usage_and_done()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        // Without a condense model, follow-ups asked for are skipped before any call: [] in the first and only kalla chunk (§3.8, §9.8).
        using var response = await AskUme(client, AltanQuestion, """{"related_documents":3,"follow_ups":2}""", stream: true, usage: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        lines[^1].ShouldBe("data: [DONE]");
        var events = lines.SkipLast(1).Select(l => JsonDocument.Parse(l["data: ".Length..]).RootElement.Clone()).ToList();
        var final = events.FindIndex(e => FinishReason(e) == "stop");
        var kallaChunks = events.Select((e, i) => (e, i)).Where(x => x.e.TryGetProperty("kalla", out _)).ToList();
        var (chunk, at) = kallaChunks.ShouldHaveSingleItem();
        at.ShouldBe(final + 1);
        events[at + 1].TryGetProperty("usage", out _).ShouldBeTrue();
        at.ShouldBe(events.Count - 2);

        (chunk.GetProperty("object").GetString(), chunk.GetProperty("choices").GetArrayLength(), chunk.GetProperty("id").GetString())
            .ShouldBe(("chat.completion.chunk", 0, events[0].GetProperty("id").GetString()));
        var kalla = chunk.GetProperty("kalla");
        Members(kalla).ShouldBe(["outcome", "degraded", "related_documents", "follow_ups"]);
        kalla.GetProperty("follow_ups").GetArrayLength().ShouldBe(0);
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBeInRange(1, 3);
    }

    [Fact]
    public async Task Related_documents_make_no_extra_embedding_gateway_call_or_sql_statement()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);
        (await AnswerAsync(await AskWith(client, AltanQuestion))).ShouldStartWith("Testsvar på"); // warm: config, embedding cache

        // Only this test's requests count (§16.2 item 2): each runs under its own trace, given as traceparent.
        var statements = new ConcurrentDictionary<ActivityTraceId, ConcurrentQueue<string>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // Statements only: a physical connection opened from the pool is a span without a query.
                if (activity.Source.Name == "Npgsql" && activity.GetTagItem("db.query.text") is string { Length: > 0 } text)
                {
                    statements.GetOrAdd(activity.TraceId, _ => new ConcurrentQueue<string>()).Enqueue(text);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        async Task<(string[] Statements, string[] Calls)> Turn(string? kalla)
        {
            calls.Clear();
            var trace = ActivityTraceId.CreateRandom();
            var extension = kalla is null ? (JsonElement?)null : JsonDocument.Parse(kalla).RootElement.Clone();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = JsonContent.Create(new { model = "bygg", messages = new[] { new { role = "user", content = AltanQuestion } }, kalla = extension }),
            };
            request.Headers.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
            using var response = await client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (statements.TryGetValue(trace, out var seen) ? [.. seen] : [], [.. calls.Select(c => c.Path + " " + c.Step).Order()]);
        }

        // Each kind twice, alternating, and the fewer statements of the two counts: a statement that isn't the turn's own
        // can land in one request's trace (the Data Protection key ring read again after a failed read under load, #1117),
        // but one that related documents add is in both turns with them.
        var turns = new List<(string Kind, string[] Statements, string[] Calls)>();
        foreach (var kalla in new[] { null, """{"related_documents":10}""", null, """{"related_documents":10}""" })
        {
            var (seen, made) = await Turn(kalla);
            turns.Add((kalla is null ? "without" : "with", seen, made));
        }

        var report = string.Join("\n", turns.Select(t => $"{t.Kind}: {t.Statements.Length}\n  " + string.Join("\n  ", t.Statements.Select(q => q.ReplaceLineEndings(" ")[..Math.Min(100, q.Length)]))));
        var without = turns.Where(t => t.Kind == "without").Min(t => t.Statements.Length);
        var with = turns.Where(t => t.Kind == "with").Min(t => t.Statements.Length);
        without.ShouldBeGreaterThan(0, report); // the knowledge role's statements are seen at all
        with.ShouldBe(without, report);
        foreach (var turn in turns)
        {
            turn.Calls.ShouldBe(turns[0].Calls);
        }
    }

    [Fact]
    public async Task The_extension_is_counted_by_members_and_related_documents_by_entries()
    {
        await using var api = Host();
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        var umes = new ConcurrentQueue<string>();
        var entries = new ConcurrentQueue<int>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name is "rag.chat.kalla" or "rag.chat.related_documents" && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? members = null, kind = null;
            foreach (var tag in tags)
            {
                members = tag.Key == "members" ? tag.Value as string : members;
                kind = tag.Key == "client_kind" ? tag.Value as string : kind;
            }

            umes.Enqueue($"{members}/{kind}");
        });
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => entries.Enqueue(value));
        listener.Start();
        using var client = Client(api, db.IntegrationKey);

        foreach (var (kalla, stream) in new[]
                 {
                     ("{}", false), ("""{"related_documents":2}""", true), ("""{"follow_ups":2}""", false), ("""{"related_documents":1,"follow_ups":1}""", false),
                 })
        {
            using var response = await AskUme(client, AltanQuestion, kalla, stream);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await response.Content.ReadAsStringAsync(Ct);
        }

        using (var plain = await Ask(client, AltanQuestion))
        {
            plain.StatusCode.ShouldBe(HttpStatusCode.OK); // without kalla: not counted
        }

        var kind = Domain.Entities.ClientKinds.Integration;
        umes.ShouldBe([$"outcome/{kind}", $"related/{kind}", $"follow_ups/{kind}", $"both/{kind}"]);
        entries.ShouldBe([2, 1]);
    }
}
