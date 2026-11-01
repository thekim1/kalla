using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
}
