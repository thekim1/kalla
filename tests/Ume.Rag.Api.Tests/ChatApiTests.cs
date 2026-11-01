using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The chat role (chat design §3, §4.1, §18.2; P1a item 15a) on the knowledge fixture with the FakeGateway: the model list
/// by the caller's access, a cited answer with url_citation annotations, the not-found text without a model call, and the
/// OpenAI error shape.
/// </summary>
public sealed class ChatApiTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The fixture's snapshot with the integration client in the assistant's audience as well.</summary>
    private PolicySnapshot Snapshot()
    {
        var snapshot = db.Snapshot();
        var assistant = snapshot.Assistants[db.AssistantId];
        return snapshot with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = assistant with
                {
                    Description = "Svarar om bygglov.", ContactName = "Bygglov", ContactEmail = "bygglov@umea.se",
                    Published = assistant.Published! with { SystemPrompt = "Var kortfattad.", NotFound = """{"sv":"Inget svar i källorna."}""" },
                    Audiences = [.. assistant.Audiences, new AudienceEntry(AssistantAudienceKinds.Client, db.IntegrationClient, null)],
                },
            },
        };
    }

    private WebApplicationFactory<Program> Host() => db.ApiHost(Snapshot()).WithWebHostBuilder(web =>
    {
        web.UseSetting("Rag:Roles", "knowledge,chat");
        web.ConfigureTestServices(services =>
            services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
    });

    private static HttpClient Client(WebApplicationFactory<Program> api, string key)
    {
        var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static Task<HttpResponseMessage> Ask(HttpClient client, string question, string model = "bygg") =>
        client.PostAsJsonAsync("/v1/chat/completions", new { model, messages = new[] { new { role = "user", content = question } } }, Ct);

    [Fact]
    public async Task Models_are_the_assistants_the_caller_may_use()
    {
        await using var api = Host();
        using var integration = Client(api, db.IntegrationKey);
        var models = await integration.GetFromJsonAsync<JsonElement>("/v1/models", Ct);
        var model = models.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        (model.GetProperty("id").GetString(), model.GetProperty("name").GetString(), model.GetProperty("owned_by").GetString()).ShouldBe(("bygg", "Bygg", "umea-kommun"));
        model.GetProperty("description").GetString().ShouldBe("Svarar om bygglov. Kontakt: Bygglov, bygglov@umea.se");
        model.GetProperty("info").GetProperty("meta").GetProperty("description").GetString().ShouldBe("Svarar om bygglov. Kontakt: Bygglov, bygglov@umea.se");

        // The internal client is not in the audience: no models, and the chat answers 404 model_not_found.
        using var internalClient = Client(api, db.InternalKey);
        (await internalClient.GetFromJsonAsync<JsonElement>("/v1/models", Ct)).GetProperty("data").GetArrayLength().ShouldBe(0);
        using var denied = await Ask(internalClient, "Behöver jag bygglov för en altan?");
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var error = (await denied.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("type").GetString(), error.GetProperty("param").GetString()).ShouldBe(("model_not_found", "not_found_error", "model"));
    }

    [Fact]
    public async Task A_question_is_answered_from_the_sources_with_renumbered_citations()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        using var response = await Ask(client, "Behöver jag bygglov för en altan högre än 1,2 meter?");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("object").GetString().ShouldBe("chat.completion");
        body.GetProperty("id").GetString()!.ShouldStartWith("chatcmpl-");
        body.GetProperty("model").GetString().ShouldBe("bygg"); // never the gateway model
        var message = body.GetProperty("choices")[0].GetProperty("message");
        var content = message.GetProperty("content").GetString()!;
        // The FakeGateway cites every [n] it sees in the prompt: the frame's example [1] and [3]; only [1] is a source.
        // The fake echoes the start of the final user message (the sources block); the answer filter escapes its "<".
        content.ShouldStartWith("Testsvar på \"&lt;sources note=");
        content.ShouldEndWith("Se [1].");
        var annotation = message.GetProperty("annotations").EnumerateArray().ShouldHaveSingleItem().GetProperty("url_citation");
        annotation.GetProperty("title").GetString()!.ShouldStartWith("Altan och uteplats");
        content[annotation.GetProperty("start_index").GetInt32()..annotation.GetProperty("end_index").GetInt32()].ShouldBe("[1]");
        body.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().ShouldBeGreaterThan(0);

        // §11.1, §12: an issued conversation id; a follow-up with it joins the conversation, a forged one is 404; the writer
        // stores metadata only (mode none): two messages per turn, the sources with their prompt numbers, the daily totals.
        var issued = response.Headers.GetValues("x-rag-conversation-id").ShouldHaveSingleItem();
        issued.ShouldStartWith("c1.");
        var conversationId = Guid.ParseExact(issued.Split('.')[1], "N");
        using (var followUp = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
               {
                   Content = JsonContent.Create(new { model = "bygg", messages = new[] { new { role = "user", content = "Behöver jag bygglov för en altan?" } } }),
               })
        {
            followUp.Headers.Add("x-rag-conversation-id", issued);
            using var second = await client.SendAsync(followUp, Ct);
            second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
        }

        using (var forged = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
               {
                   Content = JsonContent.Create(new { model = "bygg", messages = new[] { new { role = "user", content = "Hej?" } } }),
               })
        {
            forged.Headers.Add("x-rag-conversation-id", issued[..^2] + (issued[^2] == 'A' ? "BB" : "AA"));
            using var refused = await client.SendAsync(forged, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("conversation_not_found");
        }

        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using (var conversation = new Npgsql.NpgsqlCommand("""
            SELECT c."HistoryMode", c."TitleCiphertext" IS NULL, count(m.*), count(*) FILTER (WHERE m."ContentCiphertext" IS NULL), max(m."Seq"),
                   c."ExpiresAt" > now() + interval '89 days'
            FROM chat."Conversations" c JOIN chat."Messages" m ON m."ConversationId" = c."Id" WHERE c."Id" = $1 GROUP BY c."Id"
            """, connection) { Parameters = { new() { Value = conversationId } } })
        await using (var reader = await conversation.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            (reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetBoolean(5))
                .ShouldBe(("none", true, 4L, 4L, 4, true));
        }

        await using (var sources = new Npgsql.NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE s."Used" AND s."Number" = 1) FROM chat."MessageSources" s
            JOIN chat."Messages" m ON m."Id" = s."MessageId" WHERE m."ConversationId" = $1
            """, connection) { Parameters = { new() { Value = conversationId } } })
        {
            ((long)(await sources.ExecuteScalarAsync(Ct))!).ShouldBeGreaterThan(0);
        }

        // §3.4: the caller's list and detail (metadata only), then the user delete (§13.2): 204, audited, idempotent, then 404.
        var list = await client.GetFromJsonAsync<JsonElement>("/v1/conversations?limit=50", Ct);
        var listed = list.GetProperty("data").EnumerateArray().Single(c => c.GetProperty("id").GetString() == issued);
        (listed.GetProperty("assistant").GetString(), listed.GetProperty("history_mode").GetString(), listed.GetProperty("title").ValueKind)
            .ShouldBe(("bygg", "none", JsonValueKind.Null));
        var detail = await client.GetFromJsonAsync<JsonElement>($"/v1/conversations/{issued}", Ct);
        var messages = detail.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).ShouldBe(["user", "assistant", "user", "assistant"]);
        messages.ShouldAllBe(m => m.GetProperty("content").ValueKind == JsonValueKind.Null);
        messages[1].GetProperty("sources")[0].GetProperty("number").GetInt32().ShouldBe(1);
        (await client.GetFromJsonAsync<JsonElement>($"/v1/conversations/{conversationId}", Ct)).GetProperty("messages").GetArrayLength().ShouldBe(4);

        // §3.5, §14.1: a rating of the first answer (chatcmpl- id), changed once; the thumbs move by the delta; a comment is
        // refused in mode none; a bad rating is 400; another client's or an unknown message is 404.
        var answerId = body.GetProperty("id").GetString()!;
        async Task<HttpResponseMessage> Rate(HttpClient who, object payload) => await who.PostAsJsonAsync("/v1/feedback", payload, Ct);
        (await Rate(client, new { message_id = answerId, rating = 1, reason = "other" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Rate(client, new { message_id = answerId, rating = -1, reason = "incorrect" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (var withComment = await Rate(client, new { message_id = answerId, rating = -1, comment = "Fel höjd." }))
        {
            withComment.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await withComment.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("comment_not_stored");
        }

        (await Rate(client, new { message_id = answerId, rating = 3 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Rate(client, new { message_id = "chatcmpl-" + Guid.CreateVersion7().ToString("N"), rating = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using (var feedback = new Npgsql.NpgsqlCommand("""
            SELECT f."Rating", f."ReasonCategory", f."CommentCiphertext" IS NULL, t."ThumbsUp", t."ThumbsDown"
            FROM chat."Feedback" f JOIN chat."Messages" m ON m."Id" = f."MessageId" JOIN chat."Conversations" c ON c."Id" = m."ConversationId"
            JOIN chat."AssistantDailyTotals" t ON t."AssistantId" = c."AssistantId" AND t."ClientId" = c."ClientId" AND t."Classification" = c."Classification"
                AND t."Day" = (m."CreatedAt" AT TIME ZONE 'Europe/Stockholm')::date
            WHERE f."MessageId" = $1 AND f."Source" = 'api'
            """, connection) { Parameters = { new() { Value = Guid.ParseExact(answerId["chatcmpl-".Length..], "N") } } })
        await using (var reader = await feedback.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            // The API's own reasons keep their category (main-session decision on #264). The day's totals may include other tests' turns.
            (reader.GetInt16(0), reader.GetString(1), reader.GetBoolean(2)).ShouldBe(((short)-1, "incorrect", true));
            reader.GetInt32(4).ShouldBeGreaterThanOrEqualTo(1);
        }

        (await client.GetFromJsonAsync<JsonElement>($"/v1/conversations/{issued}", Ct)).GetProperty("messages")[1].GetProperty("rating").GetInt32().ShouldBe(-1);

        // #264 review: parallel ratings of one message (a double click, a retry) move exactly one thumb in the end, and never 500.
        async Task<(long Up, long Down)> ThumbsAsync()
        {
            await using var totals = new Npgsql.NpgsqlCommand("""
                SELECT coalesce(sum("ThumbsUp"), 0), coalesce(sum("ThumbsDown"), 0) FROM chat."AssistantDailyTotals" t
                JOIN chat."Conversations" c ON c."AssistantId" = t."AssistantId" AND c."ClientId" = t."ClientId" WHERE c."Id" = $1
                """, connection) { Parameters = { new() { Value = conversationId } } };
            await using var reader = await totals.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        var before = await ThumbsAsync(); // the -1 above counts one thumb down
        for (var round = 0; round < 10; round++)
        {
            var answers = await Task.WhenAll(Rate(client, new { message_id = answerId, rating = 1 }), Rate(client, new { message_id = answerId, rating = -1 }));
            answers.ShouldAllBe(a => a.StatusCode == HttpStatusCode.NoContent);
        }

        var stored = (await client.GetFromJsonAsync<JsonElement>($"/v1/conversations/{issued}", Ct)).GetProperty("messages")[1].GetProperty("rating").GetInt32();
        var after = await ThumbsAsync();
        (after.Up - before.Up, after.Down - before.Down).ShouldBe(stored == 1 ? (1L, -1L) : (0L, 0L));

        using var otherClient = Client(api, db.InternalKey);
        (await Rate(otherClient, new { message_id = answerId, rating = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherClient.GetAsync($"/v1/conversations/{conversationId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Another client's delete is a 204 that deletes nothing (whether the id exists is not told, #263 review).
        (await otherClient.DeleteAsync($"/v1/conversations/{conversationId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/v1/conversations/{conversationId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.DeleteAsync($"/v1/conversations/{issued}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/v1/conversations/{issued}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/v1/conversations/{conversationId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Rate(client, new { message_id = answerId, rating = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound); // the message went with it
        await using (var audit = new Npgsql.NpgsqlCommand("""
            SELECT count(*) FROM audit."AuditLog" WHERE "Action" = 'conversation.delete' AND "EntityId" = $1
            """, connection) { Parameters = { new() { Value = conversationId.ToString("D") } } })
        {
            // The api writes the audit row; reading it back needs the admin role.
            await using var admin = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Admin));
            await admin.OpenAsync(Ct);
            audit.Connection = admin;
            ((long)(await audit.ExecuteScalarAsync(Ct))!).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Under_a_max_residency_a_public_assistant_answers_at_the_cap_with_the_eu_key()
    {
        // A public assistant's level asks for External; this host holds only the Eu key (M2, #237).
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = snapshot.Assistants[db.AssistantId] with { Classification = Ume.Rag.Domain.Classification.Public },
            },
        };
        WebApplicationFactory<Program> Public(string? maxResidency) => db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            if (maxResidency is not null)
            {
                web.UseSetting("Gateway:MaxResidency", maxResidency);
            }

            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
        });

        await using (var capped = Public("Eu"))
        {
            using var client = Client(capped, db.IntegrationKey);
            using var response = await Ask(client, "Behöver jag bygglov för en altan högre än 1,2 meter?");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        await using var uncapped = Public(null);
        using var refused = await Ask(Client(uncapped, db.IntegrationKey), "Behöver jag bygglov för en altan högre än 1,2 meter?");
        refused.IsSuccessStatusCode.ShouldBeFalse(); // External without an External key: never sent
    }

    [Fact]
    public async Task A_question_without_coverage_gets_the_not_found_text_and_requests_are_checked()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        using (var none = await Ask(client, "Hur långt är det till månen i kilometer?"))
        {
            none.StatusCode.ShouldBe(HttpStatusCode.OK, await none.Content.ReadAsStringAsync(Ct));
            var body = await none.Content.ReadFromJsonAsync<JsonElement>(Ct);
            var message = body.GetProperty("choices")[0].GetProperty("message");
            message.GetProperty("content").GetString().ShouldBe("Inget svar i källorna.");
            message.GetProperty("annotations").GetArrayLength().ShouldBe(0);
            body.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBe(0); // no model call
        }

        using (var unknown = await Ask(client, "fråga", model: "finns-inte"))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var anonymous = api.CreateHttpsClient();
        using var refused = await Ask(anonymous, "fråga");
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refused.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("type").GetString().ShouldBe("authentication_error");
    }

    /// <summary>A key of the integration client scoped to <paramref name="collections"/> (as a department admin's key, #185).</summary>
    private async Task<string> ScopedKeyAsync(params Guid[] collections)
    {
        var generated = new Ume.Rag.Domain.Services.KeyHasher(System.Text.Encoding.UTF8.GetBytes(KnowledgeFixture.KeyPepper)).Generate();
        await using var context = TestPostgres.Context(db.Connection(RagDatabase.Admin));
        await Ume.Rag.Infrastructure.Config.ConfigWrites.ExecuteConfigWriteAsync(context, async (_, ct) =>
        {
            context.Keys.Add(new ApiKey
            {
                ClientId = db.IntegrationClient, Name = "scoped", Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test",
                Scopes = new KeyScopes([], collections),
            });
            await context.SaveChangesAsync(ct);
            return 0;
        }, Ct);
        return generated.PlainText;
    }

    [Fact]
    public async Task A_collection_scoped_key_searches_only_its_collections_and_sees_only_assistants_that_reach_them()
    {
        // #198 review: "bygg" links department A's and B's collections; "miljo" links only B's. A key scoped to A's collection.
        var snapshot = Snapshot();
        var bygg = snapshot.Assistants[db.AssistantId];
        var miljo = Guid.NewGuid();
        snapshot = snapshot with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = bygg with { Published = bygg.Published! with { CollectionIds = [db.DeptACollection, db.DeptBCollection] } },
                [miljo] = bygg with { Id = miljo, Slug = "miljo", Name = "Miljö", Published = bygg.Published! with { Id = Guid.NewGuid(), CollectionIds = [db.DeptBCollection] } },
            },
        };
        await using var api = db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
        });
        using var scoped = Client(api, await ScopedKeyAsync(db.DeptACollection));
        using var unscoped = Client(api, db.IntegrationKey);

        (await scoped.GetFromJsonAsync<JsonElement>("/v1/models", Ct)).GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ShouldBe(["bygg"]);
        (await unscoped.GetFromJsonAsync<JsonElement>("/v1/models", Ct)).GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString())
            .ShouldBe(["bygg", "miljo"]);
        using (var outside = await Ask(scoped, "Vad visar tomtkartan?", model: "miljo"))
        {
            outside.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await outside.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("model_not_found");
        }

        // The scoped key's answer cites only department A's document (B's is never in the prompt).
        await using var check = TestPostgres.Context(db.Connection(null));
        async Task<IReadOnlyList<Guid>> CitedSources(HttpClient client)
        {
            using var response = await Ask(client, "Vad visar tomtkartan om fastighetsgränser och servitut?");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            var documents = body.GetProperty("choices")[0].GetProperty("message").GetProperty("annotations").EnumerateArray()
                .Select(a => Guid.Parse(a.GetProperty("url_citation").GetProperty("url").GetString()!.Split('/')[3])).ToList();
            return await check.Documents.AsNoTracking().Where(d => documents.Contains(d.Id)).Select(d => d.SourceId).Distinct().ToListAsync(Ct);
        }

        (await CitedSources(scoped)).ShouldBe([db.DeptASource]);
        (await CitedSources(unscoped)).ShouldNotBeEmpty(); // unchanged: either department's document may rank first
    }

    private static async Task<List<JsonElement>> StreamAsync(HttpClient client, string question, bool usage = false)
    {
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "bygg", stream = true, stream_options = usage ? new { include_usage = true } : null, messages = new[] { new { role = "user", content = question } },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        lines[^1].ShouldBe("data: [DONE]");
        return [.. lines.SkipLast(1).Select(l => JsonDocument.Parse(l["data: ".Length..]).RootElement.Clone())];
    }

    private static bool IsEvent(JsonElement e) => e.TryGetProperty("event", out _);

    private static bool IsAnnotated(JsonElement e) =>
        e.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0 && c[0].GetProperty("delta").TryGetProperty("annotations", out _);

    private static string? FinishReason(JsonElement e) =>
        e.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0 && c[0].TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

    private static string Content(IEnumerable<JsonElement> events) => string.Concat(events
        .Where(e => e.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0 && c[0].GetProperty("delta").TryGetProperty("content", out _))
        .Select(e => e.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString()));

    [Fact]
    public async Task A_streamed_answer_carries_annotations_and_open_webui_gets_source_events()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        var events = await StreamAsync(client, "Behöver jag bygglov för en altan högre än 1,2 meter?", usage: true);
        events[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString().ShouldBe("assistant");
        Content(events).ShouldEndWith("Se [1].");
        var annotated = events.Where(e => e.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0 && c[0].GetProperty("delta").TryGetProperty("annotations", out _)).ToList();
        annotated.ShouldHaveSingleItem().GetProperty("choices")[0].GetProperty("delta").GetProperty("annotations")[0].GetProperty("url_citation")
            .GetProperty("title").GetString()!.ShouldStartWith("Altan och uteplats");
        events.Any(IsEvent).ShouldBeFalse();
        events.Single(e => e.TryGetProperty("usage", out _)).GetProperty("usage").GetProperty("completion_tokens").GetInt32().ShouldBeGreaterThan(0);
        events.Any(e => FinishReason(e) == "stop").ShouldBeTrue();

        // The same client as an Open WebUI client: one source event before the text, no annotations.
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Clients = snapshot.Clients.ToDictionary(c => c.Key, c => c.Key == db.IntegrationClient ? c.Value with { Kind = ClientKinds.OpenWebUi } : c.Value),
        };
        await using var owui = db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            // An Open WebUI client chats as its user (identity §5.2): the instance is configured and each request has the JWT.
            web.UseSetting("OpenWebUi:ClientId", db.IntegrationClient.ToString());
            web.UseSetting("OpenWebUi:JwtSecret", "open-webui-jwt-secret-for-tests-0123456789");
            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
        });
        using var owuiClient = Client(owui, db.IntegrationKey);
        var now = DateTimeOffset.UtcNow;
        owuiClient.DefaultRequestHeaders.Add("X-OpenWebUI-User-Jwt", OpenWebUiUserJwtTests.Jwt(new
        {
            sub = "u-1", role = "user", iss = "open-webui", iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds(),
        }));
        var chatId = "owui-chat-" + Guid.CreateVersion7().ToString("N");
        owuiClient.DefaultRequestHeaders.Add("X-OpenWebUI-Chat-Id", chatId);
        owuiClient.DefaultRequestHeaders.Add("X-Rag-Owui-Message-Id", "owui-answer-1");
        owuiClient.DefaultRequestHeaders.Add("X-Rag-Owui-User-Message-Id", "owui-question-1");
        var owuiEvents = await StreamAsync(owuiClient, "Behöver jag bygglov för en altan högre än 1,2 meter?");
        var source = owuiEvents.Where(e => e.TryGetProperty("event", out _)).ShouldHaveSingleItem().GetProperty("event");
        source.GetProperty("type").GetString().ShouldBe("source");
        source.GetProperty("data").GetProperty("metadata")[0].GetProperty("external").GetBoolean().ShouldBeTrue();
        source.GetProperty("data").GetProperty("document")[0].GetString()!.ShouldContain("bygglov");
        var sourceIndex = owuiEvents.FindIndex(e => e.TryGetProperty("event", out _));
        var markerIndex = owuiEvents.FindIndex(e => Content([e]).Contains("[1]", StringComparison.Ordinal));
        sourceIndex.ShouldBeLessThan(markerIndex);
        owuiEvents.Any(IsAnnotated).ShouldBeFalse();

        // §11.1, §14.2 (#263 review): Open WebUI's chat is the conversation key, with no issued id; its message ids are kept
        // for the feedback import.
        await owui.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var stored = new Npgsql.NpgsqlCommand("""
            SELECT c."SubjectId", c."SubjectKind", string_agg(m."Role" || ':' || m."ExternalMessageId", ',' ORDER BY m."Seq")
            FROM chat."Conversations" c JOIN chat."Messages" m ON m."ConversationId" = c."Id" WHERE c."ExternalChatId" = $1 GROUP BY c."Id"
            """, connection) { Parameters = { new() { Value = chatId } } };
        await using var reader = await stored.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        (reader.GetString(0), reader.GetString(1), reader.GetString(2)).ShouldBe(("owui:u-1", "owui-user", "user:owui-question-1,assistant:owui-answer-1"));
        (await reader.ReadAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_streamed_not_found_is_one_chunk_and_the_per_user_limit_answers_429()
    {
        await using var api = Host().WithWebHostBuilder(web => web.UseSetting("Chat:MessagesPerMinutePerUser", "2"));
        using var client = Client(api, db.IntegrationKey);
        var events = await StreamAsync(client, "Hur långt är det till månen i kilometer?");
        Content(events).ShouldBe("Inget svar i källorna.");

        (await Ask(client, "Hur långt är det till månen?")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var limited = await Ask(client, "Och till solen?");
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await limited.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("rate_limited");
    }

    [Fact]
    public async Task Settings_chat_supplies_the_not_found_default_and_a_follow_up_is_condensed_before_retrieval()
    {
        // The assistant has no not-found text of its own: Settings.Chat's default answers. A condense model is configured.
        var snapshot = Snapshot();
        var bygg = snapshot.Assistants[db.AssistantId];
        snapshot = snapshot with
        {
            ChatSettings = """{"condenseModel":"ume/chat-eu","notFound":{"sv":"Plattformens svar: inget hittades."}}""",
            Assistants = new Dictionary<Guid, AssistantEntry> { [db.AssistantId] = bygg with { Published = bygg.Published! with { NotFound = "{}" } } },
        };
        await using var api = db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
        });
        using var client = Client(api, db.IntegrationKey);

        using (var none = await Ask(client, "Hur långt är det till månen i kilometer?"))
        {
            (await none.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                .ShouldBe("Plattformens svar: inget hittades.");
        }

        // A follow-up with an anaphora goes through condense (one extra gateway call) and is still answered.
        using var followUp = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "bygg",
            messages = new object[]
            {
                new { role = "user", content = "Behöver jag bygglov för en altan?" },
                new { role = "assistant", content = "Ja, över 1,2 meter [1]." },
                new { role = "user", content = "Gäller det även en altan som är högre än 1,2 meter?" },
            },
        }, Ct);
        followUp.StatusCode.ShouldBe(HttpStatusCode.OK, await followUp.Content.ReadAsStringAsync(Ct));
        (await followUp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!
            .ShouldStartWith("Testsvar på");
    }
}
