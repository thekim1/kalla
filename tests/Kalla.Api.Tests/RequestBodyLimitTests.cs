using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Answering.Chat;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// to-verify #32 on real Kestrel (the TestServer enforces no body limit): with the knowledge and chat roles in one process
/// (<c>Rag:Roles = knowledge,chat</c>), the knowledge routes keep the host's 16 KB (knowledge design §3.6) and the chat route
/// alone takes 2 MB (chat design §3.2, its <c>IRequestSizeLimitMetadata</c>). A body of exactly the limit is answered, one
/// byte more is 413. The bodies are valid JSON padded with whitespace; an oversized one is sent with
/// <c>Expect: 100-continue</c>, so Kestrel answers from the <c>Content-Length</c> before the client sends it.
/// </summary>
public sealed class RequestBodyLimitTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int KnowledgeLimit = 16 * 1024;

    private KestrelHost<Program> Host()
    {
        var snapshot = db.Snapshot();
        var assistant = snapshot.Assistants[db.AssistantId];
        snapshot = snapshot with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = assistant with { Audiences = [.. assistant.Audiences, new AudienceEntry(AssistantAudienceKinds.Client, db.IntegrationClient, null)] },
            },
        };
        return new KestrelHost<Program>(db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            web.ConfigureTestServices(services => services.AddHttpClient(GatewayChatClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
        }));
    }

    private static HttpClient Client(KestrelHost<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    /// <summary><paramref name="json"/> (an object) padded with spaces before its closing brace to <paramref name="bytes"/> UTF-8 bytes.</summary>
    private static ByteArrayContent Padded(string json, long bytes)
    {
        var open = json[..^1];
        var padding = bytes - Encoding.UTF8.GetByteCount(open) - 1;
        padding.ShouldBeGreaterThanOrEqualTo(0);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(open + new string(' ', (int)padding) + "}"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength.ShouldBe(bytes);
        return content;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string json, long bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Padded(json, bytes) };
        request.Headers.ExpectContinue = true;
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Knowledge_routes_keep_16_kb_when_the_chat_role_shares_the_host()
    {
        await using var host = Host();
        using var client = Client(host, db.InternalKey);
        const string Retrieve = """{"query":"bygglov"}""";

        using (var at = await PostAsync(client, "/v1/retrieve", Retrieve, KnowledgeLimit))
        {
            at.StatusCode.ShouldBe(HttpStatusCode.OK, await at.Content.ReadAsStringAsync(Ct));
        }

        using var over = await PostAsync(client, "/v1/retrieve", Retrieve, KnowledgeLimit + 1);
        var body = await over.Content.ReadAsStringAsync(Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, body);
        // Kestrel's limit, as the host's problem details (knowledge design §3.6: 413 without a code).
        JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString().ShouldBe($"Request body too large. The max request body size is {KnowledgeLimit} bytes.");
    }

    [Fact]
    public async Task The_chat_route_takes_2_mb_and_refuses_one_byte_more()
    {
        await using var host = Host();
        using var client = Client(host, db.IntegrationKey);
        const string Chat = """{"model":"bygg","messages":[{"role":"user","content":"Behöver jag bygglov för en altan?"}]}""";

        using (var at = await PostAsync(client, "/v1/chat/completions", Chat, ChatEndpoints.BodyLimit))
        {
            at.StatusCode.ShouldBe(HttpStatusCode.OK, await at.Content.ReadAsStringAsync(Ct));
        }

        using var over = await PostAsync(client, "/v1/chat/completions", Chat, ChatEndpoints.BodyLimit + 1);
        var body = await over.Content.ReadAsStringAsync(Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, body);
        // Chat design §3.6: the OpenAI error shape with code request_too_large, not the host's problem details.
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("type").GetString()).ShouldBe(("request_too_large", "invalid_request_error"));
    }
}
