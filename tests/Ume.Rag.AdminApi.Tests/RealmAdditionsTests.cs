extern alias fakegateway;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
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
/// RAG's realm additions (<c>deploy/keycloak/</c>, identity design §11.2, P1 item 16) applied with
/// <c>apply-additions.py</c> to a Keycloak 26.6.4 container that already holds a minimal stand-in for the gateway's base
/// realm <c>ume</c> (a gateway client and roles, <c>departmentCodes</c> already declared). Then the test accounts are
/// created by hand, as the pilot accounts will be (the files hold no users).
/// </summary>
public sealed class RealmAdditionsFixture : IAsyncLifetime
{
    private const int Port = 8080;
    private readonly IContainer _keycloak;
    private readonly string _adminPassword = TestSecrets.RandomHex(16);

    public RealmAdditionsFixture()
    {
        var realm = TestPostgres.RepositoryFile(Path.Combine("tests", "Ume.Rag.AdminApi.Tests", "Keycloak", "ume-realm.json"));
        _keycloak = new ContainerBuilder(KeycloakFixture.Image)
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", _adminPassword)
            .WithEnvironment("GATEWAY_CLIENT_SECRET", TestSecrets.RandomHex())
            .WithResourceMapping(new FileInfo(realm), "/opt/keycloak/data/import/")
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(Port).ForPath("/realms/ume/.well-known/openid-configuration"),
                wait => wait.WithTimeout(TimeSpan.FromMinutes(5))))
            .Build();
    }

    public RagDatabase Database { get; } = new();
    public string OidcClientSecret { get; } = TestSecrets.RandomHex();

    /// <summary>Meets the additions' password policy (12 characters or more, not the user name).</summary>
    public string UserPassword { get; } = TestSecrets.RandomHex(16);

    public string BaseUrl => $"http://{_keycloak.Hostname}:{_keycloak.GetMappedPublicPort(Port)}";
    public string Authority => $"{BaseUrl}/realms/ume";

    /// <summary>The output of the first apply (both parts).</summary>
    public (int ExitCode, string Output) FirstApply { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Database.InitializeAsync().AsTask(), _keycloak.StartAsync());
        await using (var db = TestPostgres.Context(Database.Connection(RagDatabase.Admin)))
        {
            db.Departments.AddRange(FakeCatalog.Departments.Select(d => new Department
            {
                Id = Guid.CreateVersion7(), Name = d.Name, CostCenterCode = d.CostCenterCode, MemberGroups = d.MemberGroups, IsActive = d.IsActive,
                SyncedAt = DateTimeOffset.UtcNow,
            }));
            await db.SaveChangesAsync();
        }

        FirstApply = await ApplyAsync("all");
        if (FirstApply.ExitCode != 0)
        {
            throw new InvalidOperationException("apply-additions.py failed:\n" + FirstApply.Output);
        }

        await CreateUserAsync("rag-editor-user", ["rag-pipeline-editor"], ["1000"]);
        await CreateUserAsync("rag-admin-user", ["rag-platform-admin", "rag-department-admin", "gateway-admin"], ["1000"]);
        await CreateUserAsync("rag-department-admin-user", ["rag-department-admin"], ["1000"]);
    }

    public async ValueTask DisposeAsync()
    {
        await _keycloak.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>Runs <c>deploy/keycloak/apply-additions.py</c> against the container, as the gateway session would.</summary>
    public async Task<(int ExitCode, string Output)> ApplyAsync(string part)
    {
        var script = TestPostgres.RepositoryFile(Path.Combine("deploy", "keycloak", "apply-additions.py"));
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-I", script, "--url", BaseUrl, "--realm", "ume", "--admin-user", "admin", "--part", part })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["KEYCLOAK_ADMIN_PASSWORD"] = _adminPassword;
        start.Environment["UME_RAG_ADMIN_PUBLIC_HOST"] = "rag-admin.example.invalid";
        start.Environment["UME_RAG_OPENWEBUI_HOST"] = "chat.example.invalid";
        start.Environment["UME_RAG_OIDC_CLIENT_SECRET"] = OidcClientSecret;
        start.Environment["UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET"] = TestSecrets.RandomHex();
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    /// <summary>An admin REST client for realm <c>ume</c> (bootstrap admin of <c>master</c>).</summary>
    public async Task<HttpClient> AdminAsync()
    {
        using var anonymous = new HttpClient();
        using var response = await anonymous.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = "admin", ["password"] = _adminPassword,
        }));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var admin = new HttpClient { BaseAddress = new Uri($"{BaseUrl}/admin/realms/ume/") };
        admin.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("access_token").GetString());
        return admin;
    }

    public async Task<string> IdOfAsync(HttpClient admin, string path)
    {
        using var json = JsonDocument.Parse(await admin.GetStringAsync(path));
        return json.RootElement.EnumerateArray().Single().GetProperty("id").GetString()!;
    }

    private async Task CreateUserAsync(string user, string[] roles, string[] departments)
    {
        using var admin = await AdminAsync();
        using var created = await admin.PostAsJsonAsync("users", new
        {
            username = user, email = $"{user}@example.invalid", emailVerified = true, firstName = "Test", lastName = "User", enabled = true,
            attributes = new Dictionary<string, string[]> { ["departmentCodes"] = departments },
            credentials = new[] { new { type = "password", value = UserPassword, temporary = false } },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = created.Headers.Location!.Segments[^1];
        var reps = new List<JsonElement>();
        foreach (var role in roles)
        {
            reps.Add(JsonDocument.Parse(await admin.GetStringAsync($"roles/{role}")).RootElement.Clone());
        }

        using var mapped = await admin.PostAsJsonAsync($"users/{id}/role-mappings/realm", reps);
        mapped.EnsureSuccessStatusCode();
    }

    /// <summary>The admin API against the realm on the admin UI's local origin (a local redirect URI of the additions).</summary>
    public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("ConnectionStrings:ragdb", Database.Connection(RagDatabase.Admin));
        web.UseSetting("Oidc:Authority", Authority);
        web.UseSetting("Oidc:ClientSecret", OidcClientSecret);
    });
}

/// <summary>
/// P1 item 16 done-when: the additions import next to a minimal <c>ume</c> realm without collisions, and sign-in works
/// (identity design §11.2, §16 "full scope allowed off"; to-verify #83 rechecked in <see cref="KeycloakSignInTests"/>).
/// </summary>
public sealed partial class RealmAdditionsTests(RealmAdditionsFixture keycloak) : IClassFixture<RealmAdditionsFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_additions_import_next_to_the_base_realm_and_a_second_import_changes_nothing()
    {
        keycloak.FirstApply.Output.ShouldContain("partial import: 10 added"); // 7 roles, 3 clients
        keycloak.FirstApply.Output.ShouldContain("added teamCodes; already declared departmentCodes");

        var again = await keycloak.ApplyAsync("additions");
        again.ExitCode.ShouldBe(1, again.Output);
        again.Output.ShouldContain("already exists");

        using var admin = await keycloak.AdminAsync();
        using var clients = JsonDocument.Parse(await admin.GetStringAsync("clients", Ct));
        var rag = clients.RootElement.EnumerateArray().Where(c => c.GetProperty("clientId").GetString() is "ume-rag-admin" or "open-webui" or "ume-rag-api" or "llm-gateway-admin").ToList();
        rag.Count.ShouldBe(4);
        rag.ShouldAllBe(c => !c.GetProperty("fullScopeAllowed").GetBoolean());
        rag.Single(c => c.GetProperty("clientId").GetString() == "open-webui").GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString())
            .ShouldBe(["https://chat.example.invalid/oauth/oidc/callback", "http://localhost:8080/oauth/oidc/callback"], ignoreOrder: true);
        using var users = JsonDocument.Parse(await admin.GetStringAsync("users?briefRepresentation=true", Ct));
        users.RootElement.GetArrayLength().ShouldBe(3); // only the three the fixture created
    }

    [Fact]
    public async Task The_realm_wide_settings_hold_password_policy_brute_force_and_the_otp_flow()
    {
        using var admin = await keycloak.AdminAsync();
        using var realm = JsonDocument.Parse(await admin.GetStringAsync("", Ct));
        realm.RootElement.GetProperty("bruteForceProtected").GetBoolean().ShouldBeTrue();
        realm.RootElement.GetProperty("browserFlow").GetString().ShouldBe("rag-browser");
        realm.RootElement.GetProperty("passwordPolicy").GetString()!.ShouldContain("length(12)");

        var id = await keycloak.IdOfAsync(admin, "users?exact=true&username=rag-editor-user");
        using var weak = await admin.PutAsJsonAsync($"users/{id}/reset-password", new { type = "password", value = "short1", temporary = false }, Ct);
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Each_client_carries_only_its_own_systems_roles()
    {
        // Full scope allowed is off on every client: ume-rag-admin's roles mapper emits the rag-* roles in its scope
        // mappings and no gateway role; the gateway's client the reverse (identity design §16, "New (P1)").
        using var admin = await keycloak.AdminAsync();
        var user = await keycloak.IdOfAsync(admin, "users?exact=true&username=rag-admin-user");
        (await ExampleRolesAsync(admin, "ume-rag-admin", user)).ShouldBe(["rag-platform-admin", "rag-department-admin"], ignoreOrder: true);
        (await ExampleRolesAsync(admin, "llm-gateway-admin", user)).ShouldBe(["gateway-admin"]);
        (await ExampleRolesAsync(admin, "open-webui", user)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_editor_signs_in_through_the_bff_without_otp()
    {
        await using var factory = keycloak.Factory();
        using var app = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:5174"), AllowAutoRedirect = false });
        using var browser = new KeycloakBrowser(keycloak.Authority);

        var page = await browser.StartAsync(app);
        page = await browser.LoginAsync(page, "rag-editor-user", keycloak.UserPassword);
        await browser.FinishAsync(app, page);

        using var json = JsonDocument.Parse(await app.GetStringAsync("/bff/user", Ct));
        Strings(json.RootElement.GetProperty("roles")).ShouldBe(["rag-pipeline-editor"]);
        Strings(json.RootElement.GetProperty("departmentCodes")).ShouldBe(["1000"]);
    }

    [Theory]
    [InlineData("rag-admin-user", new[] { "rag-platform-admin", "rag-department-admin" })] // both admin roles: still one OTP step
    [InlineData("rag-department-admin-user", new[] { "rag-department-admin" })]
    public async Task An_admin_sets_up_otp_at_the_first_sign_in_and_enters_it_once_after(string user, string[] roles)
    {
        await using var factory = keycloak.Factory();

        // First sign-in: the password, then Keycloak's OTP set-up page (no OTP yet, so rag-browser makes it required).
        using (var app = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:5174"), AllowAutoRedirect = false }))
        using (var browser = new KeycloakBrowser(keycloak.Authority))
        {
            var page = await browser.StartAsync(app);
            page = await browser.LoginAsync(page, user, keycloak.UserPassword);
            page.Form.ShouldBe("kc-totp-settings-form", page.Body);
            var secret = page.Fields["totpSecret"];
            page = await browser.SubmitAsync(page, new() { ["totp"] = Totp(secret, 0), ["userLabel"] = "test" });
            await browser.FinishAsync(app, page);
            using var json = JsonDocument.Parse(await app.GetStringAsync("/bff/user", Ct));
            Strings(json.RootElement.GetProperty("roles")).ShouldBe(roles, ignoreOrder: true);

            // Second sign-in in a fresh browser: one OTP form, then back to the app.
            using var second = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:5174"), AllowAutoRedirect = false });
            using var fresh = new KeycloakBrowser(keycloak.Authority);
            page = await fresh.StartAsync(second);
            page = await fresh.LoginAsync(page, user, keycloak.UserPassword);
            page.Form.ShouldBe("kc-otp-login-form", page.Body);
            page = await fresh.SubmitAsync(page, new() { ["otp"] = Totp(secret, 1) }); // the next window: a used code is refused
            await fresh.FinishAsync(second, page);
        }
    }

    private async Task<string[]> ExampleRolesAsync(HttpClient admin, string clientId, string user)
    {
        var client = await keycloak.IdOfAsync(admin, $"clients?clientId={clientId}");
        using var token = JsonDocument.Parse(await admin.GetStringAsync($"clients/{client}/evaluate-scopes/generate-example-access-token?scope=openid&userId={user}", Ct));
        return token.RootElement.TryGetProperty("roles", out var roles) ? Strings(roles) : [];
    }

    /// <summary>RFC 6238 TOTP as Keycloak's default OTP policy (HmacSHA1, 6 digits, 30 s); the key is the raw secret.</summary>
    private static string Totp(string secret, int windowsAhead)
    {
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) + windowsAhead);
#pragma warning disable CA5350 // RFC 6238 and Keycloak's default OTP policy use HMAC-SHA1
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var code = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7fffffff) % 1_000_000;
        return code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>A Keycloak page: the form's id and action, its fields, the body.</summary>
    private sealed record KeycloakPage(string Form, string Action, Dictionary<string, string> Fields, string Body);

    /// <summary>
    /// A browser for the BFF's code flow at Keycloak: keeps Keycloak's <c>Secure</c> cookies over plain HTTP (as browsers
    /// do on localhost), follows redirects inside Keycloak and stops at the one back to the app or at a form.
    /// </summary>
    private sealed partial class KeycloakBrowser(string authority) : IDisposable
    {
        private readonly string _origin = new Uri(authority).GetLeftPart(UriPartial.Authority);
        private readonly Dictionary<string, string> _cookies = [];
        private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        private string? _leaving;

        public void Dispose() => _http.Dispose();

        public async Task<KeycloakPage> StartAsync(HttpClient app)
        {
            using var login = await app.GetAsync("/bff/login", Ct);
            login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            return await FollowAsync(new HttpRequestMessage(HttpMethod.Get, login.Headers.Location));
        }

        public Task<KeycloakPage> LoginAsync(KeycloakPage page, string user, string password)
        {
            page.Form.ShouldBe("kc-form-login", page.Body);
            return SubmitAsync(page, new() { ["username"] = user, ["password"] = password, ["credentialId"] = "" });
        }

        public Task<KeycloakPage> SubmitAsync(KeycloakPage page, Dictionary<string, string> values)
        {
            var fields = new Dictionary<string, string>(page.Fields);
            foreach (var (key, value) in values)
            {
                fields[key] = value;
            }

            return FollowAsync(new HttpRequestMessage(HttpMethod.Post, page.Action) { Content = new FormUrlEncodedContent(fields) });
        }

        /// <summary>Hands the authorization response (redirect or form post) to the app and expects its session.</summary>
        public async Task FinishAsync(HttpClient app, KeycloakPage page)
        {
            HttpResponseMessage signedIn;
            if (_leaving is { } location)
            {
                signedIn = await app.GetAsync(new Uri(location).PathAndQuery, Ct);
            }
            else
            {
                page.Form.ShouldBe("", page.Body); // Keycloak's form_post page has no id
                signedIn = await app.PostAsync(new Uri(page.Action).PathAndQuery, new FormUrlEncodedContent(page.Fields), Ct);
            }

            using (signedIn)
            {
                signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect, await signedIn.Content.ReadAsStringAsync(Ct));
                signedIn.Headers.Location!.ToString().ShouldBe("/");
            }
        }

        private async Task<KeycloakPage> FollowAsync(HttpRequestMessage request)
        {
            _leaving = null;
            while (true)
            {
                using var response = await SendAsync(request);
                request.Dispose();
                if (response.StatusCode == HttpStatusCode.Found && response.Headers.Location is { } location)
                {
                    var next = new Uri(new Uri(_origin), location);
                    if (next.GetLeftPart(UriPartial.Authority) != _origin)
                    {
                        _leaving = next.ToString();
                        return new KeycloakPage("", next.ToString(), [], "");
                    }

                    request = new HttpRequestMessage(HttpMethod.Get, next);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
                var form = FormTag().Match(body).Value;
                form.ShouldNotBeNullOrEmpty(body);
                var fields = new Dictionary<string, string>();
                foreach (var input in InputTag().Matches(body).Select(m => m.Value))
                {
                    var name = Attribute(input, "name");
                    if (name.Length > 0 && Attribute(input, "type").ToUpperInvariant() is "HIDDEN")
                    {
                        fields[name] = WebUtility.HtmlDecode(Attribute(input, "value"));
                    }
                }

                return new KeycloakPage(Attribute(form, "id"), WebUtility.HtmlDecode(Attribute(form, "action")), fields, body);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            var response = await _http.SendAsync(request, Ct);
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

        private static string Attribute(string tag, string name) =>
            Regex.Match(tag, $"""\s{name}="(?<value>[^"]*)""", RegexOptions.IgnoreCase).Groups["value"].Value;

        [GeneratedRegex("""<form[^>]*>""", RegexOptions.IgnoreCase)]
        private static partial Regex FormTag();

        [GeneratedRegex("""<input[^>]*>""", RegexOptions.IgnoreCase)]
        private static partial Regex InputTag();
    }
}
