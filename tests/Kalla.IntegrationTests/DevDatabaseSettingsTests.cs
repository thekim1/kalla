using Npgsql;
using Kalla.MigrationService;
using Kalla.TestKit;

namespace Kalla.IntegrationTests;

/// <summary>Knowledge design §6.4: in Development the HNSW defaults are set on the database, so every new connection has them.</summary>
public sealed class DevDatabaseSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_hnsw_defaults_reach_a_new_superuser_connection()
    {
        await using var database = new RagDatabase(); // its own: the settings are per database
        await database.InitializeAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await DevDatabaseSettings.ApplyAsync(db, Ct);
            await DevDatabaseSettings.ApplyAsync(db, Ct);
        }

        await using var connection = new NpgsqlConnection(database.Connection(null, pooling: false));
        await connection.OpenAsync(Ct);
        foreach (var (name, value) in DevDatabaseSettings.Hnsw)
        {
            await using var command = new NpgsqlCommand("SELECT current_setting($1, true)", connection) { Parameters = { new() { Value = name } } };
            (await command.ExecuteScalarAsync(Ct)).ShouldBe(value);
        }
    }
}
