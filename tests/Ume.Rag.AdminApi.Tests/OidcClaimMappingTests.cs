// Copied from the gateway (tests/Ume.LlmGateway.AdminApi.Tests/OidcClaimMappingTests.cs); roles renamed to rag-*.
using System.Security.Claims;

namespace Ume.Rag.AdminApi.Tests;

public sealed class OidcClaimMappingTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static string[] Values(ClaimsPrincipal user, string type) => [.. user.FindAll(type).Select(c => c.Value)];

    [Fact]
    public void Defaults_expand_json_arrays_like_the_bundled_realm()
    {
        var user = Principal(("roles", "[\"rag-viewer\",\"rag-platform-admin\"]"), ("departmentCodes", "[\"KS\"]"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions());
        Values(user, "roles").ShouldBe(["rag-viewer", "rag-platform-admin"], ignoreOrder: true);
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }

    [Fact]
    public void Renamed_claims_are_copied_to_the_names_the_api_uses()
    {
        var user = Principal(("role", "rag-viewer"), ("department", "KS"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions { RoleClaim = "role", DepartmentClaim = "department" });
        Values(user, "roles").ShouldBe(["rag-viewer"]);
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }

    [Fact]
    public void Groups_grant_roles_case_insensitively_and_unmapped_groups_grant_nothing()
    {
        var options = new OidcOptions
        {
            RoleGroups = new() { ["rag-platform-admin"] = "GG-Llm-Admins; GG-Llm-Ops", ["rag-viewer"] = "GG-Llm-Viewers" },
        };
        var user = Principal(("groups", "[\"gg-llm-ops\",\"GG-Other\"]"));
        AdminAuthentication.ApplyClaimMapping(user, options);
        Values(user, "roles").ShouldBe(["rag-platform-admin"]);
    }

    [Fact]
    public void Team_codes_come_from_their_claim_or_from_group_names()
    {
        var user = Principal(("teamCodes", "[\"AI\",\"KC\"]"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions());
        Values(user, "teamCodes").ShouldBe(["AI", "KC"]);

        var grouped = Principal(("groups", "[\"GG-Llm-TeamAdmins\",\"GG-Team-AI\"]"));
        AdminAuthentication.ApplyClaimMapping(grouped, new OidcOptions { TeamClaim = "groups", RoleGroups = new() { ["rag-department-admin"] = "GG-Llm-TeamAdmins" } });
        Values(grouped, "roles").ShouldBe(["rag-department-admin"]);
        Values(grouped, "teamCodes").ShouldBe(["GG-Llm-TeamAdmins", "GG-Team-AI"]);
    }

    [Fact]
    public void A_user_without_matching_claims_gets_no_roles()
    {
        var user = Principal(("groups", "GG-Other"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions { RoleGroups = new() { ["rag-viewer"] = "GG-Llm-Viewers" } });
        Values(user, "roles").ShouldBeEmpty();
    }
}
