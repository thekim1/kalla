using System.Text.Json;
using Npgsql;

namespace Kalla.Infrastructure.Jobs;

/// <summary>What <c>content.rag_partition_maintain()</c> did; <see cref="DefaultRows"/> &gt; 0 means maintenance lagged.</summary>
public sealed record PartitionMaintenanceResult(int Created, int Dropped, long DefaultRows);

/// <summary>
/// What a content retention run removed; <see cref="Skipped"/> when another instance held the step's lock from the first
/// batch on (it does the work).
/// </summary>
public sealed record ContentRetentionResult(long Deleted, bool Skipped);

/// <summary>
/// The worker's nightly content maintenance (data-model §4.4, §8): run-item partitions, the deletion of terminal jobs and
/// the content retention step for rows without blobs. Each step is idempotent and short, so a second instance or a rerun
/// only finds less to do.
/// </summary>
public sealed class ContentMaintenance(JobDatabase database)
{
    /// <summary>Terminal jobs are kept this long (data-model §8).</summary>
    public static readonly TimeSpan TerminalJobRetention = TimeSpan.FromDays(7);

    public const int TerminalJobBatchSize = 1000;

    /// <summary>Rows per table and call of <c>content.rag_retention_content_step</c>, as the admin's retention batches.</summary>
    public const int ContentRetentionBatchSize = 5000;

    /// <summary>The audit actor of retention runs (data-model §8).</summary>
    public const string RetentionActor = "system:retention";

    public const string RetentionAuditAction = "retention";
    public const string RetentionAuditEntityType = "content";

    /// <summary>The writing service in <c>audit."AuditLog"</c>.</summary>
    public const string AuditService = "kalla-worker";

    /// <summary>Advisory lock key of the job cleanup, one batch at a time across instances.</summary>
    private const long CleanupLockKey = 0x5241_4730_0002;

    /// <summary>Advisory lock key of the content retention step, one batch at a time across instances.</summary>
    private const long ContentRetentionLockKey = 0x5241_4730_0003;

    // -1: another instance holds the step's lock right now.
    private const string ContentRetentionStepSql =
        """SELECT CASE WHEN pg_try_advisory_xact_lock($1) THEN content.rag_retention_content_step($2) ELSE -1 END""";

    private const string RetentionAuditSql = """
        INSERT INTO audit."AuditLog" ("Timestamp", "Actor", "Action", "EntityType", "EntityId", "Details", "Service")
        VALUES (now(), $1, $2, $3, NULL, $4::jsonb, $5)
        """;

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

    /// <summary>
    /// Calls <c>content.rag_retention_content_step</c> (data-model §8; the ages live in the function) with
    /// <see cref="ContentRetentionBatchSize"/> until a call deletes nothing, each call one statement under the step's
    /// transaction advisory lock, then writes the run to <c>audit."AuditLog"</c> as <see cref="RetentionActor"/>. When another
    /// instance holds the lock at the first call it returns <see cref="ContentRetentionResult.Skipped"/> and writes nothing;
    /// when it takes the lock later, this run stops there and audits what it removed.
    /// </summary>
    public async Task<ContentRetentionResult> DeleteExpiredContentAsync(CancellationToken cancellationToken)
    {
        long total = 0;
        var batches = 0;
        while (true)
        {
            long step;
            await using (var connection = await database.OpenAsync(cancellationToken))
            await using (var command = new NpgsqlCommand(ContentRetentionStepSql, connection)
            {
                Parameters = { JobDatabase.Parameter(ContentRetentionLockKey), JobDatabase.Parameter(ContentRetentionBatchSize) },
            })
            {
                step = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            if (step < 0 && batches == 0)
            {
                return new ContentRetentionResult(0, Skipped: true);
            }

            if (step <= 0)
            {
                break;
            }

            batches++;
            total += step;
        }

        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var audit = new NpgsqlCommand(RetentionAuditSql, connection)
        {
            Parameters =
            {
                JobDatabase.Parameter(RetentionActor), JobDatabase.Parameter(RetentionAuditAction), JobDatabase.Parameter(RetentionAuditEntityType),
                JobDatabase.Parameter(JsonSerializer.Serialize(new { deleted = total, batchSize = ContentRetentionBatchSize })),
                JobDatabase.Parameter(AuditService),
            },
        })
        {
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        return new ContentRetentionResult(total, Skipped: false);
    }
}
