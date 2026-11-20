using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// #413: in the shared Keycloak realm the group claim carries every group by its short name, the gateway's included, so
/// a role group must be a <c>rag-</c> name or an Entra object id; the admin API refuses to start otherwise.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class OidcOptionsValidatorTests(AdminDatabaseFixture database)
{
    private static ValidateOptionsResult Validate(Dictionary<string, string> roleGroups) =>
        new OidcOptionsValidator().Validate(null, new OidcOptions { Authority = "https://idp.invalid", RoleGroups = roleGroups });

    [Theory]
    [InlineData("rag-platform-admins")]
    [InlineData("RAG-Admins")] // matching is case-insensitive, so is the prefix
    [InlineData("rag-admins; rag-ops")]
    [InlineData("3f2b8c1e-6a4d-4e5f-9b7a-1c2d3e4f5a6b")] // an Entra object id
    [InlineData("rag-admins;3f2b8c1e-6a4d-4e5f-9b7a-1c2d3e4f5a6b;")]
    public void Rag_names_and_entra_object_ids_are_accepted(string groups) =>
        Validate(new() { ["rag-platform-admin"] = groups }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("Ekonomi")]
    [InlineData("admins")]
    [InlineData("GG-Llm-Admins")]
    [InlineData("rag-")] // the prefix alone
    [InlineData("ragadmins")]
    [InlineData("rag-admins; gateway-admins")] // one bad group in the list is enough
    public void Other_group_names_are_refused_with_the_role_and_group_named(string groups)
    {
        var result = Validate(new() { ["rag-viewer"] = groups });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Oidc:RoleGroups:rag-viewer");
        result.FailureMessage.ShouldContain("'rag-'");
    }

    [Fact]
    public void No_role_groups_is_the_default_and_valid() =>
        new OidcOptionsValidator().Validate(null, new OidcOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void The_admin_api_refuses_to_start_with_a_gateway_style_role_group()
    {
        using var factory = database.Factory().WithWebHostBuilder(web => web.UseSetting("Oidc:RoleGroups:rag-platform-admin", "admins"));

        var thrown = Should.Throw<OptionsValidationException>(() => factory.Services.GetRequiredService<IOptions<OidcOptions>>());
        thrown.Message.ShouldContain("Oidc:RoleGroups:rag-platform-admin names the group 'admins'");
    }

    [Fact]
    public void The_admin_api_starts_with_a_rag_role_group()
    {
        using var factory = database.Factory().WithWebHostBuilder(web => web.UseSetting("Oidc:RoleGroups:rag-platform-admin", "rag-platform-admins"));

        factory.Services.GetRequiredService<IOptions<OidcOptions>>().Value.RoleGroups["rag-platform-admin"].ShouldBe("rag-platform-admins");
    }
}
