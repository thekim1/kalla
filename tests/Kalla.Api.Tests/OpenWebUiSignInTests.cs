using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Kalla.Answering.Chat;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Security;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// M-20 S5 (identity design §19, "Open WebUI", "Stored secrets"): kalla-api's internal route that serves Open WebUI's sign-in settings
/// to the deployment's own Open WebUI client only, the version that follows every value, and kalla-api's read-only Data Protection,
/// which unprotects what kalla-admin's registration protected and creates no keys.
/// </summary>
public sealed class OpenWebUiSignInTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private const string Route = OpenWebUiSignInEndpoints.Path;
    private const string Secret = "owui-client-secret-3a9f71";
    private const string ProviderUrl = "https://idp.test/realms/fors/.well-known/openid-configuration";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (WebApplicationFactory<Program> Api, CapturingLoggerProvider Logs) Host(PolicySnapshot? snapshot = null, params (string Key, string Value)[] settings)
    {
        var logs = new CapturingLoggerProvider();
        var api = db.ApiHost(snapshot ?? db.Snapshot()).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            web.ConfigureTestServices(services =>
                services.UseGatewayCredentialCeiling().AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
            web.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        });
        return (api, logs);
    }

    private static HttpClient Client(WebApplicationFactory<Program> api, string? key)
    {
        var client = api.CreateHttpsClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    /// <summary>A host with only what Data Protection needs, as kalla-admin (<paramref name="readOnly"/> false) or kalla-api registers it.</summary>
    private static IHost DataProtectionHost(string connection, bool readOnly)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration["ConnectionStrings:ragdb"] = connection;
        builder.AddRagDatabase();
        builder.AddRagDataProtection(readOnly);
        builder.Services.AddSingleton<SettingsSecretProtector>();
        return builder.Build();
    }

    /// <summary>The wizard's values and secret in the settings row, the secret protected by kalla-admin's registration.</summary>
    private async Task StoreAsync(OpenWebUiOidcValues values, string? secret)
    {
        string? encrypted = null;
        if (secret is not null)
        {
            using var admin = DataProtectionHost(db.Connection(RagDatabase.Admin), readOnly: false);
            encrypted = admin.Services.GetRequiredService<SettingsSecretProtector>().Protect(StoredSecret.OpenWebUiOidcClientSecret, secret);
        }

        await StoreCiphertextAsync(values, encrypted);
    }

    private async Task StoreCiphertextAsync(OpenWebUiOidcValues values, string? encrypted)
    {
        await using var context = TestPostgres.Context(db.Connection(RagDatabase.Admin));
        await context.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            var row = await context.Settings.SingleOrDefaultAsync(ct);
            if (row is null)
            {
                row = new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" };
                context.Settings.Add(row);
            }

            row.OpenWebUiOidc = values.ToJson();
            row.OpenWebUiOidcEncryptedClientSecret = encrypted;
            row.OpenWebUiOidcClientSecretSetAt = encrypted is null ? null : DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(ct);
            return 0;
        }, Ct);
    }

    private static async Task<JsonElement> AnswerAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Route, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task The_deployments_Open_WebUI_client_gets_the_effective_values_and_nobody_else_does()
    {
        await StoreAsync(new OpenWebUiOidcValues { ProviderUrl = ProviderUrl, ClientId = "owui-wizard", ProviderName = "Chatten" }, Secret);
        var (api, logs) = Host();
        await using (api)
        {
            using var owui = Client(api, db.OwuiKey);
            var answer = await AnswerAsync(owui);
            answer.GetProperty("configured").GetBoolean().ShouldBeTrue();
            answer.GetProperty("provider_url").GetString().ShouldBe(ProviderUrl);
            answer.GetProperty("client_id").GetString().ShouldBe("owui-wizard");
            answer.GetProperty("client_secret").GetString().ShouldBe(Secret);
            answer.GetProperty("provider_name").GetString().ShouldBe("Chatten");
            answer.GetProperty("version").GetString()!.Length.ShouldBe(32);

            // Any other client, a missing key and a wrong one are refused, each with its 5001.
            foreach (var key in new[] { db.IntegrationKey, db.InternalKey })
            {
                using var other = Client(api, key);
                using var refused = await other.GetAsync(Route, Ct);
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await CodeOf(refused)).ShouldBe(DenyReasons.ClientNotAllowed);
                (await refused.Content.ReadAsStringAsync(Ct)).ShouldNotContain(Secret);
            }

            using (var anonymous = Client(api, null))
            using (var missing = await anonymous.GetAsync(Route, Ct))
            {
                missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            using (var wrong = Client(api, "kalla-sk-" + new string('x', 43)))
            using (var invalid = await wrong.GetAsync(Route, Ct))
            {
                invalid.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            api.Services.GetRequiredService<KnowledgeSecurity>().FlushAuthFailures();
        }

        logs.Entries.Where(e => e.EventId.Id == 5001).Select(e => e.Message).ShouldContain(m => m.Contains(DenyReasons.ClientNotAllowed));
        logs.Text.ShouldNotContain(Secret);

        // An Open WebUI client the deployment does not manage is just another client.
        var snapshot = db.Snapshot();
        snapshot = snapshot with
        {
            Clients = snapshot.Clients.ToDictionary(c => c.Key, c => c.Key == db.OwuiClient ? c.Value with { IsManagedByDeployment = false } : c.Value),
        };
        var (unmanaged, _) = Host(snapshot);
        await using (unmanaged)
        {
            using var owui = Client(unmanaged, db.OwuiKey);
            using var refused = await owui.GetAsync(Route, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Deployment_values_win_and_defaults_fill_the_rest_from_rag_admins_authority()
    {
        await StoreAsync(new OpenWebUiOidcValues { ClientId = "owui-wizard" }, Secret);
        var (api, logs) = Host(settings: [("Oidc:Authority", "https://idp.test/realms/fors"), ("OpenWebUi:Oidc:ClientSecret", "deployed-owui-secret")]);
        await using (api)
        {
            using var owui = Client(api, db.OwuiKey);
            var answer = await AnswerAsync(owui);
            (answer.GetProperty("provider_url").GetString(), answer.GetProperty("client_id").GetString(), answer.GetProperty("client_secret").GetString(),
                answer.GetProperty("provider_name").GetString())
                .ShouldBe((ProviderUrl, "owui-wizard", "deployed-owui-secret", OpenWebUiSignIn.DefaultProviderName));
        }

        logs.Text.ShouldNotContain("deployed-owui-secret");
    }

    [Fact]
    public async Task Without_a_provider_address_or_with_an_unreadable_secret_it_is_not_configured()
    {
        await StoreAsync(new OpenWebUiOidcValues(), null);
        var (api, logs) = Host();
        await using (api)
        {
            using var owui = Client(api, db.OwuiKey);
            var answer = await AnswerAsync(owui);
            answer.GetProperty("configured").GetBoolean().ShouldBeFalse();
            foreach (var name in new[] { "provider_url", "client_id", "client_secret", "provider_name" })
            {
                answer.GetProperty(name).ValueKind.ShouldBe(JsonValueKind.Null, name);
            }

            // A secret the key ring cannot read (protected by another ring, as a replaced certificate leaves it): not configured, a log line.
            var foreign = new SettingsSecretProtector(new EphemeralDataProtectionProvider(), api.Services.GetRequiredService<ILogger<SettingsSecretProtector>>())
                .Protect(StoredSecret.OpenWebUiOidcClientSecret, Secret);
            await StoreCiphertextAsync(new OpenWebUiOidcValues { ProviderUrl = ProviderUrl }, foreign);
            var unreadable = await AnswerAsync(owui);
            unreadable.GetProperty("configured").GetBoolean().ShouldBeFalse();
            unreadable.GetProperty("client_secret").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Open WebUI's stored client secret cannot be read"));
        logs.Text.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task The_version_follows_every_value_and_the_secret_through_the_route()
    {
        var (api, _) = Host();
        await using (api)
        {
            using var owui = Client(api, db.OwuiKey);
            var values = new OpenWebUiOidcValues { ProviderUrl = ProviderUrl, ClientId = "c1", ProviderName = "N1" };
            await StoreAsync(values, Secret);
            var first = (await AnswerAsync(owui)).GetProperty("version").GetString();
            (await AnswerAsync(owui)).GetProperty("version").GetString().ShouldBe(first); // stable while nothing changes
            await StoreAsync(values, Secret + "-rotated");
            var rotated = (await AnswerAsync(owui)).GetProperty("version").GetString();
            rotated.ShouldNotBe(first);
            await StoreAsync(values with { ProviderName = "N2" }, Secret + "-rotated");
            (await AnswerAsync(owui)).GetProperty("version").GetString().ShouldNotBe(rotated);
        }
    }

    [Fact]
    public void The_version_is_a_keyed_hash_of_every_value()
    {
        var key = Encoding.UTF8.GetBytes(new string('c', 32));
        EffectiveOpenWebUiSignIn Of(string? url = ProviderUrl, string id = "c", string name = "N", string? secret = Secret) => new(
            new OpenWebUiSignInValue(url, SignInSources.Wizard), new OpenWebUiSignInValue(id, SignInSources.Wizard), new OpenWebUiSignInValue(name, SignInSources.Wizard),
            new OpenWebUiClientSecret(SignInSources.Wizard, secret, null, unreadable: false), null, ProviderUrlFromDeployment: false);
        var version = OpenWebUiSignIn.Version(Of(), key);
        string[] changed =
        [
            OpenWebUiSignIn.Version(Of(url: ProviderUrl + "x"), key), OpenWebUiSignIn.Version(Of(id: "d"), key), OpenWebUiSignIn.Version(Of(name: "M"), key),
            OpenWebUiSignIn.Version(Of(secret: Secret + "x"), key), OpenWebUiSignIn.Version(Of(secret: null), key), OpenWebUiSignIn.Version(null, key),
            // Length-prefixed: moving characters between values is a change too.
            OpenWebUiSignIn.Version(Of(id: "cN", name: ""), key),
        ];
        changed.ShouldAllBe(v => v != version);
        changed.Distinct().Count().ShouldBe(changed.Length);
        OpenWebUiSignIn.Version(Of(), key).ShouldBe(version);

        // Keyed: another deployment's key gives another version, and it is no plain hash of the secret.
        OpenWebUiSignIn.Version(Of(), Encoding.UTF8.GetBytes(new string('d', 32))).ShouldNotBe(version);
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Secret))).ShouldNotStartWith(version);
    }

    [Fact]
    public async Task The_route_is_limited_per_key_and_is_not_in_the_api_reference()
    {
        await StoreAsync(new OpenWebUiOidcValues { ProviderUrl = ProviderUrl }, null);
        var (api, _) = Host(settings: ("Chat:MessagesPerMinutePerUser", "2"));
        await using (api)
        {
            using var owui = Client(api, db.OwuiKey);
            await AnswerAsync(owui);
            await AnswerAsync(owui);
            using var limited = await owui.GetAsync(Route, Ct);
            limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await CodeOf(limited)).ShouldBe("rate_limited");

            using var anonymous = Client(api, null);
            var document = await anonymous.GetStringAsync("/openapi/knowledge.json", Ct);
            document.ShouldNotContain("/internal");
        }
    }

    [Fact]
    public async Task Rag_apis_data_protection_reads_what_rag_admins_protected_and_creates_no_keys()
    {
        // kalla-admin's registration protects (and makes the ring's first key); kalla-api's, on its own role, unprotects it.
        string ciphertext;
        using (var admin = DataProtectionHost(db.Connection(RagDatabase.Admin), readOnly: false))
        {
            ciphertext = admin.Services.GetRequiredService<SettingsSecretProtector>().Protect(StoredSecret.OpenWebUiOidcClientSecret, Secret);
        }

        using (var api = DataProtectionHost(db.Connection(RagDatabase.Api), readOnly: true))
        {
            var protector = api.Services.GetRequiredService<SettingsSecretProtector>();
            var read = protector.Unprotect(StoredSecret.OpenWebUiOidcClientSecret, ciphertext);
            (read.State, read.Value).ShouldBe((StoredSecretState.Readable, Secret));
            // The purposes keep one secret's ciphertext from being read as another's.
            protector.Unprotect(StoredSecret.IdentityOidcClientSecret, ciphertext).State.ShouldBe(StoredSecretState.Unreadable);
            api.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys.ShouldBeFalse();
        }

        // The real kalla-api registers it read-only too.
        var (host, _) = Host();
        await using (host)
        {
            host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys.ShouldBeFalse();
        }

        // On an empty ring kalla-api can protect nothing and writes no key.
        await using var empty = new RagDatabase();
        await empty.InitializeAsync();
        using (var api = DataProtectionHost(empty.Connection(RagDatabase.Api), readOnly: true))
        {
            Should.Throw<CryptographicException>(() => api.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x"));
        }

        await using var context = TestPostgres.Context(empty.Connection(RagDatabase.Admin));
        (await context.DataProtectionKeys.CountAsync(Ct)).ShouldBe(0);
    }
}
