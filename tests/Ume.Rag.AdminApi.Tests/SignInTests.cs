extern alias fakegateway;

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using FakeCatalog = fakegateway::Ume.Rag.FakeGateway.FakeCatalog;

namespace Ume.Rag.AdminApi.Tests;

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
        json.RootElement.GetProperty("organisationName").GetString().ShouldBe("Umeå kommun"); // the default
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.ShouldContain(c => c.StartsWith(AdminCookies.XsrfToken + "=", StringComparison.Ordinal));
        cookies.ShouldContain(c => c.StartsWith(AdminCookies.Antiforgery + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_user_document_carries_the_configured_organisation_name_before_sign_in()
    {
        await using var factory = database.Factory().WithWebHostBuilder(web => web.UseSetting("Admin:OrganisationName", " Exempelstad "));
        using var client = AdminDatabaseFixture.HttpsClient(factory);
        using var response = await client.GetAsync("/bff/user", Ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("organisationName").GetString().ShouldBe("Exempelstad");
    }

    [Fact]
    public async Task An_empty_organisation_name_is_sent_as_null()
    {
        await using var factory = database.Factory().WithWebHostBuilder(web => web.UseSetting("Admin:OrganisationName", ""));
        using var client = AdminDatabaseFixture.HttpsClient(factory);
        using var response = await client.GetAsync("/bff/user", Ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("organisationName").ValueKind.ShouldBe(JsonValueKind.Null);
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
        client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, Ume.Rag.Domain.Admin.RagRoles.Viewer);
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
}
