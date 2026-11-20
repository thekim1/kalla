using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// #865: the one service file renders <c>Admin__GatewayAdminUrl</c> (and can render <c>Admin__ApiPublicUrl</c>) as an empty
/// string when no host or domain is set; the admin API starts with the address unset instead of failing <c>[Url]</c>.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class AdminOptionsAddressTests(AdminDatabaseFixture database)
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_admin_api_starts_with_empty_addresses_as_unset(string empty)
    {
        using var factory = database.Factory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Admin:GatewayAdminUrl", empty);
            web.UseSetting("Admin:ApiPublicUrl", empty);
        });

        var options = factory.Services.GetRequiredService<IOptions<AdminOptions>>().Value;
        options.GatewayAdminUrl.ShouldBeNull();
        options.ApiPublicUrl.ShouldBeNull();
    }

    [Fact]
    public void A_set_address_is_kept_and_one_that_is_not_a_url_still_refuses_the_start()
    {
        using (var factory = database.Factory().WithWebHostBuilder(web => web.UseSetting("Admin:GatewayAdminUrl", "https://llm-admin.example.se")))
        {
            factory.Services.GetRequiredService<IOptions<AdminOptions>>().Value.GatewayAdminUrl.ShouldBe("https://llm-admin.example.se");
        }

        using var bad = database.Factory().WithWebHostBuilder(web => web.UseSetting("Admin:GatewayAdminUrl", "llm-admin.example.se"));
        Should.Throw<OptionsValidationException>(() => bad.Services.GetRequiredService<IOptions<AdminOptions>>()).Message.ShouldContain("GatewayAdminUrl");
    }
}
