using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ume.Rag.Api.Tests;

/// <summary>Every service answers the probes and /version (P0 checklist #4).</summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/version")]
    public async Task Endpoint_answers_ok(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
