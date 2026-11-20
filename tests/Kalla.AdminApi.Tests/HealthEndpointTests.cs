using System.Net;

namespace Kalla.AdminApi.Tests;

/// <summary>Every service answers the probes and /version (P0 checklist #4); the admin API now needs its database.</summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class HealthEndpointTests(AdminDatabaseFixture database)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/version")]
    public async Task Endpoint_answers_ok(string path)
    {
        await using var factory = database.Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
