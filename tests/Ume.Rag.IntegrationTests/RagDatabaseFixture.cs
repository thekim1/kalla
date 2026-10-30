using Npgsql;
using Ume.Rag.TestKit;

namespace Ume.Rag.IntegrationTests;

/// <summary>One ragdb with the production roles for every test class of the collection.</summary>
public sealed class RagDatabaseFixture : RagDatabase, IAsyncLifetime;

[CollectionDefinition(Name)]
public sealed class RagDatabaseGroup : ICollectionFixture<RagDatabaseFixture>
{
    public const string Name = "ragdb";
}

/// <summary>Small ADO.NET helpers: each call opens its own connection, as a separate service request would.</summary>
internal static class Sql
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull or null ? default! : (T)value;
    }

    public static async Task<List<string>> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static async Task ShouldBeDeniedAsync(string connectionString, string sql)
    {
        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
