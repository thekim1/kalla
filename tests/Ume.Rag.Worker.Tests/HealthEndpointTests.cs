using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Ume.Rag.TestKit;

namespace Ume.Rag.Worker.Tests;

/// <summary>Every service answers the probes and /version (P0 checklist #4); the worker's readiness needs its database.</summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class HealthEndpointTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:ragdb", database.Connection(RagDatabase.Worker))
            .UseSetting("Worker:Maintenance", "false"));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/version")]
    public async Task Endpoint_answers_ok(string path)
    {
        using var client = _factory.CreateClient();

        // Readiness turns green once the config monitor has confirmed the first version.
        var waited = Stopwatch.StartNew();
        HttpStatusCode status;
        while ((status = await GetAsync(client, path)) != HttpStatusCode.OK && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        status.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
