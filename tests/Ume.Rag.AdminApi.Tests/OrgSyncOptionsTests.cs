using Microsoft.Extensions.Configuration;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Compose passes <c>OrgSync__BaseUrl: ${UME_RAG_ORG_SYNC_URL:-}</c> (#453): an empty value must leave the syncs off,
/// as an unset one does, and a set one must bind as the Data API's absolute address.
/// </summary>
public sealed class OrgSyncOptionsTests
{
    private static OrgSyncOptions Bind(string? baseUrl)
    {
        var values = new Dictionary<string, string?> { ["OrgSync:IntervalMinutes"] = "5" };
        if (baseUrl is not null)
        {
            values["OrgSync:BaseUrl"] = baseUrl;
        }

        var options = new OrgSyncOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(OrgSyncOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void An_empty_or_missing_base_url_leaves_the_sync_off()
    {
        Bind(null).BaseUrl.ShouldBeNull();
        Bind("").BaseUrl.ShouldBeNull();
    }

    [Fact]
    public void A_set_base_url_binds_as_an_absolute_address()
    {
        Bind("https://data.example.se").BaseUrl.ShouldBe(new Uri("https://data.example.se"));
    }
}
