extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using FakeCatalog = fakegateway::Kalla.FakeGateway.FakeCatalog;
using FakeGatewayOptions = fakegateway::Kalla.FakeGateway.FakeGatewayOptions;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>Department scope from member groups (identity design §2.3, §2.8), against the FakeGateway's catalogue.</summary>
public sealed class DepartmentMembershipResolverTests
{
    private static readonly OidcOptions Options = new();

    /// <summary>The FakeGateway catalogue as the org sync mirrors it: department 3000 lists the group "Byggnadsnämnden".</summary>
    private static IEnumerable<DepartmentGroups> Catalogue(bool active = true) =>
        FakeCatalog.Departments.Select(d => new DepartmentGroups(d.CostCenterCode, d.IsActive && active, d.MemberGroups));

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static string[] Values(ClaimsPrincipal user, string type) => [.. user.FindAll(type).Select(c => c.Value)];

    [Fact]
    public void A_member_group_adds_its_department_with_source_group()
    {
        var user = User(("groups", "Byggnadsnämnden"));
        DepartmentMembershipResolver.Apply(user, Catalogue(), Options).ShouldBe(["3000"]);
        Values(user, AdminClaims.DepartmentCodes).ShouldBe(["3000"]);
        Values(user, AdminClaims.GroupDepartmentCodes).ShouldBe(["3000"]);
    }

    [Fact]
    public void Claim_codes_and_group_codes_merge_without_duplicates()
    {
        var user = User(("departmentCodes", "1000"), ("departmentCodes", "3000"), ("groups", "Byggnadsnämnden"));
        DepartmentMembershipResolver.Apply(user, Catalogue(), Options).ShouldBeEmpty(); // 3000 already from the claim
        Values(user, AdminClaims.DepartmentCodes).ShouldBe(["1000", "3000"], ignoreOrder: true);
        Values(user, AdminClaims.GroupDepartmentCodes).ShouldBeEmpty();
    }

    [Fact]
    public void Matching_is_case_insensitive_and_unmapped_groups_add_nothing()
    {
        var user = User(("groups", "BYGGNADSNÄMNDEN"), ("groups", "Some-other-group"));
        DepartmentMembershipResolver.Apply(user, Catalogue(), Options).ShouldBe(["3000"]);

        var other = User(("groups", "Some-other-group"));
        DepartmentMembershipResolver.Apply(other, Catalogue(), Options).ShouldBeEmpty();
    }

    [Fact]
    public void Inactive_departments_add_nothing() =>
        DepartmentMembershipResolver.Apply(User(("groups", "Byggnadsnämnden")), Catalogue(active: false), Options).ShouldBeEmpty();

    [Fact]
    public void Membership_never_adds_a_role()
    {
        var user = User(("groups", "Byggnadsnämnden"));
        AdminAuthentication.ApplyClaimMapping(user, Options);
        DepartmentMembershipResolver.Apply(user, Catalogue(), Options);
        Values(user, AdminClaims.Roles).ShouldBeEmpty();
    }

    [Fact]
    public void An_overage_token_without_a_group_claim_keeps_the_claim_codes_only()
    {
        // Entra omits "groups" above its limit and sets _claim_names.groups; we never follow _claim_sources.
        var user = User(("departmentCodes", "1000"), ("_claim_names", """{"groups":"src1"}"""));
        DepartmentMembershipResolver.Apply(user, Catalogue(), Options).ShouldBeEmpty();
        Values(user, AdminClaims.DepartmentCodes).ShouldBe(["1000"]);
    }

    [Fact]
    public void A_json_array_group_claim_is_expanded_first()
    {
        var user = User(("groups", """["rag-pilot-users","Byggnadsnämnden"]"""));
        AdminAuthentication.ApplyClaimMapping(user, Options);
        DepartmentMembershipResolver.Apply(user, Catalogue(), Options).ShouldBe(["3000"]);
    }
}

/// <summary>Tenant and guest refusal (identity design §1.7).</summary>
public sealed class TenantCheckTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void The_configured_tenant_is_required_and_a_missing_tid_is_refused()
    {
        var options = new OidcOptions { TenantId = Tenant };
        TenantCheck.Refusal(User(("tid", Tenant)), options).ShouldBeNull();
        TenantCheck.Refusal(User(("tid", "99999999-2222-3333-4444-555555555555")), options).ShouldBe(TenantCheck.WrongTenant);
        TenantCheck.Refusal(User(), options).ShouldBe(TenantCheck.WrongTenant);
    }

    [Fact]
    public void Guests_are_refused_by_acct_or_a_foreign_idp()
    {
        var options = new OidcOptions { TenantId = Tenant };
        var issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
        TenantCheck.Refusal(User(("tid", Tenant), ("acct", "1")), options).ShouldBe(TenantCheck.GuestRefused);
        TenantCheck.Refusal(User(("tid", Tenant), ("iss", issuer), ("idp", "https://sts.windows.net/other-tenant/")), options).ShouldBe(TenantCheck.GuestRefused);
        TenantCheck.Refusal(User(("tid", Tenant), ("iss", issuer), ("idp", $"https://sts.windows.net/{Tenant}/")), options).ShouldBeNull();
        TenantCheck.Refusal(User(("tid", Tenant), ("iss", issuer), ("acct", "0")), options).ShouldBeNull();
        TenantCheck.Refusal(User(("tid", Tenant), ("acct", "1")), new OidcOptions { TenantId = Tenant, RefuseGuests = false }).ShouldBeNull();
    }

    [Fact]
    public void Keycloak_tokens_without_tid_acct_or_idp_pass_when_no_tenant_is_set() =>
        TenantCheck.Refusal(User(("sub", "x"), ("iss", "http://localhost:8080/realms/ume")), new OidcOptions()).ShouldBeNull();
}

/// <summary>The BFF endpoints and the sign-in steps against the real database (identity design §1).</summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class BffTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_anonymous_user_gets_the_user_document_and_the_csrf_token_cookie()
    {
        await using var factory = database.Factory();
        using var client = AdminDatabaseFixture.HttpsClient(factory);
        using var response = await client.GetAsync("/bff/user", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("isAuthenticated").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("departmentSources").GetArrayLength().ShouldBe(0);
        json.RootElement.GetProperty("organisationName").ValueKind.ShouldBe(JsonValueKind.Null); // no gateway context read
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.ShouldContain(c => c.StartsWith(AdminCookies.XsrfToken + "=", StringComparison.Ordinal));
        cookies.ShouldContain(c => c.StartsWith(AdminCookies.Antiforgery + "=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public async Task The_user_document_says_whether_the_deployment_runs_Open_WebUI(string? enabled, bool expected)
    {
        // OpenWebUi:Enabled, which the openwebui profile sets on rag-admin; without it the admin UI leaves Open WebUI out.
        await using var factory = enabled is null
            ? database.HeaderFactory()
            : database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting(Infrastructure.Security.OpenWebUiSignIn.EnabledKey, enabled));
        using var client = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, "1000");
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));

        json.RootElement.GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("openWebUi").GetProperty("enabled").GetBoolean().ShouldBe(expected);
    }

    /// <summary>
    /// The user document's <c>organisationName</c> with rag-admin's context read from a FakeGateway whose admin console names
    /// <paramref name="organisationName"/> (M-17 C12), with or without the feature <c>organisation-name</c>.
    /// </summary>
    private async Task<JsonElement> OrganisationNameFromContextAsync(string organisationName, bool feature = true)
    {
        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", ContextKey);
            web.UseSetting("FakeGateway:Gateway:OrganisationName", organisationName);
            if (!feature)
            {
                // A gateway before v0.10.2 (the binder appends to a list with a default, so the feature is removed here).
                web.ConfigureTestServices(services => services.PostConfigure<FakeGatewayOptions>(o => o.Gateway.Features.Remove(GatewayFeatures.OrganisationNameFeature)));
            }
        });
        await using var factory = database.Factory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", ContextKey);
            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
        });
        (await factory.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        using var client = AdminDatabaseFixture.HttpsClient(factory);
        using var response = await client.GetAsync("/bff/user", Ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("organisationName").Clone();
    }

    private const string ContextKey = "sk-ume-bff-organisation-0000000000";

    [Fact]
    public async Task The_user_document_names_the_gateways_organisation_before_sign_in() =>
        (await OrganisationNameFromContextAsync(" Exempelstad ")).GetString().ShouldBe("Exempelstad");

    [Fact]
    public async Task A_gateway_without_an_organisation_name_gives_null()
    {
        (await OrganisationNameFromContextAsync("")).ValueKind.ShouldBe(JsonValueKind.Null);
        (await OrganisationNameFromContextAsync("Exempelstad", feature: false)).ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_api_call_without_a_session_is_401_not_a_redirect()
    {
        await using var factory = database.Factory();
        using var client = AdminDatabaseFixture.HttpsClient(factory, allowAutoRedirect: false);
        using var response = await client.GetAsync("/api/ping", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_state_changing_call_without_the_csrf_header_is_refused()
    {
        // Signed in: authorization runs before the antiforgery check (the gateway's order), so an anonymous call is challenged first.
        await using var factory = database.HeaderFactory();
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, Kalla.Domain.Admin.RagRoles.Viewer);
        using var response = await client.PostAsync("/bff/session/extend", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sign_in_maps_claims_and_adds_departments_from_the_mirror()
    {
        // The mirror as the org sync (P0 #11) fills it from the FakeGateway catalogue.
        var admin = database.Connection(RagDatabase.Admin);
        var code = "T" + Random.Shared.Next(100_000, 999_999);
        await using (var db = TestPostgres.Context(admin))
        {
            db.Departments.Add(new Department { Id = Guid.CreateVersion7(), Name = "Test", CostCenterCode = code, MemberGroups = ["Testnämnden"], SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "s-1"), new Claim("oid", "o-1"), new Claim("roles", """["rag-viewer"]"""), new Claim("departmentCodes", "1000"),
             new Claim("groups", """["testnämnden","rag-pilot-users"]""")], "oidc"));
        await using (var db = TestPostgres.Context(admin))
        {
            (await AdminAuthentication.ValidateSignInAsync(user, new OidcOptions(), db, Ct)).ShouldBeNull();
        }

        user.FindAll(AdminClaims.Roles).Select(c => c.Value).ShouldBe(["rag-viewer"]);
        user.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).ShouldBe(["1000", code], ignoreOrder: true);
        user.FindAll(AdminClaims.GroupDepartmentCodes).Select(c => c.Value).ShouldBe([code]);
        AdminActor.Of(new Microsoft.AspNetCore.Http.DefaultHttpContext(), user).Subject.ShouldBe("o-1"); // audit actor = oid

        // A refused tenant stops before any mapping.
        var stranger = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "other")], "oidc"));
        await using var db2 = TestPostgres.Context(admin);
        (await AdminAuthentication.ValidateSignInAsync(stranger, new OidcOptions { TenantId = "mine" }, db2, Ct)).ShouldBe(TenantCheck.WrongTenant);
    }

    [Fact]
    public async Task A_role_group_a_platform_admin_saves_applies_at_the_next_sign_in_and_the_break_glass_still_counts()
    {
        // Decisions "Role groups as a setting" (M-17 C10): Settings.Identity.RoleGroups, read at each sign-in, merged with the
        // deployment's Oidc:RoleGroups:rag-platform-admin.
        await using var factory = database.HeaderFactory();
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var current = await platform.GetFromJsonAsync<JsonElement>("/api/settings/identity", Ct);
        var version = current.GetProperty("version").GetString();
        try
        {
            using (var refused = await platform.PutAsJsonAsync("/api/settings/identity", new { reviewer = new[] { "Byggnadsnämnden" }, version }, Ct))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // not a rag- name or an Entra object id (#413)
                (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Byggnadsnämnden");
            }

            using (var saved = await platform.PutAsJsonAsync("/api/settings/identity",
                new { reviewer = new[] { "rag-reviewers", " rag-reviewers " }, viewer = new[] { "8f0b7a6e-1c2d-4e5f-9a8b-7c6d5e4f3a2b" }, version }, Ct))
            {
                saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
                var body = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
                body.GetProperty("reviewer").EnumerateArray().Select(g => g.GetString()).ShouldBe(["rag-reviewers"]); // trimmed, distinct
                version = body.GetProperty("version").GetString();
            }

            var options = new OidcOptions { RoleGroups = { [RagRoles.PlatformAdmin] = "rag-break-glass" } };
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "s-2"), new Claim("groups", """["RAG-Reviewers","rag-break-glass"]""")], "oidc"));
            await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
            {
                (await AdminAuthentication.ValidateSignInAsync(user, options, db, Ct)).ShouldBeNull();
            }

            user.FindAll(AdminClaims.Roles).Select(c => c.Value).ShouldBe([RagRoles.Reviewer, RagRoles.PlatformAdmin], ignoreOrder: true);
        }
        finally
        {
            using var restored = await platform.PutAsJsonAsync("/api/settings/identity", new { version }, Ct);
            restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync(Ct));
        }
    }
}
