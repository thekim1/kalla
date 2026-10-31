using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>What one purge sweep did.</summary>
/// <param name="Purged">Blobs deleted from the store, their tombstones removed.</param>
/// <param name="Kept">Tombstones dropped without a purge because the key is referenced again.</param>
public sealed record PurgeResult(long Purged, long Kept);

/// <summary>
/// The blob-tombstone purge (data-model §8, §9; issue #34): tombstones older than <c>Settings.BlobPurgeDelayDays</c> (35),
/// oldest first, 1,000 per transaction under an advisory lock, so every restore point within the backup retention still
/// finds its blobs. A tombstoned key that a row references again (the same content fetched or uploaded since, a cache row
/// re-created) is <b>never purged</b>: its tombstone is dropped. Otherwise the blob is deleted (idempotent) and then its row.
/// The reference checks by area: <c>raw/</c> a document of that level and content hash (soft-deleted ones count: they may be
/// revived), <c>stages/</c> and <c>parse/</c> the <c>NodeOutputs</c> row of the cache key, <c>uploads/</c> the live slot with
/// that hash, <c>quarantine/</c> its <c>QuarantinedFiles</c> row.
/// </summary>
public sealed partial class BlobPurgeSweep(JobDatabase database, IBlobStore blobs, EngineSettings settings, ILogger<BlobPurgeSweep> logger)
{
    public const int BatchSize = 1000;

    private const long LockKey = 0x5241_4731_0003;

    private const string BatchSql = """
        SELECT "Key" FROM content."BlobTombstones" WHERE "DeletedAt" < now() - make_interval(days => $1)
        ORDER BY "DeletedAt" LIMIT $2 FOR UPDATE SKIP LOCKED
        """;

    private const string DeleteRowsSql = """DELETE FROM content."BlobTombstones" WHERE "Key" = ANY($1)""";

    public async Task<PurgeResult> RunAsync(CancellationToken cancellationToken)
    {
        var days = (await settings.GetAsync(cancellationToken)).BlobPurgeDelayDays;
        long purged = 0, kept = 0;
        while (true)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } })
            {
                if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    return new PurgeResult(purged, kept);
                }
            }

            var keys = new List<string>();
            await using (var batch = new NpgsqlCommand(BatchSql, connection, transaction) { Parameters = { Db.P(days), Db.P(BatchSize) } })
            await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    keys.Add(reader.GetString(0));
                }
            }

            foreach (var key in keys)
            {
                if (await ReferencedAsync(connection, transaction, key, cancellationToken))
                {
                    kept++;
                    LogKept(logger, key);
                }
                else
                {
                    if (BlobKey.TryCreate(key, out var blob) && blob.IsBlob)
                    {
                        await blobs.DeleteAsync(blob, cancellationToken);
                    }

                    purged++;
                }
            }

            await using (var delete = new NpgsqlCommand(DeleteRowsSql, connection, transaction) { Parameters = { Db.P(keys.ToArray()) } })
            {
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            if (keys.Count < BatchSize)
            {
                return new PurgeResult(purged, kept);
            }
        }
    }

    /// <summary>Whether a row still references the key (see the class remarks); an unparsable key is unreferenced.</summary>
    internal static async Task<bool> ReferencedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken)
    {
        var parts = key.Split('/');
        (string Sql, NpgsqlParameter[] Parameters)? check = parts switch
        {
            ["raw", var level, var file] when Hash(file) is { } hash =>
                ("""SELECT EXISTS (SELECT 1 FROM content."Documents" WHERE "Classification" = $1 AND "ContentHash" = $2)""", [Db.P(level), Db.P(hash)]),
            ["stages", _, _, var file] when Hash(file) is { } cacheKey =>
                ("""SELECT EXISTS (SELECT 1 FROM content."NodeOutputs" WHERE "CacheKey" = $1)""", [Db.P(cacheKey)]),
            ["parse", _, var prefix, ..] when Hash(prefix) is { } cacheKey =>
                ("""SELECT EXISTS (SELECT 1 FROM content."NodeOutputs" WHERE "CacheKey" = $1)""", [Db.P(cacheKey)]),
            ["uploads", var source, var slot, var file] when Guid.TryParse(source, out var sourceId) && Guid.TryParse(slot, out var slotId) && Hash(file) is { } sha =>
                ("""SELECT EXISTS (SELECT 1 FROM content."Uploads" WHERE "Id" = $1 AND "SourceId" = $2 AND "Sha256" = $3 AND "DeletedAt" IS NULL)""",
                    [Db.P(slotId), Db.P(sourceId), Db.P(sha)]),
            ["quarantine", var source, var file] when Guid.TryParse(source, out var sourceId) && Hash(file) is { } sha =>
                ("""SELECT EXISTS (SELECT 1 FROM content."QuarantinedFiles" WHERE "SourceId" = $1 AND "Sha256" = $2)""", [Db.P(sourceId), Db.P(sha)]),
            _ => null,
        };
        if (check is not { } query)
        {
            return false;
        }

        await using var command = new NpgsqlCommand(query.Sql, connection, transaction);
        command.Parameters.AddRange(query.Parameters);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>The 32-byte hash a key segment starts with (<c>{hex}</c>, <c>{hex}.json.gz</c>, <c>{hex}.links.json.gz</c>), or null.</summary>
    private static byte[]? Hash(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var hex = dot < 0 ? segment : segment[..dot];
        if (hex.Length != 64)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [LoggerMessage(LogLevel.Information, "Tombstoned blob {Key} is referenced again; it is kept and its tombstone dropped")]
    private static partial void LogKept(ILogger logger, string key);
}
