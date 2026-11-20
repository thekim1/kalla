extern alias fakegateway;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Kalla.Api.Tests;

/// <summary>
/// The FakeGateway in process, with the three per-level dev keys, the api's, the worker's (up to <c>Eu</c>) and the admin's
/// credential keys (the admin's with <c>organisation.read</c>) (repo-and-deployment §4.3).
/// </summary>
public sealed class FakeGatewayFactory : WebApplicationFactory<fakegateway::Program>
{
    public const string OnPremKey = "fake-onprem-key-0123456789";
    public const string EuKey = "fake-eu-key-0123456789";
    public const string ExternalKey = "fake-external-key-0123456789";
    public const string ApiCredentialKey = "fake-api-credential-key-0123456789";
    public const string WorkerCredentialKey = "fake-worker-credential-key-0123456789";
    public const string AdminCredentialKey = "fake-admin-credential-key-0123456789";

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
            ["FakeGateway:CredentialKeys:api:Key"] = ApiCredentialKey,
            ["FakeGateway:CredentialKeys:worker:Key"] = WorkerCredentialKey,
            ["FakeGateway:CredentialKeys:worker:MaxResidency"] = "Eu",
            ["FakeGateway:CredentialKeys:admin:Key"] = AdminCredentialKey,
            ["FakeGateway:CredentialKeys:admin:Scopes:0"] = "organisation.read",
            ["FakeGateway:StallSeconds"] = "1",
        }));
    }
}
