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
        var owuiEvents = await StreamAsync(owuiClient, "Behöver jag bygglov för en altan högre än 1,2 meter?");
        var source = owuiEvents.Where(e => e.TryGetProperty("event", out _)).ShouldHaveSingleItem().GetProperty("event");
        source.GetProperty("type").GetString().ShouldBe("source");
        source.GetProperty("data").GetProperty("metadata")[0].GetProperty("external").GetBoolean().ShouldBeTrue();
        source.GetProperty("data").GetProperty("document")[0].GetString()!.ShouldContain("bygglov");
        var sourceIndex = owuiEvents.FindIndex(e => e.TryGetProperty("event", out _));
        var markerIndex = owuiEvents.FindIndex(e => Content([e]).Contains("[1]", StringComparison.Ordinal));
        sourceIndex.ShouldBeLessThan(markerIndex);
        owuiEvents.Any(IsAnnotated).ShouldBeFalse();
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
}
