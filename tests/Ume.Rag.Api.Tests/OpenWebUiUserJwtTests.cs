using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Answering.Chat;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>Identity design §5.2: Open WebUI's per-request user JWT, and the chat routes for an Open WebUI client (P1a item 15d).</summary>
public sealed class OpenWebUiUserJwtTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private const string Secret = "open-webui-jwt-secret-for-tests-0123456789";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Jwt(object payload, string secret = Secret, object? header = null)
    {
        var head = B64(JsonSerializer.SerializeToUtf8Bytes(header ?? new { alg = "HS256", typ = "JWT" }));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{head}.{body}.{B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)))}";
    }

    private static object Claims(DateTimeOffset? iat = null, int lifetime = 300, string role = "user", string iss = "open-webui", string sub = "u-123") =>
        new { sub, email = "anna@umea.se", name = "Anna", role, iss, iat = (iat ?? Now).ToUnixTimeSeconds(), exp = (iat ?? Now).AddSeconds(lifetime).ToUnixTimeSeconds() };

    private static (OpenWebUiUser? User, string? Refusal) Check(string? token)
    {
        var http = new DefaultHttpContext();
        if (token is not null)
        {
            http.Request.Headers[OpenWebUiUserJwt.Header] = token;
        }

        return OpenWebUiUserJwt.Validate(http.Request, new OpenWebUiOptions { ClientId = Guid.NewGuid().ToString(), JwtSecret = Secret }, Now);
    }

    [Fact]
    public void A_valid_token_gives_the_user_and_every_rule_refuses_with_its_code()
    {
        var user = Check(Jwt(Claims())).User.ShouldNotBeNull();
        (user.Subject, user.Name, user.Email, user.Role).ShouldBe(("u-123", "Anna", "anna@umea.se", "user"));

        Check(null).Refusal.ShouldBe("user_jwt_missing");
        Check(Jwt(Claims(), secret: "another-secret-of-at-least-32-bytes-xx")).Refusal.ShouldBe("user_jwt_invalid");
        Check(Jwt(Claims(), header: new { alg = "none" })).Refusal.ShouldBe("user_jwt_invalid");
        Check(Jwt(Claims(), header: new { alg = "HS256", kid = "k1" })).Refusal.ShouldBe("user_jwt_invalid");
        Check(Jwt(Claims(iss: "someone-else"))).Refusal.ShouldBe("user_jwt_invalid");
        Check(Jwt(Claims(lifetime: 3600))).Refusal.ShouldBe("user_jwt_invalid"); // lifetime above 600 s
        Check(Jwt(Claims(iat: Now.AddMinutes(5)))).Refusal.ShouldBe("user_jwt_invalid"); // issued in the future
        Check(Jwt(Claims(iat: Now.AddMinutes(-10)))).Refusal.ShouldBe("user_jwt_expired");
        Check(Jwt(Claims(role: "pending"))).Refusal.ShouldBe("user_jwt_role");
        Check(Jwt(Claims(sub: ""))).Refusal.ShouldBe("user_jwt_invalid");
        Check("not.a.jwt").Refusal.ShouldBe("user_jwt_invalid");
    }

    private WebApplicationFactory<Program> Host(string? clientId) => db.ApiHost().WithWebHostBuilder(web =>
    {
        web.UseSetting("Rag:Roles", "knowledge,chat");
        web.UseSetting("OpenWebUi:ClientId", clientId ?? "");
        web.UseSetting("OpenWebUi:JwtSecret", Secret);
        web.ConfigureTestServices(services =>
            services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
    });

    private static Task<HttpResponseMessage> Ask(HttpClient client, string? jwt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "bygg", messages = new[] { new { role = "user", content = "Behöver jag bygglov för en altan?" } } }),
        };
        if (jwt is not null)
        {
            request.Headers.Add(OpenWebUiUserJwt.Header, jwt);
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString();

    [Fact]
    public async Task An_open_webui_client_chats_as_its_user_and_lists_models_at_client_level()
    {
        await using var api = Host(db.OwuiClient.ToString());
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.OwuiKey);
        var now = DateTimeOffset.UtcNow;

        // Models without a user JWT: the client-level list (identity §5.4).
        (await client.GetFromJsonAsync<JsonElement>("/v1/models", Ct)).GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ShouldBe(["bygg"]);

        using (var missing = await Ask(client, null))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await Code(missing)).ShouldBe("user_jwt_missing");
        }

        using (var forged = await Ask(client, Jwt(Claims(iat: now), secret: "a-forged-secret-of-at-least-32-bytes-xx")))
        {
            (await Code(forged)).ShouldBe("user_jwt_invalid");
        }

        using var answered = await Ask(client, Jwt(Claims(iat: now)));
        answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync(Ct));

        // The plain user headers are never read: without the JWT they are refused, so a broken secret is noticed.
        using var plain = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        plain.Headers.Add("X-OpenWebUI-User-Id", "u-123");
        using var refused = await client.SendAsync(plain, Ct);
        (await Code(refused)).ShouldBe("user_jwt_missing");
    }

    [Fact]
    public async Task Without_a_configured_instance_no_open_webui_user_is_trusted()
    {
        await using var api = Host(null); // OpenWebUi:ClientId empty (#95): not configured
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.OwuiKey);
        using var response = await Ask(client, Jwt(Claims(iat: DateTimeOffset.UtcNow)));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Code(response)).ShouldBe("user_jwt_invalid");
    }
}
