using Npgsql;
using Ume.Rag.TestKit;

namespace Ume.Rag.IntegrationTests;

/// <summary>The pinned pgvector image starts and accepts a connection (Stage 0 smoke test).</summary>
public sealed class TestPostgresSmokeTests
{
    [Fact]
    public async Task Pinned_image_accepts_a_connection_and_offers_pgvector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var container = TestPostgres.Builder().Build();
        await container.StartAsync(ct);

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT default_version FROM pg_available_extensions WHERE name = 'vector'", connection);
        var version = await command.ExecuteScalarAsync(ct);

        version.ShouldBe("0.8.7");
    }
}
