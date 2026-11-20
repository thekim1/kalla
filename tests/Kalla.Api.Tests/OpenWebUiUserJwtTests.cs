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
using Kalla.Answering.Chat;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>Identity design §5.2: Open WebUI's per-request user JWT, and the chat routes for an Open WebUI client (P1a item 15d).</summary>
public sealed class OpenWebUiUserJwtTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private const string Secret = "open-webui-jwt-secret-for-tests-0123456789";
    private const string LongSecret = "open-webui-jwt-secret-for-tests-long-enough-for-hs512-0123456789";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Jwt(object payload, string secret = Secret, object? header = null)
    {
        var head = B64(JsonSerializer.SerializeToUtf8Bytes(header ?? new { alg = "HS256", typ = "JWT" }));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{head}.{body}.{B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)))}";
    }

    /// <summary>A token signed with <paramref name="alg"/> (an HMAC other than HS256) and the right secret.</summary>
    private static string Signed(object payload, string alg)
    {
        var head = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg, typ = "JWT" }));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        var input = Encoding.ASCII.GetBytes(head + "." + body);
        var key = Encoding.UTF8.GetBytes(LongSecret);
        return $"{head}.{body}.{B64(alg == "HS384" ? HMACSHA384.HashData(key, input) : HMACSHA512.HashData(key, input))}";
    }

    private static object Claims(DateTimeOffset? iat = null, int lifetime = 300, string role = "user", string iss = "open-webui", string sub = "u-123") =>
        new { sub, email = "anna@umea.se", name = "Anna", role, iss, iat = (iat ?? Now).ToUnixTimeSeconds(), exp = (iat ?? Now).AddSeconds(lifetime).ToUnixTimeSeconds() };

    private static (OpenWebUiUser? User, string? Refusal) Check(string? token, string secret = Secret)
    {
        var http = new DefaultHttpContext();
        if (token is not null)
        {
            http.Request.Headers[OpenWebUiUserJwt.Header] = token;
        }

        return OpenWebUiUserJwt.ValidateAsync(http.Request, new OpenWebUiOptions { JwtSecret = secret }, Now)
            .GetAwaiter().GetResult();
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
        // Another algorithm with the same secret (long enough for HS512, so only the algorithm pin refuses), and a token
        // without iat or exp (#512).
        Check(Jwt(Claims(), LongSecret), LongSecret).User.ShouldNotBeNull();
        Check(Signed(Claims(), "HS384"), LongSecret).Refusal.ShouldBe("user_jwt_invalid");
        Check(Signed(Claims(), "HS512"), LongSecret).Refusal.ShouldBe("user_jwt_invalid");
        Check(Jwt(new { sub = "u-123", role = "user", iss = "open-webui", exp = Now.AddMinutes(5).ToUnixTimeSeconds() })).Refusal.ShouldBe("user_jwt_invalid");
        Check(Jwt(new { sub = "u-123", role = "user", iss = "open-webui", iat = Now.ToUnixTimeSeconds() })).Refusal.ShouldBe("user_jwt_invalid");
        // Out of DateTimeOffset's range, with a valid signature: a refusal, not a 500 (#205 review).
        Check(Jwt(new { sub = "u-123", role = "user", iss = "open-webui", iat = Now.ToUnixTimeSeconds(), exp = long.MaxValue })).Refusal.ShouldBe("user_jwt_invalid");
    }

    /// <summary>kalla-api with the fixture's Open WebUI client the deployment's own (<paramref name="managed"/>) or one made by hand.</summary>
    private WebApplicationFactory<Program> Host(bool managed)
    {
        var snapshot = db.Snapshot();
        snapshot = snapshot with
        {
            Clients = snapshot.Clients.ToDictionary(c => c.Key, c => c.Key == db.OwuiClient ? c.Value with { IsManagedByDeployment = managed } : c.Value),
        };
        return HostOf(snapshot);
    }

    private WebApplicationFactory<Program> HostOf(Kalla.Domain.Access.PolicySnapshot snapshot) => db.ApiHost(snapshot).WithWebHostBuilder(web =>
    {
        web.UseSetting("Rag:Roles", "knowledge,chat");
        web.UseSetting("OpenWebUi:JwtSecret", Secret);
        web.ConfigureTestServices(services =>
            services.UseGatewayCredentialCeiling().AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
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
        await using var api = Host(managed: true);
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
    public async Task An_open_webui_client_the_deployment_did_not_make_is_never_trusted_with_its_users()
    {
        // Identity §5.2 (M-17 C4): only the deployment's own Open WebUI client; one made by hand in the admin, or none (#95), is not.
        await using var api = Host(managed: false);
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.OwuiKey);
        using var response = await Ask(client, Jwt(Claims(iat: DateTimeOffset.UtcNow)));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Code(response)).ShouldBe("user_jwt_invalid");
    }
}
