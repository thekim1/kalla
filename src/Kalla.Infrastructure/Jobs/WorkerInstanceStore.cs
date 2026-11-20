using Npgsql;

namespace Kalla.Infrastructure.Jobs;

/// <summary>
/// <c>content."WorkerInstances"</c> (engine design §5.2): a running worker registers, heartbeats and removes itself. The
/// reaper judges an instance by its heartbeat on the database clock.
/// </summary>
public sealed class WorkerInstanceStore(JobDatabase database)
{
    // A heartbeat recreates the row when another instance removed it as silent (for example after a long pause), so the
    // instance is visible again; the jobs it lost meanwhile were reclaimed and fail their lease checks.
    private const string UpsertSql = """
        INSERT INTO content."WorkerInstances" ("InstanceId", "BuildVersion", "StartedAt", "LastHeartbeatAt", "Classes")
        VALUES ($1, $2, now(), now(), $3)
        ON CONFLICT ("InstanceId") DO UPDATE SET "LastHeartbeatAt" = now()
        """;

    private const string DeleteSql = """DELETE FROM content."WorkerInstances" WHERE "InstanceId" = $1""";

    /// <summary>Registers the instance, or renews its heartbeat when it is registered.</summary>
    public async Task HeartbeatAsync(Guid instanceId, string buildVersion, IReadOnlyList<string> classes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildVersion);
        ArgumentNullException.ThrowIfNull(classes);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(UpsertSql, connection)
        {
            Parameters =
            {
                JobDatabase.Parameter(instanceId),
                JobDatabase.Parameter(buildVersion.Length <= 100 ? buildVersion : buildVersion[..100]),
                JobDatabase.Parameter(classes.ToArray()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Removes the instance on a clean stop.</summary>
    public async Task DeleteAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DeleteSql, connection) { Parameters = { JobDatabase.Parameter(instanceId) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
