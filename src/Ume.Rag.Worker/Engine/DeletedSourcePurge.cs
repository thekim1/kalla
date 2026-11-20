using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The purge of a deleted source's documents (data-model §8; Decisions "Deleting knowledge bases, sources and assistants"):
/// documents of a source with <c>DeletedAt</c> whose <c>PurgeAfter</c> has passed (the 14-day grace
/// <see cref="Commands.DeleteSourceHandler"/> set) are deleted, their chunk sets and chunks with them (the keys cascade), in
/// batches of 1,000. Each document's raw blobs are tombstoned (<c>document.purged</c>), so <see cref="BlobPurgeSweep"/>
/// removes them after the blob purge delay unless another document still has that content. Part of the nightly
/// <see cref="Jobs.MaintenanceService"/>. First it queues <c>delete-source</c> for every deleted source that still has a
/// document or an upload slot that is not deleted (one deleted before the admin queued the command, or a command lost), with
/// the command's dedup key, so no deleted source keeps live content for longer than a night.
/// </summary>
public sealed class DeletedSourcePurge(JobDatabase database, IJobQueue queue, TimeProvider time)
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

    // raw/{level}/{contentHash} and the crawl's raw/{level}/{contentHash}.links.json.gz (RawContentWriter, PageLinks).
    private const string PurgeSql = """
        WITH gone AS (
            DELETE FROM content."Documents" WHERE "Id" IN (
                SELECT d."Id" FROM content."Documents" d
                  JOIN config."Sources" s ON s."Id" = d."SourceId"
                 WHERE s."DeletedAt" IS NOT NULL AND d."DeletedAt" IS NOT NULL AND d."PurgeAfter" <= $1
                 ORDER BY d."PurgeAfter"
                 LIMIT $2
                 FOR UPDATE OF d SKIP LOCKED)
            RETURNING "Classification", "ContentHash"),
        keys AS (
            SELECT DISTINCT 'raw/' || "Classification" || '/' || encode("ContentHash", 'hex') || suffix AS "Key"
              FROM gone CROSS JOIN (VALUES (''), ('.links.json.gz')) AS s(suffix)
             WHERE "ContentHash" IS NOT NULL),
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
