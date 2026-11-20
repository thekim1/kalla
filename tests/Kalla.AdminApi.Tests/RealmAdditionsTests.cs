extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kalla.Domain.Entities;
using Kalla.TestKit;
using FakeCatalog = fakegateway::Kalla.FakeGateway.FakeCatalog;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// A Keycloak container (<see cref="KeycloakFixture.Image"/>, the shared Keycloak's 26.8.1) and keycloak-config-cli
/// runs against it, as the gateway's <c>deploy/keycloak</c> project applies realm files (identity design §11.2,
/// thekim1/fors#70): the same image and digest, sign-in as the bootstrap service account
/// <c>fors-realm-import</c> by client credentials with its secret from a Spring config tree, <c>realm/*.json</c> then
/// <c>additions/*.json</c> in name order, <c>$(env:...)</c> substitution, and every resource type <c>no-delete</c>.
/// The placeholder values are the job's environment, as <c>FORS_KEYCLOAK_IMPORT_ENV</c>'s file makes them.
/// </summary>
public sealed class RealmImport(IReadOnlyList<string> realmFiles, IReadOnlyList<string> additionFiles, IReadOnlyDictionary<string, string> values)
    : IAsyncDisposable
{
    /// <summary>The gateway's <c>realm-import</c> image (deploy/keycloak/compose.yaml), pinned by the same digest.</summary>
    public const string ConfigCliImage =
        "docker.io/adorsys/keycloak-config-cli:6.5.1-26.5.5@sha256:0955d98c8a341898b7aa177477edf8a1e90569ae50bbe7598141c1270b773274";

    /// <summary>The gateway's import settings: a file adds and updates, never deletes (deploy/keycloak/compose.yaml).</summary>
    public static readonly IReadOnlyDictionary<string, string> ImportSettings = new Dictionary<string, string>
    {
        ["IMPORT_VARSUBSTITUTION_ENABLED"] = "true",
        ["IMPORT_FILES_LOCATIONS"] = "file:/config/realm/*.json,file:/config/additions/*.json",
        ["IMPORT_MANAGED_CLIENT"] = "no-delete",
        ["IMPORT_MANAGED_ROLE"] = "no-delete",
        ["IMPORT_MANAGED_CLIENTSCOPE"] = "no-delete",
        ["IMPORT_MANAGED_AUTHENTICATIONFLOW"] = "no-delete",
        ["IMPORT_MANAGED_IDENTITYPROVIDER"] = "no-delete",
        ["IMPORT_MANAGED_IDENTITYPROVIDERMAPPER"] = "no-delete",
        ["IMPORT_MANAGED_GROUP"] = "no-delete",
        ["IMPORT_MANAGED_COMPONENT"] = "no-delete",
        ["IMPORT_MANAGED_SUBCOMPONENT"] = "no-delete",
        ["IMPORT_MANAGED_REQUIREDACTION"] = "no-delete",
        ["IMPORT_MANAGED_CLIENTAUTHORIZATIONRESOURCES"] = "no-delete",
        ["IMPORT_MANAGED_SCOPEMAPPING"] = "no-delete",
        ["IMPORT_MANAGED_CLIENTSCOPEMAPPING"] = "no-delete",
    };

    private const int Port = 8080;
    private const string ImportClientId = "fors-realm-import";
    private readonly string _adminPassword = TestSecrets.RandomHex(16);
    private readonly string _importSecret = TestSecrets.RandomHex();
    private readonly INetwork _network = new NetworkBuilder().Build();
    private IContainer? _keycloak;

    public string BaseUrl => $"http://{_keycloak!.Hostname}:{_keycloak.GetMappedPublicPort(Port)}";
    public string Authority => $"{BaseUrl}/realms/fors";

    public async Task StartAsync()
    {
        await _network.CreateAsync();
        _keycloak = new ContainerBuilder(KeycloakFixture.Image)
            .WithNetwork(_network)
            .WithNetworkAliases("keycloak")
            .WithCommand("start-dev")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", _adminPassword)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_CLIENT_ID", ImportClientId)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_CLIENT_SECRET", _importSecret)
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(Port).ForPath("/realms/master/.well-known/openid-configuration"),
                wait => wait.WithTimeout(TimeSpan.FromMinutes(5))))
            .Build();
        await _keycloak.StartAsync();
    }

    /// <summary>One run of the import job (<c>docker compose run --rm realm-import</c>); <paramref name="overrides"/> changes its settings.</summary>
    public async Task<(long ExitCode, string Output)> RunAsync(IReadOnlyDictionary<string, string>? overrides = null)
    {
        var builder = new ContainerBuilder(ConfigCliImage)
            .WithNetwork(_network)
            .WithEnvironment("KEYCLOAK_URL", $"http://keycloak:{Port}")
            .WithEnvironment("KEYCLOAK_GRANTTYPE", "client_credentials")
            .WithEnvironment("KEYCLOAK_CLIENTID", ImportClientId)
            .WithEnvironment("KEYCLOAK_AVAILABILITYCHECK_ENABLED", "true")
            .WithEnvironment("KEYCLOAK_AVAILABILITYCHECK_TIMEOUT", "120s")
            .WithEnvironment("SPRING_CONFIG_IMPORT", "configtree:/run/import-secrets/config/")
            .WithResourceMapping(Encoding.UTF8.GetBytes(_importSecret), "/run/import-secrets/config/keycloak.client-secret");
        foreach (var (key, value) in ImportSettings.Concat(values))
        {
            builder = builder.WithEnvironment(key, overrides?.GetValueOrDefault(key) ?? value);
        }

        foreach (var file in realmFiles)
        {
            builder = builder.WithResourceMapping(new FileInfo(TestPostgres.RepositoryFile(file)), "/config/realm/");
        }

        foreach (var file in additionFiles)
        {
            builder = builder.WithResourceMapping(new FileInfo(TestPostgres.RepositoryFile(file)), "/config/additions/");
        }

        await using var job = builder.Build();
        await job.StartAsync();
        var exitCode = await job.GetExitCodeAsync();
        var (stdout, stderr) = await job.GetLogsAsync();
        return (exitCode, stdout + stderr);
    }

    /// <summary>An admin REST client for realm <c>fors</c> (the bootstrap admin of <c>master</c>).</summary>
    public async Task<HttpClient> AdminAsync()
    {
        using var anonymous = new HttpClient();
        using var response = await anonymous.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = "admin", ["password"] = _adminPassword,
        }));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var admin = new HttpClient { BaseAddress = new Uri($"{BaseUrl}/admin/realms/fors/") };
        admin.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("access_token").GetString());
        return admin;
    }

    public static async Task<string> IdOfAsync(HttpClient admin, string path)
    {
        using var json = JsonDocument.Parse(await admin.GetStringAsync(path));
        return json.RootElement.EnumerateArray().Single().GetProperty("id").GetString()!;
    }

    /// <summary>Creates a user by hand, as the pilot accounts are (the files hold no users), and returns its id.</summary>
    public async Task<string> CreateUserAsync(string user, string password, string[] roles, Dictionary<string, string[]> attributes)
    {
        using var admin = await AdminAsync();
        using var created = await admin.PostAsJsonAsync("users", new
        {
            username = user, email = $"{user}@example.invalid", emailVerified = true, firstName = "Test", lastName = "User", enabled = true,
            attributes, credentials = new[] { new { type = "password", value = password, temporary = false } },
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
        return id;
    }

    /// <summary>Adds the user to a group by name (the operator's shared groups, #468).</summary>
    public async Task JoinGroupAsync(string userId, string group)
    {
        using var admin = await AdminAsync();
        var id = await IdOfAsync(admin, $"groups?exact=true&search={Uri.EscapeDataString(group)}");
        using var joined = await admin.PutAsync($"users/{userId}/groups/{id}", null);
        joined.StatusCode.ShouldBe(HttpStatusCode.NoContent, await joined.Content.ReadAsStringAsync());
    }

    /// <summary>The access token Keycloak would issue to <paramref name="clientId"/> for the user (the admin console's evaluate tab).</summary>
    public static async Task<JsonDocument> ExampleTokenAsync(HttpClient admin, string clientId, string userId)
    {
        var client = await IdOfAsync(admin, $"clients?clientId={clientId}");
        return JsonDocument.Parse(await admin.GetStringAsync($"clients/{client}/evaluate-scopes/generate-example-access-token?scope=openid&userId={userId}"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_keycloak is not null)
        {
            await _keycloak.DisposeAsync();
        }

        await _network.DisposeAsync();
    }
}

/// <summary>
/// RAG's additions (<c>deploy/keycloak/rag-additions.json</c>, identity design §11.2) imported with keycloak-config-cli
/// after a minimal stand-in for the gateway's <c>realm/10-gateway.json</c> (realm-wide settings, the user profile with
/// <c>departmentCodes</c> and <c>teamCodes</c>, the client <c>fors-admin</c> and two gateway roles). This is a test
/// realm, so <c>rag-additions.local.json</c> follows (the BFF below signs in on https://localhost:5174). Then the test
/// accounts are created by hand.
/// </summary>
public sealed class RealmAdditionsFixture : IAsyncLifetime
{
    // The operator's site file of shared groups (#468) goes last: it names realm roles of both systems, so it sorts after
    // rag-additions*.json, as deploy/keycloak/README.md says.
    public static readonly string[] Additions =
    [
        Path.Combine("deploy", "keycloak", "rag-additions.json"), Path.Combine("deploy", "keycloak", "rag-additions.local.json"),
        Path.Combine("deploy", "keycloak", "examples", "site-groups.json"),
    ];

    public RealmAdditionsFixture()
    {
        Import = new RealmImport([Path.Combine("tests", "Kalla.AdminApi.Tests", "Keycloak", "10-gateway.json")], Additions, new Dictionary<string, string>
        {
            ["GATEWAY_CLIENT_SECRET"] = TestSecrets.RandomHex(),
            ["FORS_KEYCLOAK_REALM"] = "fors",
            ["KALLA_ADMIN_PUBLIC_HOST"] = "kalla-admin.example.invalid",
            ["KALLA_OPENWEBUI_HOST"] = "chat.example.invalid",
            ["KALLA_OIDC_CLIENT_SECRET"] = OidcClientSecret,
            ["KALLA_OPENWEBUI_OIDC_CLIENT_SECRET"] = TestSecrets.RandomHex(),
        });
    }

    public RealmImport Import { get; }
    public RagDatabase Database { get; } = new();
    public string OidcClientSecret { get; } = TestSecrets.RandomHex();

    /// <summary>Meets the base realm's password policy (12 characters or more, not the user name).</summary>
    public string UserPassword { get; } = TestSecrets.RandomHex(16);

    /// <summary>The output of the first import.</summary>
    public (long ExitCode, string Output) FirstImport { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Database.InitializeAsync().AsTask(), Import.StartAsync());
        await using (var db = TestPostgres.Context(Database.Connection(RagDatabase.Admin)))
        {
            db.Departments.AddRange(FakeCatalog.Departments.Select(d => new Department
            {
                Id = Guid.CreateVersion7(), Name = d.Name, CostCenterCode = d.CostCenterCode, MemberGroups = d.MemberGroups, IsActive = d.IsActive,
                SyncedAt = DateTimeOffset.UtcNow,
            }));
            await db.SaveChangesAsync();
        }

        FirstImport = await Import.RunAsync();
        if (FirstImport.ExitCode != 0)
        {
            throw new InvalidOperationException("keycloak-config-cli failed:\n" + FirstImport.Output);
        }

        var departments = new Dictionary<string, string[]> { ["departmentCodes"] = ["1000"] };
        await Import.CreateUserAsync("rag-editor-user", UserPassword, ["rag-pipeline-editor"], departments);
        await Import.CreateUserAsync("rag-admin-user", UserPassword, ["rag-platform-admin", "rag-department-admin", "gateway-admin"], departments);
        await Import.CreateUserAsync("rag-department-admin-user", UserPassword, ["rag-department-admin"], departments);
        await Import.JoinGroupAsync(await Import.CreateUserAsync("shared-admin-user", UserPassword, [], departments), "fors-admins");
    }

    public async ValueTask DisposeAsync()
    {
        await Import.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>The admin API against the realm on the admin UI's local origin (a redirect URI of the local file).</summary>
    public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("ConnectionStrings:ragdb", Database.Connection(RagDatabase.Admin));
        web.UseSetting("Oidc:Authority", Import.Authority);
        web.UseSetting("Oidc:ClientSecret", OidcClientSecret);
    });
}

/// <summary>
/// B-15 done-when (#390, #215): the additions import with keycloak-config-cli after a base realm in a 26.8.1 container,
/// a re-run deletes nothing, each client's token carries only its own system's roles, and no admin is asked for OTP
/// (Decisions "Gateway release 1 and shared Keycloak as built", identity design §11.2).
/// </summary>
public sealed partial class RealmAdditionsTests(RealmAdditionsFixture keycloak) : IClassFixture<RealmAdditionsFixture>
{
    private static readonly string[] RagClients = ["kalla-admin", "open-webui", "kalla-api"];
    private static readonly string[] RagRoles =
        ["rag-platform-admin", "rag-department-admin", "rag-pipeline-editor", "rag-reviewer", "rag-skill-editor", "rag-editor", "rag-viewer"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_additions_import_after_the_base_realm_and_leave_its_settings_alone()
    {
        keycloak.FirstImport.ExitCode.ShouldBe(0, keycloak.FirstImport.Output);

        using var admin = await keycloak.Import.AdminAsync();
        using var clients = JsonDocument.Parse(await admin.GetStringAsync("clients", Ct));
        var all = clients.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("clientId").GetString()!, c => c.Clone());
        foreach (var clientId in RagClients.Append("fors-admin"))
        {
            all.ShouldContainKey(clientId);
            all[clientId].GetProperty("fullScopeAllowed").GetBoolean().ShouldBeFalse(clientId);
        }

        // The local file (a test realm only) adds the AppHost URIs and keeps the pilot's.
        Strings(all["kalla-admin"].GetProperty("redirectUris")).ShouldBe(
            ["https://kalla-admin.example.invalid/signin-oidc", "https://localhost:5174/signin-oidc", "https://localhost:7402/signin-oidc"], ignoreOrder: true);
        Strings(all["open-webui"].GetProperty("redirectUris")).ShouldBe(
            ["https://chat.example.invalid/oauth/oidc/login/callback", "http://localhost:8080/oauth/oidc/login/callback"], ignoreOrder: true);

        using var roles = JsonDocument.Parse(await admin.GetStringAsync("roles", Ct));
        RagRoles.Append("gateway-admin").ShouldBeSubsetOf(roles.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()!));

        // The base realm's settings: the additions declare none, and no OTP is enforced (#390 Q3, Q4).
        using var realm = JsonDocument.Parse(await admin.GetStringAsync("", Ct));
        realm.RootElement.GetProperty("passwordPolicy").GetString()!.ShouldStartWith("length(12)");
        realm.RootElement.GetProperty("bruteForceProtected").GetBoolean().ShouldBeTrue();
        realm.RootElement.GetProperty("browserFlow").GetString().ShouldBe("browser");
        using var profile = JsonDocument.Parse(await admin.GetStringAsync("users/profile", Ct));
        new[] { "username", "email", "departmentCodes", "teamCodes" }.ShouldBeSubsetOf(profile.RootElement.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()!));
        using var totp = JsonDocument.Parse(await admin.GetStringAsync("authentication/required-actions/CONFIGURE_TOTP", Ct));
        totp.RootElement.GetProperty("defaultAction").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_import_deletes_nothing()
    {
        using (var before = await keycloak.Import.AdminAsync())
        {
            (await before.PostAsJsonAsync("clients", new { clientId = "made-by-hand", enabled = true }, Ct)).EnsureSuccessStatusCode();
            (await before.PostAsJsonAsync("roles", new { name = "made-by-hand-role" }, Ct)).EnsureSuccessStatusCode();
        }

        var again = await keycloak.Import.RunAsync();
        again.ExitCode.ShouldBe(0, again.Output);

        using var admin = await keycloak.Import.AdminAsync(); // a fresh token: master's access tokens live 60 s

        foreach (var clientId in RagClients.Append("fors-admin").Append("made-by-hand"))
        {
            using var found = JsonDocument.Parse(await admin.GetStringAsync($"clients?clientId={clientId}", Ct));
            found.RootElement.GetArrayLength().ShouldBe(1, clientId);
        }

        using var role = await admin.GetAsync("roles/made-by-hand-role", Ct);
        role.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var users = JsonDocument.Parse(await admin.GetStringAsync("users?briefRepresentation=true", Ct));
        users.RootElement.GetArrayLength().ShouldBe(4);
        // The operator's shared group and its membership survive the re-run too (groups are no-delete, #468).
        var shared = await RealmImport.IdOfAsync(admin, "users?exact=true&username=shared-admin-user");
        using (var groups = JsonDocument.Parse(await admin.GetStringAsync($"users/{shared}/groups", Ct)))
        {
            groups.RootElement.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ShouldContain("fors-admins");
        }

        var user = await RealmImport.IdOfAsync(admin, "users?exact=true&username=rag-admin-user");
        using var mappings = JsonDocument.Parse(await admin.GetStringAsync($"users/{user}/role-mappings/realm", Ct));
        new[] { "rag-platform-admin", "rag-department-admin", "gateway-admin" }.ShouldBeSubsetOf(mappings.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()!));
    }

    [Fact]
    public async Task One_shared_group_gives_each_system_its_own_role_and_no_other()
    {
        // Decisions "Shared sign-in with the gateway" (#468): the operator's group fors-admins (examples/site-groups.json)
        // maps gateway-admin and rag-platform-admin. Its member has no direct role, and each client's token carries only
        // its own system's role, because full scope allowed is off and each client maps only its own roles.
        using var admin = await keycloak.Import.AdminAsync();
        var user = await RealmImport.IdOfAsync(admin, "users?exact=true&username=shared-admin-user");

        using (var rag = await RealmImport.ExampleTokenAsync(admin, "kalla-admin", user))
        {
            Claim(rag, "roles").ShouldBe(["rag-platform-admin"]);
        }

        using var gateway = await RealmImport.ExampleTokenAsync(admin, "fors-admin", user);
        Claim(gateway, "roles").ShouldBe(["gateway-admin"]);
    }

    [Fact]
    public async Task Each_client_carries_only_its_own_systems_roles()
    {
        // Full scope allowed is off on every client: kalla-admin's roles claim carries the rag-* roles in its scope
        // mappings and no gateway role; the gateway's client the reverse. Both carry departmentCodes (identity design §11.2).
        using var admin = await keycloak.Import.AdminAsync();
        var user = await RealmImport.IdOfAsync(admin, "users?exact=true&username=rag-admin-user");

        using (var rag = await RealmImport.ExampleTokenAsync(admin, "kalla-admin", user))
        {
            Claim(rag, "roles").ShouldBe(["rag-platform-admin", "rag-department-admin"], ignoreOrder: true);
            Claim(rag, "departmentCodes").ShouldBe(["1000"]);
        }

        using (var gateway = await RealmImport.ExampleTokenAsync(admin, "fors-admin", user))
        {
            Claim(gateway, "roles").ShouldBe(["gateway-admin"]);
            Claim(gateway, "departmentCodes").ShouldBe(["1000"]);
        }

        using var openWebUi = await RealmImport.ExampleTokenAsync(admin, "open-webui", user);
        Claim(openWebUi, "roles").ShouldBeEmpty();
    }

    [Fact]
    public void The_additions_hold_only_rags_part_of_the_realm()
    {
        // An additions file must not declare userProfile or realm settings (config-cli would replace the gateway's), and its
        // placeholders are config-cli's: a Keycloak-style ${NAME} would be imported as literal text.
        Dictionary<string, string[]> allowed = new()
        {
            ["rag-additions.json"] = ["realm", "roles", "clients", "scopeMappings"],
            ["rag-additions.local.json"] = ["realm", "clients"],
        };
        foreach (var (file, keys) in allowed)
        {
            var text = File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("deploy", "keycloak", file)));
            using var json = JsonDocument.Parse(text);
            json.RootElement.GetProperty("realm").GetString().ShouldBe("$(env:FORS_KEYCLOAK_REALM:-fors)", file);
            json.RootElement.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(keys, file);
            text.ShouldNotContain("${", Case.Sensitive, file);
            PlaceholderName().Matches(text).Select(m => m.Groups[1].Value).Distinct().ShouldBeSubsetOf(
                ["FORS_KEYCLOAK_REALM", "KALLA_ADMIN_PUBLIC_HOST", "KALLA_OPENWEBUI_HOST", "KALLA_OIDC_CLIENT_SECRET", "KALLA_OPENWEBUI_OIDC_CLIENT_SECRET"], file);
        }

        // #207 review: the local AppHost URIs are only in rag-additions.local.json, which never goes to production.
        foreach (var file in new[] { "rag-additions.json", Path.Combine("standalone", "10-rag-realm.json") })
        {
            File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("deploy", "keycloak", file))).ShouldNotContain("localhost", Case.Insensitive, file);
        }

        File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("deploy", "keycloak", "rag-additions.local.json"))).ShouldContain("https://localhost:5174/signin-oidc");
    }

    [Fact]
    public void The_open_webui_client_describes_the_callback_it_redirects_to()
    {
        // B-23: Open WebUI moved to /oauth/oidc/login/callback at v0.12.5 (identity design 5.3); the description followed.
        using var json = JsonDocument.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("deploy", "keycloak", "rag-additions.json"))));
        var client = json.RootElement.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == "open-webui");
        var redirect = new Uri(client.GetProperty("redirectUris").EnumerateArray().Single().GetString()!.Replace("$(env:KALLA_OPENWEBUI_HOST)", "owui.test", StringComparison.Ordinal));
        var description = client.GetProperty("description").GetString()!;
        description.ShouldContain(redirect.AbsolutePath);
        description.ShouldNotContain("legacy", Case.Insensitive);
    }

    [Theory]
    [InlineData("rag-admin-user", new[] { "rag-platform-admin", "rag-department-admin" })]
    [InlineData("rag-department-admin-user", new[] { "rag-department-admin" })]
    [InlineData("rag-editor-user", new[] { "rag-pipeline-editor" })]
    public async Task Users_sign_in_through_the_bff_with_the_password_alone(string user, string[] roles)
    {
        // #390 Q4: no OTP for the pilot, admins included. After the password Keycloak goes straight back to the app.
        await using var factory = keycloak.Factory();
        using var app = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:5174"), AllowAutoRedirect = false });
        using var browser = new KeycloakBrowser(keycloak.Import.Authority);

        var page = await browser.StartAsync(app);
        page = await browser.LoginAsync(page, user, keycloak.UserPassword);
        page.Form.ShouldNotBe("kc-totp-settings-form", page.Body);
        page.Form.ShouldNotBe("kc-otp-login-form", page.Body);
        await browser.FinishAsync(app, page);

        using var json = JsonDocument.Parse(await app.GetStringAsync("/bff/user", Ct));
        Strings(json.RootElement.GetProperty("roles")).ShouldBe(roles, ignoreOrder: true);
        Strings(json.RootElement.GetProperty("departmentCodes")).ShouldBe(["1000"]);
    }

    private static string[] Claim(JsonDocument token, string name) =>
        token.RootElement.TryGetProperty(name, out var value) ? Strings(value) : [];

    [GeneratedRegex(@"\$\(env:([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PlaceholderName();

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
