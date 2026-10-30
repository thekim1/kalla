using Npgsql;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>What <c>content.rag_partition_maintain()</c> did; <see cref="DefaultRows"/> &gt; 0 means maintenance lagged.</summary>
public sealed record PartitionMaintenanceResult(int Created, int Dropped, long DefaultRows);

/// <summary>
/// The worker's nightly content maintenance that P0 has tables for (data-model §4.4, §8): run-item partitions and the
/// deletion of terminal jobs. Each step is idempotent and short, so a second instance or a rerun only finds less to do.
/// </summary>
public sealed class ContentMaintenance(JobDatabase database)
{
    /// <summary>Terminal jobs are kept this long (data-model §8).</summary>
    public static readonly TimeSpan TerminalJobRetention = TimeSpan.FromDays(7);

    public const int TerminalJobBatchSize = 1000;

    /// <summary>Advisory lock key of the job cleanup, one batch at a time across instances.</summary>
    private const long CleanupLockKey = 0x5241_4730_0002;

    // Oldest first through IX_Jobs_Terminal ("CompletedAt" WHERE terminal state).
    private const string DeleteBatchSql = """
        DELETE FROM content."Jobs" WHERE "Id" IN (
            SELECT "Id" FROM content."Jobs"
            WHERE "State" IN ('succeeded','failed','dead','cancelled') AND "CompletedAt" < now() - $1::interval
            ORDER BY "CompletedAt"
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        """;

    /// <summary>Creates run-item partitions for today + 7 days and drops expired ones (the function serialises itself).</summary>
    public async Task<PartitionMaintenanceResult> MaintainPartitionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT created, dropped, default_rows FROM content.rag_partition_maintain()", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new PartitionMaintenanceResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
    }

    /// <summary>
    /// Deletes terminal jobs older than <paramref name="olderThan"/> in batches of <see cref="TerminalJobBatchSize"/>, each
    /// its own transaction, until a batch comes back short or another instance holds the batch lock. Returns the count.
    /// </summary>
    public async Task<long> DeleteTerminalJobsAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction)
            {
                Parameters = { JobDatabase.Parameter(CleanupLockKey) },
            })
            {
                if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    return total;
                }
            }

            int deleted;
            await using (var delete = new NpgsqlCommand(DeleteBatchSql, connection, transaction)
            {
                Parameters = { JobDatabase.Parameter(olderThan), JobDatabase.Parameter(TerminalJobBatchSize) },
            })
            {
                deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            total += deleted;
            if (deleted < TerminalJobBatchSize)
            {
                return total;
            }
        }
    }
}
