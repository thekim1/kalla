using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The gateway's address is a secret (AGENTS.md, #232, #265): every named HttpClient of kalla-admin, called against a refused
/// address, leaves no log line with the host, at any level, including Polly's retry telemetry of the standard resilience
/// handler.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class ClientAddressLoggingTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>kalla-admin's named clients that reach the gateway.</summary>
    public static TheoryData<string> Clients => new()
    {
        Infrastructure.Gateway.GatewayApi.HttpClientName, Answering.GatewayQueryEmbedder.HttpClientName,
        Answering.Chat.GatewayChatClient.HttpClientName,
    };

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_failing_call_logs_no_host(string name)
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://gateway-secret.invalid:8443/");
            web.ConfigureTestServices(services => services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        });
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://gateway-secret.invalid:8443/v1/models");
        request.Headers.Add("x-fors-max-residency", "Eu"); // the gateway clients refuse a call without a ceiling; their callers give one

        await Should.ThrowAsync<Exception>(() => client.SendAsync(request, Ct));
        logs.Text.ShouldNotContain("gateway-secret", Case.Insensitive);
    }
}
