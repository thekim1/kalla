using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// M-20 S2 (identity design §19, "Effective sign-in values" and "Reload"): rag-admin's OpenID Connect options come from the
/// effective sign-in values and are rebuilt at the next sign-in when they change, in the settings row or in the deployment; it
/// starts without an authority and refuses a sign-in then. The placeholder provider's metadata follows the authority
/// (<see cref="AdminDatabaseFixture.StaticConfiguration"/>), so a challenge's address names the authority it was built with.
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class SignInReloadTests(AdminDatabaseFixture database)
{
    private const string Deployed = "https://idp.invalid/realms/ume";
    private const string WizardA = "https://wizard-a.invalid/realms/ume";
    private const string WizardB = "https://wizard-b.invalid/realms/ume";
    private const string WizardSecret = "wizard-secret-0c4d7e";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The admin API with no deployment authority or secret, plus <paramref name="settings"/>.</summary>
    private WebApplicationFactory<Program> Host(CapturingLoggerProvider? logs = null, params (string Key, string Value)[] settings) =>
        database.Factory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Oidc:Authority", "");
            web.UseSetting("Oidc:ClientSecret", "");
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            if (logs is not null)
            {
                web.ConfigureTestServices(services => services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
            }
        });

    /// <summary>Starts a sign-in at <c>/bff/login</c>; returns where the browser is sent.</summary>
    private static async Task<Uri> ChallengeAsync(WebApplicationFactory<Program> factory)
    {
        using var client = AdminDatabaseFixture.HttpsClient(factory, allowAutoRedirect: false);
        using var response = await client.GetAsync("/bff/login?returnUrl=/sources", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
        return response.Headers.Location.ShouldNotBeNull();
    }

    private static void ShouldGoTo(Uri location, string authority, string clientId)
    {
        location.GetLeftPart(UriPartial.Path).ShouldBe(authority + "/authorize");
        HttpUtility.ParseQueryString(location.Query)["client_id"].ShouldBe(clientId);
    }

    private static async Task<JsonElement> UserDocumentAsync(WebApplicationFactory<Program> factory)
    {
        using var client = AdminDatabaseFixture.HttpsClient(factory);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        return json.RootElement.Clone();
    }

    private static OpenIdConnectOptions LiveOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

    /// <summary>Clears the wizard's values and secret, so each test starts from the deployment's values alone.</summary>
    private static Task<EffectiveSignIn> ClearWizardAsync(WebApplicationFactory<Program> factory) => SignInSettingsTests.SaveAsync(factory, new OidcWizardValues(), "");

    [Fact]
    public async Task The_challenge_goes_to_the_wizards_authority_without_a_deployment_one_and_to_the_deployments_when_both_are_set()
    {
        await using var wizardOnly = Host();
        await using var both = Host(settings: ("Oidc:Authority", Deployed));
        try
        {
            await SignInSettingsTests.SaveAsync(wizardOnly, new OidcWizardValues { Authority = WizardA, ClientId = "wizard-client" }, WizardSecret);

            ShouldGoTo(await ChallengeAsync(wizardOnly), WizardA, "wizard-client");
            LiveOptions(wizardOnly).ClientSecret.ShouldBe(WizardSecret);
            // The deployment's authority wins; the client id it leaves unset is the wizard's.
            ShouldGoTo(await ChallengeAsync(both), Deployed, "wizard-client");
        }
        finally
        {
            await ClearWizardAsync(wizardOnly);
        }
    }

    [Fact]
    public async Task A_saved_change_applies_to_the_next_challenge_on_both_of_two_instances_without_a_restart()
    {
        await using var first = Host();
        await using var second = Host();
        try
        {
            await SignInSettingsTests.SaveAsync(first, new OidcWizardValues { Authority = WizardA, ClientId = "client-a" }, null);
            ShouldGoTo(await ChallengeAsync(first), WizardA, "client-a");
            ShouldGoTo(await ChallengeAsync(second), WizardA, "client-a");
            var built = LiveOptions(first);

            // Unchanged values: the options are not rebuilt.
            await ChallengeAsync(first);
            LiveOptions(first).ShouldBeSameAs(built);

            // Saved through the first instance; the second has nothing in memory to tell it, and follows at its next challenge.
            await SignInSettingsTests.SaveAsync(first, new OidcWizardValues { Authority = WizardB, ClientId = "client-b" }, null);
            ShouldGoTo(await ChallengeAsync(second), WizardB, "client-b");
            ShouldGoTo(await ChallengeAsync(first), WizardB, "client-b");
            LiveOptions(first).ShouldNotBeSameAs(built);
        }
        finally
        {
            await ClearWizardAsync(first);
        }
    }

    [Fact]
    public async Task A_changed_deployment_value_applies_to_the_next_challenge()
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = Host(logs, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "deploy-secret-1"));
        await ClearWizardAsync(factory);
        ShouldGoTo(await ChallengeAsync(factory), Deployed, new OidcOptions().ClientId);
        LiveOptions(factory).ClientSecret.ShouldBe("deploy-secret-1");

        // As a reloaded key-per-file secret or a changed value: the configuration is read at each challenge.
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration["Oidc:Authority"] = "https://reloaded.invalid/realms/ume";
        configuration["Oidc:ClientId"] = "reloaded-client";
        ShouldGoTo(await ChallengeAsync(factory), "https://reloaded.invalid/realms/ume", "reloaded-client");

        // A changed secret alone rebuilds the options too.
        var built = LiveOptions(factory);
        configuration["Oidc:ClientSecret"] = "deploy-secret-2";
        await ChallengeAsync(factory);
        LiveOptions(factory).ShouldNotBeSameAs(built);
        LiveOptions(factory).ClientSecret.ShouldBe("deploy-secret-2");

        logs.Text.ShouldContain("Sign-in options rebuilt");
        logs.Text.ShouldNotContain("deploy-secret-1");
        logs.Text.ShouldNotContain("deploy-secret-2");
    }

    [Fact]
    public async Task Without_an_authority_the_host_starts_reports_it_and_refuses_a_sign_in_without_a_500()
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = Host(logs);
        await ClearWizardAsync(factory);

        (await UserDocumentAsync(factory)).GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeFalse();

        using var client = AdminDatabaseFixture.HttpsClient(factory, allowAutoRedirect: false);
        using (var login = await client.GetAsync("/bff/login", Ct))
        {
            var text = await login.Content.ReadAsStringAsync(Ct);
            login.StatusCode.ShouldBe(HttpStatusCode.Conflict, text);
            JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.SignInNotConfigured.Code);
            login.Headers.Location.ShouldBeNull();
        }

        logs.Logged(typeof(AdminAuthentication).FullName!, SignInNotConfiguredReasons.NoAuthority).ShouldBeTrue(logs.Text);
        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/version", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/ping", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // A protected page outside /api is sent to /bff/login, where every sign-in starts.
        using (var page = await client.GetAsync("/openapi/v1.json", Ct))
        {
            page.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            page.Headers.Location!.OriginalString.ShouldBe("/bff/login?returnUrl=%2Fopenapi%2Fv1.json");
        }

        // The wizard's values make it configured at the next sign-in, without a restart.
        try
        {
            await SignInSettingsTests.SaveAsync(factory, new OidcWizardValues { Authority = WizardA }, null);
            (await UserDocumentAsync(factory)).GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeTrue();
            ShouldGoTo(await ChallengeAsync(factory), WizardA, new OidcOptions().ClientId);
        }
        finally
        {
            await ClearWizardAsync(factory);
        }
    }

    [Fact]
    public async Task An_unreadable_stored_secret_refuses_the_sign_in_as_not_configured_with_its_own_log_line()
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = Host(logs);
        try
        {
            await SignInSettingsTests.SaveAsync(factory, new OidcWizardValues { Authority = WizardA }, WizardSecret);
            ShouldGoTo(await ChallengeAsync(factory), WizardA, new OidcOptions().ClientId);
            await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
            {
                // Protected by another key ring: a replaced certificate without the old one.
                var foreign = new SettingsSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SettingsSecretProtector>.Instance)
                    .Protect(StoredSecret.IdentityOidcClientSecret, WizardSecret);
                await db.Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.IdentityOidcEncryptedClientSecret, foreign), Ct);
            }

            using var client = AdminDatabaseFixture.HttpsClient(factory, allowAutoRedirect: false);
            using var login = await client.GetAsync("/bff/login", Ct);
            login.StatusCode.ShouldBe(HttpStatusCode.Conflict, await login.Content.ReadAsStringAsync(Ct));
            (await UserDocumentAsync(factory)).GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeFalse();
            logs.Logged(typeof(AdminAuthentication).FullName!, SignInNotConfiguredReasons.UnreadableClientSecret).ShouldBeTrue(logs.Text);
            logs.Logged(typeof(AdminSignIn).FullName!, SignInNotConfiguredReasons.UnreadableClientSecret).ShouldBeTrue(logs.Text);
            logs.Text.ShouldNotContain(WizardSecret);
        }
        finally
        {
            await ClearWizardAsync(factory);
        }
    }

    [Fact]
    public async Task A_callback_the_handler_cannot_complete_sends_the_person_back_to_sign_in_not_a_500()
    {
        // As a sign-in started before the values changed, or a replayed one: its state or correlation cookie does not check out.
        await using var factory = database.Factory();
        using var client = AdminDatabaseFixture.HttpsClient(factory, allowAutoRedirect: false);
        using var callback = await client.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "code-from-before", ["state"] = "state-from-before",
        }), Ct);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync(Ct));
        callback.Headers.Location!.OriginalString.ShouldBe("/?signin=failed");
    }

    [Fact]
    public async Task The_sign_in_checks_take_the_effective_claim_names_tenant_and_guest_rule()
    {
        await using var factory = Host(settings: ("Oidc:Authority", Deployed));
        try
        {
            await SignInSettingsTests.SaveAsync(factory, new OidcWizardValues { RoleClaim = "wizard_roles", TenantId = "wizard-tenant", RefuseGuests = false }, null);
            await ChallengeAsync(factory);

            // The handler's own event, on the options built from the wizard's values.
            var mapped = await TokenValidatedAsync(factory, User(("tid", "wizard-tenant"), ("acct", "1"), ("wizard_roles", RagRoles.Viewer)));
            mapped.Refused.ShouldBeFalse(); // a guest, but the wizard does not refuse guests
            Roles(mapped.Principal).ShouldBe([RagRoles.Viewer]);
            (await TokenValidatedAsync(factory, User(("tid", "other"), ("wizard_roles", RagRoles.Viewer)))).Refused.ShouldBeTrue();

            // A deployment value wins over the wizard's.
            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            configuration["Oidc:RoleClaim"] = "deploy_roles";
            configuration["Oidc:RefuseGuests"] = "true";
            await ChallengeAsync(factory);
            var deployed = await TokenValidatedAsync(factory, User(("tid", "wizard-tenant"), ("wizard_roles", RagRoles.Viewer), ("deploy_roles", RagRoles.Reviewer)));
            deployed.Refused.ShouldBeFalse();
            Roles(deployed.Principal).ShouldBe([RagRoles.Reviewer]);
            (await TokenValidatedAsync(factory, User(("tid", "wizard-tenant"), ("acct", "1")))).Refused.ShouldBeTrue(); // a guest, refused again
        }
        finally
        {
            await ClearWizardAsync(factory);
        }
    }

    [Fact]
    public void A_snapshot_is_configured_only_with_a_usable_authority_and_compares_every_value_the_secret_included()
    {
        static SignInSnapshot Snapshot(bool requireHttps, StoredSecretValue? secret = null, params (string Key, string? Value)[] values) => SignInSnapshot.Of(
            SignInSettings.Resolve(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build(),
                new OidcWizardValues(), () => secret ?? StoredSecretValue.NotSet, null, 3, null), requireHttps);

        Snapshot(true).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.NoAuthority);
        Snapshot(true, null, ("Oidc:Authority", "http://idp.example/realms/ume")).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.InvalidAuthority);
        Snapshot(false, null, ("Oidc:Authority", "idp.example")).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.InvalidAuthority);
        Snapshot(false, null, ("Oidc:Authority", "http://localhost:8080/realms/ume")).IsConfigured.ShouldBeTrue();
        Snapshot(true, StoredSecretValue.Unreadable, ("Oidc:Authority", Deployed)).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.UnreadableClientSecret);

        var one = Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"));
        one.IsConfigured.ShouldBeTrue();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"))).ShouldBeTrue();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-2"))).ShouldBeFalse();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"), ("Oidc:TeamClaim", "teams"))).ShouldBeFalse();
        one.ToString().ShouldNotContain("secret-1");
        one.Values.ToString().ShouldNotContain("secret-1");
    }

    [Fact]
    public void A_set_authority_must_be_an_absolute_https_url_outside_development_and_none_is_allowed()
    {
        var production = new OidcAuthorityValidator(new HostingEnvironment { EnvironmentName = Environments.Production });
        var development = new OidcAuthorityValidator(new HostingEnvironment { EnvironmentName = Environments.Development });
        production.Validate(null, new OidcOptions()).Succeeded.ShouldBeTrue();
        production.Validate(null, new OidcOptions { Authority = " " }).Succeeded.ShouldBeTrue();
        production.Validate(null, new OidcOptions { Authority = Deployed }).Succeeded.ShouldBeTrue();
        production.Validate(null, new OidcOptions { Authority = "http://idp.example/realms/ume" }).Failed.ShouldBeTrue();
        development.Validate(null, new OidcOptions { Authority = "http://localhost:8080/realms/ume" }).Succeeded.ShouldBeTrue();
        development.Validate(null, new OidcOptions { Authority = "idp.example" }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void A_set_but_invalid_authority_still_stops_the_start()
    {
        using var factory = database.Factory().WithWebHostBuilder(web => web.UseSetting("Oidc:Authority", "not-a-url"));
        Should.Throw<OptionsValidationException>(() => factory.Services.GetRequiredService<IOptions<OidcOptions>>()).Message.ShouldContain("Oidc:Authority");
    }

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    private static string[] Roles(ClaimsPrincipal principal) => [.. principal.FindAll(AdminClaims.Roles).Select(c => c.Value).Distinct()];

    /// <summary>Runs the live options' <c>OnTokenValidated</c> for <paramref name="user"/>, as the handler does after the token checks.</summary>
    private static async Task<(bool Refused, ClaimsPrincipal Principal)> TokenValidatedAsync(WebApplicationFactory<Program> factory, ClaimsPrincipal user)
    {
        var options = LiveOptions(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var context = new TokenValidatedContext(http, new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            options, user, new AuthenticationProperties());
        await options.Events.OnTokenValidated(context);
        var location = http.Response.Headers.Location.ToString();
        return (location.StartsWith("/?signin=refused", StringComparison.Ordinal), context.Principal!);
    }
}
