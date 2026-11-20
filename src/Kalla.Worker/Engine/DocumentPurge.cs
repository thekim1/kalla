using Npgsql;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Persistence;

namespace Kalla.Worker.Engine;

/// <summary>
/// The purge of soft-deleted documents (data-model §8; connectors design §13.4; Decisions "Deleting knowledge bases, sources
/// and assistants"): every document with <c>DeletedAt</c> whose <c>PurgeAfter</c> has passed is deleted, its chunk sets and
/// chunks with it (the keys cascade), in batches of 1,000, whether a live source no longer has it (the reconcile or
/// <c>confirm-deletion</c> soft-deleted it) or its source was deleted (<see cref="Commands.DeleteSourceHandler"/>). A document
/// seen again within its 14-day grace was revived (<see cref="DocumentStore.UpsertFetchedAsync"/>: <c>DeletedAt</c> cleared)
/// and is not touched. A purged document's raw blobs are tombstoned (<c>document.purged</c>) when no other document of its
/// level has that content (a soft-deleted one counts: it may be revived, and it tombstones them at its own purge), so
/// <see cref="BlobPurgeSweep"/> removes them after the blob purge delay. Part of the nightly <see cref="Jobs.MaintenanceService"/>.
/// First it queues <c>delete-source</c> for every deleted source that still has a document or an upload slot that is not
/// deleted (one deleted before the admin queued the command, or a command lost), with the command's dedup key, so no deleted
/// source keeps live content for longer than a night.
/// </summary>
public sealed class DocumentPurge(JobDatabase database, IJobQueue queue, TimeProvider time)
{
    /// <summary>What one run did.</summary>
    public sealed record Outcome(int Queued, long Purged);

    // The job's department and level as the admin's command has them: the collection's department, the source's level.
    private const string MissedSql = """
        SELECT s."Id", c."DepartmentId", s."Classification"
          FROM config."Sources" s JOIN config."Collections" c ON c."Id" = s."CollectionId"
         WHERE s."DeletedAt" IS NOT NULL
           AND (EXISTS (SELECT 1 FROM content."Documents" d WHERE d."SourceId" = s."Id" AND d."DeletedAt" IS NULL)
                OR EXISTS (SELECT 1 FROM content."Uploads" u WHERE u."SourceId" = s."Id" AND u."DeletedAt" IS NULL))
         LIMIT $1
        """;

    public const int BatchSize = 1000;

    /// <summary>The tombstone reason of a purged document's raw blobs.</summary>
    public const string TombstoneReason = "document.purged";

    // raw/{level}/{contentHash} and the crawl's raw/{level}/{contentHash}.links.json.gz (RawContentWriter, PageLinks). The
    // statement's reads see the documents before its delete, so the batch's own documents are left out of the "other document"
    // check: two purged together with one content tombstone it.
    private const string PurgeSql = """
        WITH gone AS (
            DELETE FROM content."Documents" WHERE "Id" IN (
                SELECT "Id" FROM content."Documents"
                 WHERE "DeletedAt" IS NOT NULL AND "PurgeAfter" <= $1
                 ORDER BY "PurgeAfter"
                 LIMIT $2
                 FOR UPDATE SKIP LOCKED)
            RETURNING "Id", "Classification", "ContentHash"),
        unshared AS (
            SELECT DISTINCT g."Classification", g."ContentHash" FROM gone g
             WHERE g."ContentHash" IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM content."Documents" d
                                WHERE d."Classification" = g."Classification" AND d."ContentHash" = g."ContentHash"
                                  AND NOT EXISTS (SELECT 1 FROM gone x WHERE x."Id" = d."Id"))),
        keys AS (
            SELECT 'raw/' || "Classification" || '/' || encode("ContentHash", 'hex') || suffix AS "Key"
              FROM unshared CROSS JOIN (VALUES (''), ('.links.json.gz')) AS s(suffix)),
        tombstoned AS (
            INSERT INTO content."BlobTombstones" ("Key", "DeletedAt", "Reason")
            SELECT "Key", $1, $3 FROM keys
            ON CONFLICT ("Key") DO NOTHING)
        SELECT count(*)::int FROM gone
        """;

    /// <summary>Queues the missed commands, then purges every due document, batch by batch.</summary>
    public async Task<Outcome> RunAsync(CancellationToken cancellationToken)
    {
        var requests = new List<JobRequest>();
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(MissedSql, connection) { Parameters = { Db.P(BatchSize) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceId = reader.GetGuid(0);
                requests.Add(new JobRequest(JobClasses.Command, reader.GetGuid(1), reader.GetString(2), ContractJson.Serialize(new DeleteSourceCommand { SourceId = sourceId }))
                {
                    DedupKey = JobDedupKeys.DeleteSource(sourceId),
                });
            }
        }

        var queued = requests.Count == 0 ? 0 : (await queue.EnqueueAsync(requests, null, cancellationToken)).Count;
        return new Outcome(queued, await PurgeAsync(cancellationToken));
    }

    private async Task<long> PurgeAsync(CancellationToken cancellationToken)
    {
        long purged = 0;
        while (true)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(PurgeSql, connection)
            {
                Parameters = { Db.P(time.GetUtcNow().ToUniversalTime()), Db.P(BatchSize), Db.P(TombstoneReason) },
            };
            var batch = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
            purged += batch;
            if (batch < BatchSize)
            {
                return purged;
            }
        }
    }
}
