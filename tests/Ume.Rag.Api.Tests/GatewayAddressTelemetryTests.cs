using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The gateway's address is a secret (AGENTS.md; issue #232): rag-api, rag-admin and the migration service do not log
/// HttpClient requests at Information, and outgoing spans carry no host, port or query.
/// </summary>
public sealed class GatewayAddressTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Ume.Rag.Api")]
    [InlineData("Ume.Rag.AdminApi")]
    [InlineData("Ume.Rag.MigrationService")]
    public void The_service_does_not_log_http_client_requests_at_information(string project)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("src", project, "appsettings.json"))));
        var levels = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel");
        levels.TryGetProperty("System.Net.Http.HttpClient", out var level).ShouldBeTrue("System.Net.Http.HttpClient needs its own level");
        level.GetString().ShouldBeOneOf(["Warning", "Error", "Critical", "None"], "request URLs (the gateway's address) must not reach the log");
    }

    [Fact]
    public async Task An_outgoing_span_keeps_method_scheme_and_path_but_no_host_port_or_query()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("gateway").RemoveStandardResilienceHandler();
        using var host = builder.Build();
        await host.StartAsync(Ct);

        var finished = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (finished) { finished.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);

        using (var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("gateway"))
        {
            await Should.ThrowAsync<HttpRequestException>(() => client.PostAsync("http://gateway-secret.invalid:8443/v1/embeddings?key=abc", null, Ct));
        }

        await host.StopAsync(Ct);
        Activity span;
        lock (finished)
        {
            span = finished.ShouldHaveSingleItem();
        }

        var tags = span.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        tags.GetValueOrDefault("url.full").ShouldBe("http://redacted/v1/embeddings");
        tags.ShouldNotContainKey("server.address");
        tags.ShouldNotContainKey("server.port");
        tags.ShouldNotContainKey("url.query");
        tags.Values.ShouldAllBe(v => v == null || !v.Contains("gateway-secret", StringComparison.Ordinal));
        span.DisplayName.ShouldNotContain("gateway-secret");
    }
}
