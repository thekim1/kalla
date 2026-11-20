using Npgsql;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The purge of a deleted source's documents (data-model §8; Decisions "Deleting knowledge bases, sources and assistants"):
/// documents of a source with <c>DeletedAt</c> whose <c>PurgeAfter</c> has passed (the 14-day grace
/// <see cref="Commands.DeleteSourceHandler"/> set) are deleted, their chunk sets and chunks with them (the keys cascade), in
/// batches of 1,000. Each document's raw blobs are tombstoned (<c>document.purged</c>), so <see cref="BlobPurgeSweep"/>
/// removes them after the blob purge delay unless another document still has that content. Part of the nightly
/// <see cref="Jobs.MaintenanceService"/>.
/// </summary>
public sealed class DeletedSourcePurge(JobDatabase database, TimeProvider time)
{
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

    /// <summary>Purges every due document, batch by batch; returns how many.</summary>
    public async Task<long> RunAsync(CancellationToken cancellationToken)
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
