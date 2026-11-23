using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kalla.Domain.Admin;
using Kalla.Infrastructure.Security;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// M-20 S1 (identity design §19): kalla-admin's sign-in values from the deployment, else <c>Settings.Identity.Oidc</c>, else the
/// code's default, each with its source; the wizard's client secret encrypted with Data Protection and never in a response or
/// the audit log. The fixture's deployment sets <c>Oidc:Authority</c> and <c>Oidc:ClientSecret</c>.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class SignInSettingsTests(AdminDatabaseFixture database)
{
    private const string WizardSecret = "wizard-secret-5f1c9a";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static EffectiveSignIn Resolve(IConfiguration configuration, OidcWizardValues wizard, Func<StoredSecretValue>? secret = null) =>
        SignInSettings.Resolve(configuration, wizard, secret ?? (() => StoredSecretValue.NotSet), null, 7, null);

    private static SettingsSecretProtector Protector(IDataProtectionProvider provider, ListLogger<SettingsSecretProtector>? logger = null) =>
        new(provider, logger ?? new ListLogger<SettingsSecretProtector>());

    [Fact]
    public void A_deployment_value_wins_over_the_wizard_and_a_blank_one_counts_as_unset()
    {
        var wizard = new OidcWizardValues
        {
            Authority = "https://wizard.example/realms/fors", ClientId = "wizard-client", RoleClaim = "wizard-roles", ExtraScopes = ["groups"], TenantId = "wizard-tenant",
            RefuseGuests = false,
        };
        var effective = Resolve(Configuration(
            ("Oidc:Authority", "https://deploy.example/realms/fors"), ("Oidc:ClientId", "  "), ("Oidc:RoleClaim", ""), ("Oidc:ExtraScopes:0", " "),
            ("Oidc:TenantId", "deploy-tenant"), ("Oidc:RefuseGuests", "true")), wizard);

        effective.Authority.ShouldBe(new SignInValue<string?>("https://deploy.example/realms/fors", SignInValueSources.Deployment));
        effective.TenantId.ShouldBe(new SignInValue<string?>("deploy-tenant", SignInValueSources.Deployment));
        effective.RefuseGuests.ShouldBe(new SignInValue<bool>(true, SignInValueSources.Deployment));
        // Blank deployment values are unset, so the wizard's apply.
        effective.ClientId.ShouldBe(new SignInValue<string>("wizard-client", SignInValueSources.Wizard));
        effective.RoleClaim.ShouldBe(new SignInValue<string>("wizard-roles", SignInValueSources.Wizard));
        effective.ExtraScopes.Value.ShouldBe(["groups"]);
        effective.ExtraScopes.Source.ShouldBe(SignInValueSources.Wizard);

        var scopes = Resolve(Configuration(("Oidc:ExtraScopes:0", "groups"), ("Oidc:ExtraScopes:1", "offline_access")), wizard).ExtraScopes;
        scopes.Value.ShouldBe(["groups", "offline_access"]);
        scopes.Source.ShouldBe(SignInValueSources.Deployment);
        Resolve(Configuration(("Oidc:RefuseGuests", "false")), wizard with { RefuseGuests = true }).RefuseGuests
            .ShouldBe(new SignInValue<bool>(false, SignInValueSources.Deployment));
    }

    [Fact]
    public void The_code_default_applies_when_neither_sets_a_value()
    {
        var defaults = new OidcOptions();
        var effective = Resolve(Configuration(("Oidc:Authority", ""), ("Oidc:ClientSecret", " ")), new OidcWizardValues { ClientId = " ", ExtraScopes = [] });

        effective.Authority.ShouldBe(new SignInValue<string?>(null, SignInValueSources.Default));
        effective.ClientId.ShouldBe(new SignInValue<string>(defaults.ClientId, SignInValueSources.Default));
        effective.RoleClaim.ShouldBe(new SignInValue<string>(defaults.RoleClaim, SignInValueSources.Default));
        effective.DepartmentClaim.ShouldBe(new SignInValue<string>(defaults.DepartmentClaim, SignInValueSources.Default));
        effective.TeamClaim.ShouldBe(new SignInValue<string>(defaults.TeamClaim, SignInValueSources.Default));
        effective.GroupClaim.ShouldBe(new SignInValue<string>(defaults.GroupClaim, SignInValueSources.Default));
        effective.ExtraScopes.Value.ShouldBeEmpty();
        effective.ExtraScopes.Source.ShouldBe(SignInValueSources.Default);
        effective.TenantId.ShouldBe(new SignInValue<string?>(null, SignInValueSources.Default));
        effective.RefuseGuests.ShouldBe(new SignInValue<bool>(defaults.RefuseGuests, SignInValueSources.Default));
        (effective.ClientSecret.Source, effective.ClientSecret.IsSet, effective.ClientSecret.Value).ShouldBe((SignInValueSources.Default, false, null));
        effective.Version.ShouldBe(7u);
    }

    [Fact]
    public void The_client_secret_follows_the_same_rule_and_a_deployment_secret_never_touches_the_key_ring()
    {
        var deployed = Resolve(Configuration(("Oidc:ClientSecret", "deploy-secret")), new OidcWizardValues(),
            () => throw new InvalidOperationException("The stored secret is not read when the deployment sets one."));
        (deployed.ClientSecret.Source, deployed.ClientSecret.Value, deployed.ClientSecret.IsSet).ShouldBe((SignInValueSources.Deployment, "deploy-secret", true));
        deployed.ClientSecret.ToString().ShouldNotContain("deploy-secret");
        deployed.ToString().ShouldNotContain("deploy-secret");

        var wizard = Resolve(Configuration(("Oidc:ClientSecret", "")), new OidcWizardValues(), () => StoredSecretValue.Readable(WizardSecret));
        (wizard.ClientSecret.Source, wizard.ClientSecret.Value, wizard.ClientSecret.Unreadable).ShouldBe((SignInValueSources.Wizard, WizardSecret, false));

        var unreadable = Resolve(Configuration(), new OidcWizardValues(), () => StoredSecretValue.Unreadable);
        (unreadable.ClientSecret.Source, unreadable.ClientSecret.Value, unreadable.ClientSecret.IsSet, unreadable.ClientSecret.Unreadable)
            .ShouldBe((SignInValueSources.Wizard, null, true, true));
    }

    [Fact]
    public void A_secret_round_trips_as_ciphertext_and_one_the_key_ring_cannot_read_is_unreadable_without_an_exception()
    {
        var provider = new EphemeralDataProtectionProvider();
        var logger = new ListLogger<SettingsSecretProtector>();
        var protector = Protector(provider, logger);

        var ciphertext = protector.Protect(StoredSecret.IdentityOidcClientSecret, WizardSecret);
        ciphertext.ShouldNotContain(WizardSecret);
        var read = protector.Unprotect(StoredSecret.IdentityOidcClientSecret, ciphertext);
        (read.State, read.Value).ShouldBe((StoredSecretState.Readable, WizardSecret));
        read.ToString().ShouldNotContain(WizardSecret);
        protector.Unprotect(StoredSecret.IdentityOidcClientSecret, null).State.ShouldBe(StoredSecretState.NotSet);

        // Another key ring (a replaced certificate, a lost key), a damaged value, and a payload of another purpose.
        var otherRing = Protector(new EphemeralDataProtectionProvider()).Protect(StoredSecret.IdentityOidcClientSecret, WizardSecret);
        var otherPurpose = provider.CreateProtector("Ume.Rag.AdminApi.KeySecret.v1").Protect(WizardSecret);
        foreach (var bad in new[] { otherRing, "not-a-payload", otherPurpose })
        {
            var result = protector.Unprotect(StoredSecret.IdentityOidcClientSecret, bad);
            (result.State, result.Value).ShouldBe((StoredSecretState.Unreadable, null));
        }

        logger.Entries.Count.ShouldBe(3);
        logger.Entries.ShouldAllBe(m => m.Level == LogLevel.Warning && !m.Message.Contains(WizardSecret) && m.Message.Contains(StoredSecret.IdentityOidcClientSecret.Purpose));
    }

    [Fact]
    public async Task Saving_wizard_values_stores_ciphertext_bumps_the_version_and_audits_without_the_secret()
    {
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Oidc:ClientSecret", ""));
        var saved = await SaveAsync(factory, new OidcWizardValues
        {
            Authority = " https://wizard.example/realms/fors ", ClientId = "wizard-client", ExtraScopes = ["groups", " "], ClientSecretExpiresOn = new DateOnly(2027, 6, 30),
        }, WizardSecret);

        // The deployment's authority wins; the wizard's client id and secret apply, the latter decrypted for server-side callers.
        saved.Authority.ShouldBe(new SignInValue<string?>("https://idp.invalid/realms/fors", SignInValueSources.Deployment));
        saved.ClientId.ShouldBe(new SignInValue<string>("wizard-client", SignInValueSources.Wizard));
        saved.ExtraScopes.Value.ShouldBe(["groups"]);
        saved.ExtraScopes.Source.ShouldBe(SignInValueSources.Wizard);
        (saved.ClientSecret.Source, saved.ClientSecret.Value, saved.ClientSecret.Unreadable).ShouldBe((SignInValueSources.Wizard, WizardSecret, false));
        saved.ClientSecret.SetAt.ShouldNotBeNull();
        saved.ClientSecretExpiresOn.ShouldBe(new DateOnly(2027, 6, 30));

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var row = await db.Settings.AsNoTracking().SingleAsync(Ct);
        row.Version.ShouldBe(saved.Version);
        row.IdentityOidcEncryptedClientSecret.ShouldNotBeNullOrEmpty();
        row.IdentityOidcEncryptedClientSecret.ShouldNotContain(WizardSecret);
        row.IdentityOidc.ShouldContain("https://wizard.example/realms/fors");
        row.IdentityOidc.ShouldNotContain(WizardSecret);

        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == AuditEntityTypes.Settings && a.EntityId == SignInSettings.AuditEntityId)
            .OrderByDescending(a => a.Id).FirstAsync(Ct);
        audit.Action.ShouldBe("update");
        audit.Details.ShouldNotBeNull();
        audit.Details.ShouldContain("wizard-client");
        audit.Details.ShouldNotContain(WizardSecret);
        audit.Details.ShouldNotContain(row.IdentityOidcEncryptedClientSecret!);

        // Saving again without a secret keeps it; an empty one removes it.
        var kept = await SaveAsync(factory, new OidcWizardValues { ClientId = "wizard-client-2" }, null);
        (kept.ClientSecret.Value, kept.ClientId.Value).ShouldBe((WizardSecret, "wizard-client-2"));
        kept.Version.ShouldNotBe(saved.Version);
        var removed = await SaveAsync(factory, new OidcWizardValues(), "");
        (removed.ClientSecret.Source, removed.ClientSecret.IsSet).ShouldBe((SignInValueSources.Default, false));
        removed.ClientId.Source.ShouldBe(SignInValueSources.Default);
    }

    [Fact]
    public async Task The_endpoint_answers_values_with_their_sources_and_never_the_secret()
    {
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Oidc:ClientSecret", ""));
        await SaveAsync(factory, new OidcWizardValues { ClientId = "wizard-client", TenantId = "wizard-tenant", ClientSecretExpiresOn = new DateOnly(2027, 1, 31) }, WizardSecret);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        using var response = await admin.GetAsync("/api/settings/sign-in", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        text.ShouldNotContain(WizardSecret);
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            text.ShouldNotContain((await db.Settings.AsNoTracking().Select(s => s.IdentityOidcEncryptedClientSecret).SingleAsync(Ct))!);
        }

        var json = JsonDocument.Parse(text).RootElement;
        Value(json, "authority").ShouldBe(("https://idp.invalid/realms/fors", SignInValueSources.Deployment));
        Value(json, "clientId").ShouldBe(("wizard-client", SignInValueSources.Wizard));
        Value(json, "tenantId").ShouldBe(("wizard-tenant", SignInValueSources.Wizard));
        Value(json, "roleClaim").ShouldBe((new OidcOptions().RoleClaim, SignInValueSources.Default));
        json.GetProperty("refuseGuests").GetProperty("value").GetBoolean().ShouldBeTrue();
        json.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
        var secret = json.GetProperty("clientSecret");
        secret.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["expiresOn", "set", "setAt", "source", "unreadable"]);
        secret.GetProperty("set").GetBoolean().ShouldBeTrue();
        secret.GetProperty("source").GetString().ShouldBe(SignInValueSources.Wizard);
        secret.GetProperty("expiresOn").GetString().ShouldBe("2027-01-31");
        secret.GetProperty("unreadable").GetBoolean().ShouldBeFalse();

        // The fixture's own deployment secret: answered as set by the deployment, never shown.
        await using var deployed = database.HeaderFactory();
        using var deployedAdmin = await AdminDatabaseFixture.UserAsync(deployed, RagRoles.PlatformAdmin);
        var deployedText = await deployedAdmin.GetStringAsync("/api/settings/sign-in", Ct);
        deployedText.ShouldNotContain("test-secret");
        deployedText.ShouldNotContain(WizardSecret);
        var deployedSecret = JsonDocument.Parse(deployedText).RootElement.GetProperty("clientSecret");
        (deployedSecret.GetProperty("source").GetString(), deployedSecret.GetProperty("set").GetBoolean()).ShouldBe((SignInValueSources.Deployment, true));
        deployedSecret.GetProperty("expiresOn").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_ciphertext_the_key_ring_cannot_read_is_reported_unreadable_not_a_failure()
    {
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Oidc:ClientSecret", ""));
        await SaveAsync(factory, new OidcWizardValues(), WizardSecret);
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            // Protected by another key ring: what a replaced certificate without the old one leaves behind.
            var foreign = Protector(new EphemeralDataProtectionProvider()).Protect(StoredSecret.IdentityOidcClientSecret, WizardSecret);
            await db.Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.IdentityOidcEncryptedClientSecret, foreign), Ct);
        }

        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var response = await admin.GetAsync("/api/settings/sign-in", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var secret = JsonDocument.Parse(text).RootElement.GetProperty("clientSecret");
        (secret.GetProperty("set").GetBoolean(), secret.GetProperty("source").GetString(), secret.GetProperty("unreadable").GetBoolean())
            .ShouldBe((true, SignInValueSources.Wizard, true));

        await using var scope = factory.Services.CreateAsyncScope();
        var effective = await scope.ServiceProvider.GetRequiredService<SignInSettings>().ReadAsync(Ct);
        (effective.ClientSecret.Unreadable, effective.ClientSecret.Value).ShouldBe((true, null));
    }

    [Theory]
    [InlineData(RagRoles.DepartmentAdmin)]
    [InlineData(RagRoles.Viewer)]
    public async Task Only_a_platform_admin_reads_the_sign_in_settings(string role)
    {
        await using var factory = database.HeaderFactory();
        using var user = await AdminDatabaseFixture.UserAsync(factory, role, "1000");
        using var response = await user.GetAsync("/api/settings/sign-in", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static (string?, string?) Value(JsonElement json, string name) =>
        (json.GetProperty(name).GetProperty("value").GetString(), json.GetProperty(name).GetProperty("source").GetString());

    /// <summary>The store's write as setup mode will call it, in a request of a platform admin.</summary>
    internal static async Task<EffectiveSignIn> SaveAsync(WebApplicationFactory<Program> factory, OidcWizardValues values, string? clientSecret)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AdminClaims.Subject, "setup-person"), new Claim(AdminClaims.Roles, RagRoles.PlatformAdmin)], "test")),
        };
        try
        {
            return await scope.ServiceProvider.GetRequiredService<SignInSettings>()
                .SaveWizardValuesAsync(scope.ServiceProvider.GetRequiredService<AdminContext>(), AdminActor.Of(accessor.HttpContext), values, clientSecret, null, null, Ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

}
