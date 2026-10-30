using System.Collections.Concurrent;
using Npgsql;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.TestKit;

namespace Ume.Rag.Worker.Tests;

/// <summary>
/// One ragdb with the production roles. Every test class that touches it is in this collection, so they run one after the
/// other: the queue is shared, and the connection-budget test counts every worker-role connection.
/// </summary>
public sealed class RagDatabaseFixture : RagDatabase, IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new(StringComparer.Ordinal);

    /// <summary>
    /// Removes every job and instance, so a test sees only its own, and closes the idle pooled connections earlier tests
    /// left, so each test starts with the worker role's whole connection cap.
    /// </summary>
    public async Task ResetAsync()
    {
        await Sql.ExecuteAsync(Connection(null), """DELETE FROM content."Jobs"; DELETE FROM content."WorkerInstances";""");
        await DisposeDataSourcesAsync();
        NpgsqlConnection.ClearAllPools();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await Sql.ScalarAsync<long>(Connection(null), "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ume_rag_worker'") > 0)
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "worker-role connections from an earlier test are still open");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The queue as <paramref name="role"/> sees it, outside any host.</summary>
    public PostgresJobQueue Queue(string role) => new(Database(role), new JobWakeUps(TimeProvider.System));

    /// <summary>The queue's connections as <paramref name="role"/>, from one data source per role, as a process has.</summary>
    public JobDatabase Database(string role) => new(_dataSources.GetOrAdd(role, r => NpgsqlDataSource.Create(Connection(r))));

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeDataSourcesAsync();
        await DisposeAsync();
    }

    private async Task DisposeDataSourcesAsync()
    {
        foreach (var role in _dataSources.Keys)
        {
            if (_dataSources.TryRemove(role, out var dataSource))
            {
                await dataSource.DisposeAsync();
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RagDatabaseGroup : ICollectionFixture<RagDatabaseFixture>
{
    public const string Name = "ragdb";
}

/// <summary>Small ADO.NET helpers: each call opens its own connection.</summary>
internal static class Sql
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task ExecuteAsync(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull or null ? default! : (T)value;
    }

    /// <summary>One job row as the database holds it.</summary>
    public static async Task<JobRow> JobAsync(string connectionString, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, """
            SELECT "State", "Attempts", "Reclaims", "LeaseOwner", "LastError", "Result"::text,
                   extract(epoch FROM "AvailableAt" - now())::float8, "CompletedAt" IS NOT NULL, "DepartmentId"
            FROM content."Jobs" WHERE "Id" = $1
            """, [id]);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            throw new InvalidOperationException($"Job {id} not found.");
        }

        return new JobRow(reader.GetString(0), reader.GetInt16(1), reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            TimeSpan.FromSeconds(reader.GetDouble(6)), reader.GetBoolean(7), reader.GetGuid(8));
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return command;
    }
}

/// <summary>A job row; <see cref="AvailableIn"/> is <c>AvailableAt − now()</c> on the database clock.</summary>
internal sealed record JobRow(string State, short Attempts, short Reclaims, Guid? LeaseOwner, string? LastError, string? Result,
    TimeSpan AvailableIn, bool Completed, Guid DepartmentId);
