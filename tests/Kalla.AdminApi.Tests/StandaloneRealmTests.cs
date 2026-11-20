using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The standalone variant (Decisions "Identity providers: AD and Entra ID through Keycloak, Källa standalone later"):
/// <c>deploy/keycloak/standalone/10-rag-realm.json</c> in place of the gateway's base file, then the same
/// <c>rag-additions.json</c>, imported with keycloak-config-cli into an empty 26.8.1 Keycloak.
/// </summary>
public sealed class StandaloneRealmFixture : IAsyncLifetime
{
    public StandaloneRealmFixture()
    {
        Import = new RealmImport(
            [Path.Combine("deploy", "keycloak", "standalone", "10-rag-realm.json")],
            [Path.Combine("deploy", "keycloak", "rag-additions.json")],
            new Dictionary<string, string>
            {
                ["KALLA_ADMIN_PUBLIC_HOST"] = "rag-admin.example.invalid",
                ["KALLA_OPENWEBUI_HOST"] = "chat.example.invalid",
                ["KALLA_OIDC_CLIENT_SECRET"] = TestSecrets.RandomHex(),
                ["KALLA_OPENWEBUI_OIDC_CLIENT_SECRET"] = TestSecrets.RandomHex(),
            });
    }

    public RealmImport Import { get; }

    public (long ExitCode, string Output) FirstImport { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await Import.StartAsync();
        FirstImport = await Import.RunAsync();
    }

    public async ValueTask DisposeAsync() => await Import.DisposeAsync();
}

public sealed class StandaloneRealmTests(StandaloneRealmFixture keycloak) : IClassFixture<StandaloneRealmFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_standalone_realm_file_and_the_additions_make_a_complete_realm_without_otp()
    {
        keycloak.FirstImport.ExitCode.ShouldBe(0, keycloak.FirstImport.Output);

        using var admin = await keycloak.Import.AdminAsync();
        using (var realm = JsonDocument.Parse(await admin.GetStringAsync("", Ct)))
        {
            realm.RootElement.GetProperty("passwordPolicy").GetString().ShouldBe("length(12) and maxLength(128) and notUsername and notEmail and passwordHistory(5)");
            realm.RootElement.GetProperty("bruteForceProtected").GetBoolean().ShouldBeTrue();
            realm.RootElement.GetProperty("failureFactor").GetInt32().ShouldBe(10);
            realm.RootElement.GetProperty("registrationAllowed").GetBoolean().ShouldBeFalse();
            realm.RootElement.GetProperty("browserFlow").GetString().ShouldBe("browser");
        }

        using (var profile = JsonDocument.Parse(await admin.GetStringAsync("users/profile", Ct)))
        {
            new[] { "username", "email", "departmentCodes", "teamCodes" }.ShouldBeSubsetOf(profile.RootElement.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()!));
        }

        var password = TestSecrets.RandomHex(16);
        var user = await keycloak.Import.CreateUserAsync("rag-admin-user", password, ["rag-platform-admin"],
            new() { ["departmentCodes"] = ["1000"], ["teamCodes"] = ["T1"] });
        using (var token = await RealmImport.ExampleTokenAsync(admin, "kalla-admin", user))
        {
            Strings(token.RootElement.GetProperty("roles")).ShouldBe(["rag-platform-admin"]);
            Strings(token.RootElement.GetProperty("departmentCodes")).ShouldBe(["1000"]);
            Strings(token.RootElement.GetProperty("teamCodes")).ShouldBe(["T1"]);
        }

        using var weak = await admin.PutAsJsonAsync($"users/{user}/reset-password", new { type = "password", value = "short1", temporary = false }, Ct);
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var again = await keycloak.Import.RunAsync();
        again.ExitCode.ShouldBe(0, again.Output);
        using var after = await keycloak.Import.AdminAsync(); // a fresh token: master's access tokens live 60 s
        using var users = JsonDocument.Parse(await after.GetStringAsync("users?briefRepresentation=true", Ct));
        users.RootElement.GetArrayLength().ShouldBe(1);
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
