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
        content.ShouldStartWith("Testsvar på \"<sources note="); // the fake echoes the start of the final user message: the sources block
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

        using (var stream = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "bygg", stream = true, messages = new[] { new { role = "user", content = "q" } } }, Ct))
        {
            stream.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await stream.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("param").GetString().ShouldBe("stream");
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
}
