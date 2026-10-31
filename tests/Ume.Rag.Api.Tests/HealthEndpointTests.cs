using System.Net;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>Every service answers the probes and /version (P0 checklist #4); rag-api now needs its database and keys to start.</summary>
public sealed class HealthEndpointTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/version")]
    public async Task Endpoint_answers_ok(string path)
    {
        using var api = db.ApiHost();
        using var client = api.CreateHttpsClient();

        // Ready waits for the first confirmed config version (the monitor's first poll).
        var status = HttpStatusCode.ServiceUnavailable;
        for (var attempt = 0; attempt < 100 && status != HttpStatusCode.OK; attempt++)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            status = response.StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        status.ShouldBe(HttpStatusCode.OK);
    }
}
