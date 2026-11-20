using System.Net;
using System.Text.Json;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

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

        using var response = await GetWhenOkAsync(client, path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// /version has the camelCase shape of every other service and the gateway, not rag-api's snake_case API naming (#497).
    /// </summary>
    [Fact]
    public async Task Version_is_camel_case_like_every_service()
    {
        using var api = db.ApiHost();
        using var client = api.CreateHttpsClient();

        using var response = await GetWhenOkAsync(client, "/version");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        root.GetProperty("application").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("informationalVersion").GetString().ShouldNotBeNullOrEmpty();
        root.TryGetProperty("informational_version", out _).ShouldBeFalse();
    }

    // Ready waits for the first confirmed config version (the monitor's first poll).
    private static async Task<HttpResponseMessage> GetWhenOkAsync(HttpClient client, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK || attempt >= 99)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
