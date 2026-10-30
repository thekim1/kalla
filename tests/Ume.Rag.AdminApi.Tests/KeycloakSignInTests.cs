extern alias fakegateway;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using FakeCatalog = fakegateway::Ume.Rag.FakeGateway.FakeCatalog;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The bundled realm (<c>dev/keycloak/ume-realm.json</c>) in a Keycloak container, the admin API on Postgres, and the
/// mirror filled as the org sync fills it from the FakeGateway catalogue.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    /// <summary>
    /// The version <c>Aspire.Hosting.Keycloak</c> runs (tag 26.6), pinned by digest. Same pin as the AppHost's
    /// <c>KeycloakDigest</c>; update both together.
    /// </summary>
    public const string Image = "quay.io/keycloak/keycloak:26.6.4@sha256:0aae0de7fca85525f727d3354df17896092de8bb26ae4c12d89c77e5df8cbce4";

    private const int Port = 8080;

    private readonly IContainer _keycloak;

    public KeycloakFixture()
    {
        var realm = TestPostgres.RepositoryFile(Path.Combine("dev", "keycloak", "ume-realm.json"));
        _keycloak = new ContainerBuilder(Image)
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", TestSecrets.RandomHex(16))
            .WithEnvironment("UME_RAG_OIDC_CLIENT_SECRET", OidcClientSecret)
            .WithEnvironment("UME_RAG_DEV_USER_PASSWORD", UserPassword)
            .WithEnvironment("UME_RAG_API_CLIENT_SECRET", ApiClientSecret)
            .WithEnvironment("UME_RAG_OWUI_CLIENT_SECRET", TestSecrets.RandomHex())
            .WithResourceMapping(new FileInfo(realm), "/opt/keycloak/data/import/")
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(Port).ForPath("/realms/ume/.well-known/openid-configuration"),
                wait => wait.WithTimeout(TimeSpan.FromMinutes(5))))
            .Build();
    }

    public RagDatabase Database { get; } = new();
    public string OidcClientSecret { get; } = TestSecrets.RandomHex();
    public string UserPassword { get; } = TestSecrets.RandomHex(16);
    public string ApiClientSecret { get; } = TestSecrets.RandomHex();

    /// <summary>The realm's issuer as the test process reaches it.</summary>
    public string Authority => $"http://{_keycloak.Hostname}:{_keycloak.GetMappedPublicPort(Port)}/realms/ume";

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Database.InitializeAsync().AsTask(), _keycloak.StartAsync());
        await using var db = TestPostgres.Context(Database.Connection(RagDatabase.Admin));
        db.Departments.AddRange(FakeCatalog.Departments.Select(d => new Department
        {
            Id = Guid.CreateVersion7(), Name = d.Name, CostCenterCode = d.CostCenterCode, MemberGroups = d.MemberGroups, IsActive = d.IsActive,
            SyncedAt = DateTimeOffset.UtcNow,
        }));
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _keycloak.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>The admin API against the realm, on the admin UI's origin (a redirect URI of <c>ume-rag-admin</c>).</summary>
    public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development"); // plain-HTTP metadata from the container
        web.UseSetting("ConnectionStrings:ragdb", Database.Connection(RagDatabase.Admin));
        web.UseSetting("Oidc:Authority", Authority);
        web.UseSetting("Oidc:ClientSecret", OidcClientSecret);
    });
}

/// <summary>
/// Sign-in through the real BFF as each user of the bundled realm (P0 checklist #9 done-when; identity design §11, §13.3)
/// and the realm's machine and delegated clients (to-verify #83).
/// </summary>
public sealed partial class KeycloakSignInTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string[], string[], string[]> Users => new()
    {
        // user, roles, departments from the claim, departments from member groups
        { "rag-platform-admin", ["rag-platform-admin"], ["1000", "3000", "5000"], [] },
        { "rag-department-admin", ["rag-department-admin"], ["1000"], [] },
        { "rag-pipeline-editor", ["rag-pipeline-editor"], ["1000"], [] },
        { "rag-reviewer", ["rag-reviewer"], ["1000"], [] },
        { "rag-skill-editor", ["rag-skill-editor"], ["1000"], [] },
        { "rag-viewer", ["rag-viewer"], ["1000"], [] },
        // No attribute: department 3000 lists the group Byggnadsnämnden in the FakeGateway catalogue (identity design §2.3).
        { "rag-group-member", ["rag-viewer"], [], ["3000"] },
        { "rag-no-role", [], ["1000"], [] },
        { "rag-chat-user", [], [], [] },
    };

    [Theory]
    [MemberData(nameof(Users))]
    public async Task Each_user_signs_in_with_the_right_roles_and_departments(string user, string[] roles, string[] claimDepartments, string[] groupDepartments)
    {
        await using var factory = keycloak.Factory();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:5174"), AllowAutoRedirect = false });

        await SignInAsync(admin, user);

        using var json = JsonDocument.Parse(await admin.GetStringAsync("/bff/user", Ct));
        var root = json.RootElement;
        root.GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        root.GetProperty("email").GetString().ShouldBe($"{user}@example.invalid");
        Strings(root.GetProperty("roles")).ShouldBe(roles, ignoreOrder: true);
        Strings(root.GetProperty("departmentCodes")).ShouldBe([.. claimDepartments, .. groupDepartments], ignoreOrder: true);
        root.GetProperty("departmentSources").EnumerateArray()
            .Select(s => (s.GetProperty("code").GetString(), s.GetProperty("source").GetString()))
            .ShouldBe([.. claimDepartments.Select(c => ((string?)c, (string?)"claim")), .. groupDepartments.Select(c => ((string?)c, (string?)"group"))], ignoreOrder: true);

        // Every /api call needs a rag-* role (identity design §1.3).
        using var ping = await admin.GetAsync("/api/ping", Ct);
        ping.StatusCode.ShouldBe(roles.Length > 0 ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Client_credentials_tokens_carry_client_id_and_no_sid()
    {
        // to-verify #83 (identity design §7.1 step 4) on the Keycloak version Aspire pins.
        using var http = new HttpClient();
        using var response = await http.PostAsync($"{keycloak.Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "ume-rag-api-dev", ["client_secret"] = keycloak.ApiClientSecret,
        }), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var token = await AccessTokenAsync(response);

        token.GetProperty("client_id").GetString().ShouldBe("ume-rag-api-dev");
        token.TryGetProperty("sid", out _).ShouldBeFalse();
        token.GetProperty("aud").GetString().ShouldBe("ume-rag-api");
        Strings(token.GetProperty("roles")).ShouldBe(["Knowledge.Read.All", "Chat.Invoke.All"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_delegated_chat_token_carries_its_scopes_and_the_api_audience()
    {
        // ume-rag-chat-dev: public, code + PKCE, loopback redirect; Chat.Use by default, Knowledge.Read on request.
        const string redirect = "http://127.0.0.1:49152/callback";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = $"{keycloak.Authority}/protocol/openid-connect/auth?client_id=ume-rag-chat-dev&response_type=code" +
                        $"&scope={Uri.EscapeDataString("openid Knowledge.Read")}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                        $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        var callback = await LoginAtKeycloakAsync(authorize, "rag-chat-user");
        callback.Action.ShouldStartWith(redirect);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(callback.Action).Query)["code"].ShouldNotBeNull();

        using var http = new HttpClient();
        using var response = await http.PostAsync($"{keycloak.Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "ume-rag-chat-dev", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        }), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var token = await AccessTokenAsync(response);

        token.GetProperty("azp").GetString().ShouldBe("ume-rag-chat-dev");
        token.GetProperty("scope").GetString()!.Split(' ').ShouldBeSubsetOf(["openid", "profile", "email", "Chat.Use", "Knowledge.Read"]);
        token.GetProperty("scope").GetString()!.Split(' ').ShouldContain("Chat.Use");
        token.GetProperty("scope").GetString()!.Split(' ').ShouldContain("Knowledge.Read");
        var audience = token.GetProperty("aud");
        (audience.ValueKind == JsonValueKind.Array ? Strings(audience) : [audience.GetString()!]).ShouldContain("ume-rag-api");
    }

    /// <summary>
    /// Runs the BFF's code flow: <c>/bff/login</c>, the Keycloak login form, then the response back to <c>/signin-oidc</c>
    /// (a form post, the handler's default response mode) until the session cookie is set.
    /// </summary>
    private async Task SignInAsync(HttpClient admin, string user)
    {
        using var login = await admin.GetAsync("/bff/login", Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var callback = await LoginAtKeycloakAsync(login.Headers.Location!.ToString(), user);

        var path = new Uri(callback.Action).PathAndQuery;
        using var signedIn = callback.Fields.Count > 0
            ? await admin.PostAsync(path, new FormUrlEncodedContent(callback.Fields), Ct)
            : await admin.GetAsync(path, Ct);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect, await signedIn.Content.ReadAsStringAsync(Ct));
        signedIn.Headers.Location!.ToString().ShouldBe("/");
    }

    /// <summary>
    /// Opens <paramref name="authorize"/>, submits the login form as <paramref name="user"/> and returns where Keycloak
    /// sends the browser: a redirect, or the auto-submitted form of <c>response_mode=form_post</c>.
    /// </summary>
    private async Task<(string Action, Dictionary<string, string> Fields)> LoginAtKeycloakAsync(string authorize, string user)
    {
        using var browser = new HttpClient(new LocalhostBrowser());
        using var form = await StayAtKeycloakAsync(browser, await browser.GetAsync(authorize, Ct));
        form.StatusCode.ShouldBe(HttpStatusCode.OK, $"{authorize} -> {form.Headers.Location}");
        var page = await form.Content.ReadAsStringAsync(Ct);
        var action = WebUtility.HtmlDecode(ActionOf(LoginForm().Match(page).Value));
        action.ShouldNotBeNullOrEmpty(page);

        using var submitted = await StayAtKeycloakAsync(browser, await browser.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = user, ["password"] = keycloak.UserPassword, ["credentialId"] = "",
        }), Ct));
        if (submitted.StatusCode == HttpStatusCode.Found)
        {
            return (submitted.Headers.Location!.ToString(), []);
        }

        var body = await submitted.Content.ReadAsStringAsync(Ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        // Keycloak's form_post page: <FORM METHOD="POST" ACTION="…"> with <INPUT TYPE="HIDDEN" NAME="…" VALUE="…" />.
        var post = ActionOf(AnyForm().Match(body).Value);
        post.ShouldNotBeNullOrEmpty(body);
        var fields = HiddenInput().Matches(body).Select(m => m.Value).ToDictionary(
            input => Attribute(input, "name"), input => WebUtility.HtmlDecode(Attribute(input, "value")));
        return (WebUtility.HtmlDecode(post), fields);
    }

    /// <summary>
    /// Follows redirects within Keycloak (the authorize request redirects to the login form after the handler's pushed
    /// authorization request) and stops at the one that leaves it for the client.
    /// </summary>
    private async Task<HttpResponseMessage> StayAtKeycloakAsync(HttpClient browser, HttpResponseMessage response)
    {
        var origin = new Uri(keycloak.Authority).GetLeftPart(UriPartial.Authority);
        while (response.StatusCode == HttpStatusCode.Found && response.Headers.Location is { } location &&
               new Uri(new Uri(origin), location).GetLeftPart(UriPartial.Authority) == origin)
        {
            var next = new Uri(new Uri(origin), location);
            response.Dispose();
            response = await browser.GetAsync(next, Ct);
        }

        return response;
    }

    /// <summary>
    /// Keeps Keycloak's cookies like a browser on <c>localhost</c>: they are all <c>Secure</c>, which browsers honour over
    /// plain HTTP to localhost but <see cref="CookieContainer"/> does not. One realm, so name and value are enough.
    /// </summary>
    private sealed class LocalhostBrowser() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        private readonly Dictionary<string, string> _cookies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            var response = await base.SendAsync(request, cancellationToken);
            foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var pair = header.Split(';', 2)[0].Split('=', 2);
                if (header.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase) || pair[1].Length == 0)
                {
                    _cookies.Remove(pair[0]);
                }
                else
                {
                    _cookies[pair[0]] = pair[1];
                }
            }

            return response;
        }
    }

    private static async Task<JsonElement> AccessTokenAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var payload = json.RootElement.GetProperty("access_token").GetString()!.Split('.')[1];
        return JsonDocument.Parse(Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/').PadRight((payload.Length + 3) / 4 * 4, '='))).RootElement.Clone();
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ActionOf(string formTag) => Attribute(formTag, "action");

    private static string Attribute(string tag, string name) =>
        Regex.Match(tag, $"""\s{name}="(?<value>[^"]*)""", RegexOptions.IgnoreCase).Groups["value"].Value;

    [GeneratedRegex("""<form[^>]*\sid="kc-form-login"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LoginForm();

    [GeneratedRegex("""<form[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex AnyForm();

    [GeneratedRegex("""<input[^>]*\stype="hidden"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();
}
