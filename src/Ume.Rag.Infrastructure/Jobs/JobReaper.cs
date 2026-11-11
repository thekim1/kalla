using Npgsql;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>A job the reaper took back: <see cref="State"/> is <c>queued</c>, or <c>dead</c> for a poison suspect.</summary>
public sealed record ReclaimedJob(Guid Id, string Class, string State, short Reclaims);

/// <summary>One reaper pass; <see cref="Ran"/> is false when another instance held the reaper lock.</summary>
public sealed record ReapResult(bool Ran, IReadOnlyList<ReclaimedJob> Reclaimed, int InstancesRemoved);

/// <summary>
/// The reaper (engine design §9.4, data-model §4.2). A <c>running</c> job whose lease expired, or whose owner has not
/// heartbeated for the instance timeout (30 s), goes back to <c>queued</c> without using an attempt
/// (<c>Attempts − 1</c>, undoing the dequeue's count) and with <c>Reclaims + 1</c>; at the third reclaim it is
/// dead-lettered as a poison suspect (<c>LastError = 'poison-suspect'</c>), so a document that kills its worker dies
/// instead of looping. Instances silent for 5 minutes are removed. Any instance may run it: one pass at a time under a
/// transaction-scoped advisory lock (pooler-safe). The classes of the requeued jobs are notified on <c>rag_jobs</c> at the
/// commit, so idle workers elsewhere take them at once; the caller wakes its own process.
/// </summary>
public sealed class JobReaper(JobDatabase database)
{
    public const string PoisonSuspect = "poison-suspect";
    public const short MaxReclaims = 3;
    public static readonly TimeSpan SilentInstanceRemoval = TimeSpan.FromMinutes(5);

    /// <summary>Advisory lock key of the reaper (gateway <c>ModelListSync</c> pattern).</summary>
    public const long LockKey = 0x5241_4730_0001;

    private const string ReclaimSql = """
        WITH victims AS (
            SELECT j."Id" FROM content."Jobs" j
            WHERE j."State" = 'running'
              AND (j."LeaseUntil" < now()
                   OR NOT EXISTS (SELECT 1 FROM content."WorkerInstances" w
                                  WHERE w."InstanceId" = j."LeaseOwner" AND w."LastHeartbeatAt" >= now() - $1::interval))
            FOR UPDATE SKIP LOCKED
        )
        UPDATE content."Jobs" j SET
            "Reclaims" = j."Reclaims" + 1,
            "Attempts" = greatest(j."Attempts" - 1, 0),
            "State" = CASE WHEN j."Reclaims" + 1 >= $2 THEN 'dead' ELSE 'queued' END,
            "LastError" = CASE WHEN j."Reclaims" + 1 >= $2 THEN $3 ELSE j."LastError" END,
            "CompletedAt" = CASE WHEN j."Reclaims" + 1 >= $2 THEN now() END,
            "AvailableAt" = CASE WHEN j."Reclaims" + 1 >= $2 THEN j."AvailableAt" ELSE now() END,
            "LeaseOwner" = CASE WHEN j."Reclaims" + 1 >= $2 THEN j."LeaseOwner" END,
            "LeaseUntil" = CASE WHEN j."Reclaims" + 1 >= $2 THEN j."LeaseUntil" END
        FROM victims WHERE j."Id" = victims."Id"
        RETURNING j."Id", j."Class", j."State", j."Reclaims"
        """;

    private const string RemoveSilentSql = """DELETE FROM content."WorkerInstances" WHERE "LastHeartbeatAt" < now() - $1::interval""";

    /// <summary>Runs one pass with <paramref name="instanceTimeout"/> as the heartbeat limit (default 30 s).</summary>
    public async Task<ReapResult> ReapAsync(TimeSpan instanceTimeout, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction)
        {
            Parameters = { JobDatabase.Parameter(LockKey) },
        })
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return new ReapResult(false, [], 0);
            }
        }

        var reclaimed = new List<ReclaimedJob>();
        await using (var reclaim = new NpgsqlCommand(ReclaimSql, connection, transaction)
        {
            Parameters = { JobDatabase.Parameter(instanceTimeout), JobDatabase.Parameter(MaxReclaims), JobDatabase.Parameter(PoisonSuspect) },
        })
        {
            await using var reader = await reclaim.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                reclaimed.Add(new ReclaimedJob(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt16(3)));
            }
        }

        await JobDatabase.NotifyAsync(connection, transaction,
            [.. reclaimed.Where(j => j.State == JobStates.Queued).Select(j => j.Class).Distinct(StringComparer.Ordinal)], cancellationToken);

        int removed;
        await using (var remove = new NpgsqlCommand(RemoveSilentSql, connection, transaction)
        {
            Parameters = { JobDatabase.Parameter(SilentInstanceRemoval) },
        })
        {
            removed = await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReapResult(true, reclaimed, removed);
    }
}
