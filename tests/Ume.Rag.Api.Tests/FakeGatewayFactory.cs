extern alias fakegateway;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ume.Rag.Api.Tests;

/// <summary>The FakeGateway in process, with the three per-level dev keys and a Data API key (repo-and-deployment §4.3).</summary>
public sealed class FakeGatewayFactory : WebApplicationFactory<fakegateway::Program>
{
    public const string OnPremKey = "fake-onprem-key-0123456789";
    public const string EuKey = "fake-eu-key-0123456789";
    public const string ExternalKey = "fake-external-key-0123456789";
    public const string DataApiKey = "ume-dk-fake-0123456789";

    public static IReadOnlyDictionary<string, string?> Keys { get; } = new Dictionary<string, string?>
    {
        ["OnPrem"] = OnPremKey,
        ["Eu"] = EuKey,
        ["External"] = ExternalKey,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FakeGateway:Keys:OnPrem"] = OnPremKey,
            ["FakeGateway:Keys:Eu"] = EuKey,
            ["FakeGateway:Keys:External"] = ExternalKey,
            ["FakeGateway:DataApiKey"] = DataApiKey,
            ["FakeGateway:StallSeconds"] = "1",
        }));
    }
}
