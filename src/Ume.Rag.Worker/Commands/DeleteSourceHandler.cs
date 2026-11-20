using System.Text.Json;
using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Commands;

/// <summary>
/// <c>delete-source</c> (Decisions "Deleting knowledge bases, sources and assistants"; data-model §8; admin design §4.5):
/// the admin queues it with its delete of a source. The source's running runs are cancelled as <c>cancel-run</c> cancels
/// one; then, with the job, its documents are soft-deleted as removed ones are (<c>DeletedAt</c>, <c>PurgeAfter</c> after the
/// 14-day grace, the chunks of each live set deleted, so nothing of it is found), and its upload slots are deleted with a
/// tombstone for each blob (<c>source.deleted</c>), which <see cref="BlobPurgeSweep"/> purges after the blob purge delay.
/// <see cref="DeletedSourcePurge"/> removes the documents after their grace. Idempotent: a second run finds nothing left.
/// Refused for a source that is not deleted. Result <see cref="DeleteSourceResult"/>.
/// </summary>
public sealed class DeleteSourceHandler(JobDatabase database, IJobQueue queue, RunStore runs, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string SourceNotFound = "source.not_found";
    public const string SourceNotDeleted = "source.not_deleted";

    /// <summary>The tombstone reason of a deleted source's upload blobs.</summary>
    public const string TombstoneReason = "source.deleted";

    private const string SourceSql = """SELECT "DeletedAt" IS NOT NULL FROM config."Sources" WHERE "Id" = $1""";

    private const string ActiveRunsSql = """
        SELECT "Id" FROM content."Runs" WHERE "SourceId" = $1 AND "State" IN ('running', 'awaiting-deletion-confirmation')
        """;

    private const string SoftDeleteSql = """
        UPDATE content."Documents" SET "DeletedAt" = $2, "PurgeAfter" = $3
         WHERE "SourceId" = $1 AND "DeletedAt" IS NULL
        RETURNING "LiveChunkSetId"
        """;

    private const string SoftDeleteChunksSql = """
        UPDATE content."Chunks" SET "DeletedAt" = $2 WHERE "ChunkSetId" = ANY($1) AND "DeletedAt" IS NULL
        """;

    // The blob key of a slot is uploads/{sourceId}/{uploadId}/{sha256 hex} (UploadConnector.BlobKeyOf).
    private const string DeleteUploadsSql = """
        WITH deleted AS (
            UPDATE content."Uploads" SET "DeletedAt" = $2 WHERE "SourceId" = $1 AND "DeletedAt" IS NULL
            RETURNING "Id", "Sha256"),
        tombstoned AS (
            INSERT INTO content."BlobTombstones" ("Key", "DeletedAt", "Reason")
            SELECT 'uploads/' || $1::text || '/' || "Id"::text || '/' || encode("Sha256", 'hex'), $2, $3 FROM deleted
            ON CONFLICT ("Key") DO NOTHING)
        SELECT count(*)::int FROM deleted
        """;

    public string Kind => CommandKinds.DeleteSource;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<DeleteSourceCommand>(payload) is not { } delete)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var active = new List<Guid>();
        await using (var connection = await database.OpenAsync(cancellationToken))
        {
            await using (var source = new NpgsqlCommand(SourceSql, connection) { Parameters = { Db.P(delete.SourceId) } })
            {
                switch (await source.ExecuteScalarAsync(cancellationToken))
                {
                    case null:
                        return new JobResult.FatalError(SourceNotFound);
                    case false:
                        return new JobResult.FatalError(SourceNotDeleted);
                }
            }

            await using var command = new NpgsqlCommand(ActiveRunsSql, connection) { Parameters = { Db.P(delete.SourceId) } };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                active.Add(reader.GetGuid(0));
            }
        }

        // As cancel-run: the run's jobs first (a running one stops at its next heartbeat without committing), then the run.
        var cancelled = 0;
        foreach (var runId in active)
        {
            cancelled += await queue.CancelRunAsync(runId, CancelRunCommand.CancelledReason, context.Job.Id, cancellationToken);
            await runs.CancelAsync(runId, cancellationToken);
        }

        var now = time.GetUtcNow();
        var at = now.ToUniversalTime();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        var documents = 0;
        var sets = new List<Guid>();
        await using (var softDelete = new NpgsqlCommand(SoftDeleteSql, commit.Connection, commit.Transaction)
        {
            Parameters = { Db.P(delete.SourceId), Db.P(at), Db.P((now + DocumentLifecycle.Grace).ToUniversalTime()) },
        })
        await using (var reader = await softDelete.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                documents++;
                if (Db.Nullable<Guid>(reader, 0) is { } set)
                {
                    sets.Add(set);
                }
            }
        }

        await CommandCommit.ExecuteAsync(commit, SoftDeleteChunksSql, cancellationToken, Db.P(sets.ToArray()), Db.P(at));
        var uploads = (int)(await CommandCommit.ScalarAsync(commit, DeleteUploadsSql, cancellationToken, Db.P(delete.SourceId), Db.P(at), Db.P(TombstoneReason)) ?? 0);
        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity,
            new DeleteSourceResult { Documents = documents, Uploads = uploads, CancelledJobs = cancelled }, cancellationToken);
    }
}
