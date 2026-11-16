extern alias fakegateway;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
using static Ume.Rag.AdminApi.Tests.SetupHosts;
using FakeGatewayOptions = fakegateway::Ume.Rag.FakeGateway.FakeGatewayOptions;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// M-20 S8 (identity design §19): the setup state offers the gateway's provider address (its context's <c>oidc_authority</c>, feature
/// <c>oidc-authority</c>) as a suggestion for rag-admin's authority, with the gateway admin's address for the link to its wizard; the
/// effective sign-in values and their sources do not change by it. rag-admin's context is read from a FakeGateway.
/// </summary>
[Collection(AdminDatabaseGroup8.Name)]
public sealed class SetupGatewaySuggestionTests(AdminDatabaseFixture database) : IAsyncLifetime
{
    private const string ContextKey = "sk-ume-setup-gateway-000000000000";
    private const string GatewayAuthority = "https://idp.example.se/realms/ume";
    private const string GatewayAdmin = "https://llm-admin.example.se";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Admin => database.Connection(RagDatabase.Admin);

    public async ValueTask InitializeAsync() => await ResetAsync(Admin, completed: false);

    public async ValueTask DisposeAsync() => await ResetAsync(Admin, completed: true);

    /// <summary>
    /// A setup host (signed in by headers on <c>/api</c>) whose context is read from a FakeGateway with <paramref name="oidcAuthority"/>,
    /// with or without the feature <c>oidc-authority</c>, after one context read; <paramref name="settings"/> are rag-admin's.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Host, IAsyncDisposable Gateway)> HostAsync(string? oidcAuthority, bool feature = true,
        params (string Key, string Value)[] settings)
    {
        var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", ContextKey);
            if (oidcAuthority is not null)
            {
                web.UseSetting("FakeGateway:Gateway:OidcAuthority", oidcAuthority);
            }

            if (!feature)
            {
                // A gateway without setup mode (the binder appends to a list with a default, so the feature is removed here).
                web.ConfigureTestServices(services => services.PostConfigure<FakeGatewayOptions>(o => o.Gateway.Features.Remove(GatewayFeatures.OidcAuthorityFeature)));
            }
        });
        var host = SetupHosts.Host(database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", ContextKey);
            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
        }), settings: settings);
        (await host.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        return (host, gateway);
    }

    private async Task<JsonElement> StateAsync(WebApplicationFactory<Program> host)
    {
        using var session = await SessionAsync(host, Admin);
        using var json = JsonDocument.Parse(await session.GetStringAsync("/bff/setup/state", Ct));
        return json.RootElement.Clone();
    }

    private static (string? Value, string? Source) Authority(JsonElement signIn)
    {
        var authority = signIn.GetProperty("authority");
        return (authority.GetProperty("value").GetString(), authority.GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_state_suggests_the_gateways_provider_address_and_links_the_gateway_admin_but_the_sign_in_values_stay_as_they_are()
    {
        var (host, gateway) = await HostAsync(" " + GatewayAuthority + " ", settings: ("Admin:GatewayAdminUrl", GatewayAdmin));
        await using var disposeGateway = gateway;
        await using var disposeHost = host;

        var state = await StateAsync(host);
        var suggestion = state.GetProperty("gateway");
        suggestion.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["adminUrl", "suggestedAuthority"]);
        suggestion.GetProperty("suggestedAuthority").GetString().ShouldBe(GatewayAuthority);
        suggestion.GetProperty("adminUrl").GetString().ShouldBe(GatewayAdmin);

        // A suggestion only: neither the state's sign-in values nor GET /api/settings/sign-in take it, and nothing is stored.
        Authority(state.GetProperty("signIn")).ShouldBe((null, SignInValueSources.Default));
        using var admin = await AdminDatabaseFixture.UserAsync(host, RagRoles.PlatformAdmin);
        using var settings = JsonDocument.Parse(await admin.GetStringAsync("/api/settings/sign-in", Ct));
        Authority(settings.RootElement).ShouldBe((null, SignInValueSources.Default));
        settings.RootElement.GetRawText().ShouldNotContain(GatewayAuthority);
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SignInSettings>().ReadAsync(Ct)).Authority.Value.ShouldBeNull();
        (await RowAsync(Admin))!.IdentityOidc.ShouldBe("{}");
    }

    [Fact]
    public async Task A_deployment_authority_stays_in_force_beside_the_suggestion()
    {
        var (host, gateway) = await HostAsync(GatewayAuthority, settings: ("Oidc:Authority", "https://deployed.example.se/realms/ume"));
        await using var disposeGateway = gateway;
        await using var disposeHost = host;

        var state = await StateAsync(host);
        state.GetProperty("gateway").GetProperty("suggestedAuthority").GetString().ShouldBe(GatewayAuthority);
        Authority(state.GetProperty("signIn")).ShouldBe(("https://deployed.example.se/realms/ume", SignInValueSources.Deployment));
    }

    [Theory]
    [InlineData(null)] // the gateway's sign-in is not set up yet
    [InlineData("http://idp.example.se/realms/ume")]
    [InlineData("idp.example.se/realms/ume")]
    [InlineData("https://user:pass@idp.example.se/realms/ume")]
    [InlineData("javascript:alert(1)")]
    public async Task A_gateway_with_the_feature_but_without_a_usable_https_address_suggests_none(string? oidcAuthority)
    {
        var (host, gateway) = await HostAsync(oidcAuthority);
        await using var disposeGateway = gateway;
        await using var disposeHost = host;
        host.Services.GetRequiredService<GatewayFeatures>().OidcAuthority.ShouldBeNull();

        var suggestion = (await StateAsync(host)).GetProperty("gateway");
        suggestion.GetProperty("suggestedAuthority").ValueKind.ShouldBe(JsonValueKind.Null);
        suggestion.GetProperty("adminUrl").ValueKind.ShouldBe(JsonValueKind.Null); // no context admin_url read here, nothing configured
    }

    [Fact]
    public async Task A_gateway_without_the_feature_gives_no_gateway_block()
    {
        var (host, gateway) = await HostAsync(GatewayAuthority, feature: false, settings: ("Admin:GatewayAdminUrl", GatewayAdmin));
        await using var disposeGateway = gateway;
        await using var disposeHost = host;
        host.Services.GetRequiredService<GatewayFeatures>().OidcAuthority.ShouldBeNull();

        (await StateAsync(host)).GetProperty("gateway").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
