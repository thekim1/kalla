using System.Threading.Channels;
using Npgsql;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Retrieval;

namespace Kalla.Worker.Engine;

/// <summary>What one collection's check did, per embedding profile.</summary>
public sealed record KeywordStatisticsCheck(int Recomputed, int Kept, int Removed);

/// <summary>
/// The keyword stop list's statistics (knowledge design §6.2; #499 step 2, A-29): the worker keeps
/// <c>content."KeywordStatistics"</c>, so kalla-api runs no <c>ts_stat</c>. Per (profile, collection) with at least
/// <see cref="ScopeSizeCache.KeywordStatisticsMinChunks"/> live child chunks, the rows are recomputed with <c>ts_stat</c> when
/// there are none, when the live count moved by more than <see cref="MovedShare"/> from the stored <c>ChunkCount</c>, or when
/// they are older than <see cref="MaxAge"/>; otherwise kept. Under 100 chunks (or no chunks: a deleted collection) the rows go.
/// One collection at a time, on a connection of the worker's pool (inside the role's connection budget, data-model §1.1),
/// with <c>SET LOCAL statement_timeout = 0</c> in the refresh's transaction: <c>ts_stat</c> reads every search vector of the
/// collection. A collection's rows are replaced in
/// one transaction under an advisory lock, so two workers never refresh the same collection at once (the second skips).
/// </summary>
public sealed partial class KeywordStatistics(NpgsqlDataSource dataSource, TimeProvider time, ILogger<KeywordStatistics> logger)
{
    /// <summary>A live count that moved by more than this share of the stored one is recomputed.</summary>
    public const double MovedShare = 0.10;

    /// <summary>Rows older than this are recomputed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const string CountsSql = """
        SELECT "EmbeddingProfileId", count(*) FROM content."Chunks"
        WHERE "CollectionId" = $1 AND "DeletedAt" IS NULL AND "Kind" = 'child' AND "EmbeddingProfileId" IS NOT NULL
        GROUP BY 1
        """;

    private const string StoredSql = """
        SELECT "EmbeddingProfileId", max("ChunkCount"), min("ComputedAt") FROM content."KeywordStatistics" WHERE "CollectionId" = $1 GROUP BY 1
        """;

    private const string CountOneSql = """
        SELECT count(*) FROM content."Chunks"
        WHERE "CollectionId" = $2 AND "EmbeddingProfileId" = $1 AND "DeletedAt" IS NULL AND "Kind" = 'child'
        """;

    private const string DeleteSql = """DELETE FROM content."KeywordStatistics" WHERE "EmbeddingProfileId" = $1 AND "CollectionId" = $2""";

    private const string InsertSql = """
        INSERT INTO content."KeywordStatistics" ("EmbeddingProfileId", "CollectionId", "Lexeme", "Ndoc", "ChunkCount", "ComputedAt")
        SELECT $1, $2, s.word, s.ndoc, $3, $5
        FROM ts_stat(format(
            'SELECT "SearchVector" FROM content."Chunks" WHERE "Kind" = ''child'' AND "DeletedAt" IS NULL AND "EmbeddingProfileId" = %L AND "CollectionId" = %L',
            $1::uuid, $2::uuid)) s
        WHERE s.ndoc >= $3 * $4
        """;

    /// <summary>Every collection that has config or rows: the nightly check (knowledge design §6.2), one at a time.</summary>
    private const string CollectionsSql = """
        SELECT "Id" FROM config."Collections" UNION SELECT DISTINCT "CollectionId" FROM content."KeywordStatistics"
        """;

    /// <summary>Checks one collection's rows and refreshes them as needed.</summary>
    public async Task<KeywordStatisticsCheck> CheckAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var counts = await ReadAsync(connection, CountsSql, collectionId, r => (r.GetGuid(0), r.GetInt64(1)), cancellationToken);
        var stored = (await ReadAsync(connection, StoredSql, collectionId, r => (r.GetGuid(0), (Count: (long)r.GetInt32(1), At: r.GetFieldValue<DateTimeOffset>(2))),
            cancellationToken)).ToDictionary(x => x.Item1, x => x.Item2);
        var now = time.GetUtcNow();
        int recomputed = 0, kept = 0, removed = 0;
        foreach (var (profile, count) in counts)
        {
            if (count < ScopeSizeCache.KeywordStatisticsMinChunks)
            {
                continue; // removed below with the profiles that have no chunks left
            }

            if (stored.TryGetValue(profile, out var rows) && Math.Abs(count - rows.Count) <= rows.Count * MovedShare && now - rows.At <= MaxAge)
            {
                kept++;
                continue;
            }

            if (await RefreshAsync(connection, profile, collectionId, now, cancellationToken))
            {
                recomputed++;
            }
        }

        var live = counts.Where(c => c.Item2 >= ScopeSizeCache.KeywordStatisticsMinChunks).Select(c => c.Item1).ToHashSet();
        foreach (var profile in stored.Keys.Where(p => !live.Contains(p)))
        {
            await using var delete = new NpgsqlCommand(DeleteSql, connection) { Parameters = { Db.P(profile), Db.P(collectionId) } };
            await delete.ExecuteNonQueryAsync(cancellationToken);
            removed++;
        }

        if (recomputed + removed > 0)
        {
            LogChecked(logger, collectionId, recomputed, kept, removed);
        }

        return new KeywordStatisticsCheck(recomputed, kept, removed);
    }

    /// <summary>The nightly maintenance step: every collection, one at a time; a failing one is logged and the rest go on.</summary>
    public async Task<int> CheckAllAsync(CancellationToken cancellationToken)
    {
        List<Guid> collections;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await using var command = new NpgsqlCommand(CollectionsSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            collections = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                collections.Add(reader.GetGuid(0));
            }
        }

        var refreshed = 0;
        foreach (var collection in collections)
        {
            try
            {
                var check = await CheckAsync(collection, cancellationToken);
                refreshed += check.Recomputed + check.Removed;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogFailed(logger, ex, collection);
            }
        }

        return refreshed;
    }

    /// <summary>
    /// Replaces one (profile, collection)'s rows in one transaction: the advisory lock (another worker refreshing it: skip),
    /// <c>statement_timeout = 0</c>, the live count, then <c>ts_stat</c>; a count under the floor only removes the rows.
    /// </summary>
    private static async Task<bool> RefreshAsync(NpgsqlConnection connection, Guid profile, Guid collection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var locked = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtext('content.KeywordStatistics'), hashtext($1::text))", connection, transaction)
        {
            Parameters = { Db.P(collection) },
        })
        {
            if (await locked.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return false;
            }
        }

        await using (var unlimited = new NpgsqlCommand("SET LOCAL statement_timeout = 0", connection, transaction))
        {
            await unlimited.ExecuteNonQueryAsync(cancellationToken);
        }

        long count;
        await using (var counting = new NpgsqlCommand(CountOneSql, connection, transaction) { Parameters = { Db.P(profile), Db.P(collection) } })
        {
            count = (long)(await counting.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var delete = new NpgsqlCommand(DeleteSql, connection, transaction) { Parameters = { Db.P(profile), Db.P(collection) } })
        {
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (count >= ScopeSizeCache.KeywordStatisticsMinChunks)
        {
            await using var insert = new NpgsqlCommand(InsertSql, connection, transaction)
            {
                Parameters = { Db.P(profile), Db.P(collection), Db.P((int)count), Db.P(ScopeSizeCache.KeywordStatisticsShare), Db.P(now.ToUniversalTime()) },
            };
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlConnection connection, string sql, Guid collection, Func<NpgsqlDataReader, T> row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { Db.P(collection) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(row(reader));
        }

        return rows;
    }

    [LoggerMessage(LogLevel.Information, "Keyword statistics of collection {CollectionId}: {Recomputed} recomputed, {Kept} kept, {Removed} removed")]
    private static partial void LogChecked(ILogger logger, Guid collectionId, int recomputed, int kept, int removed);

    [LoggerMessage(LogLevel.Warning, "The keyword statistics check of collection {CollectionId} failed; the next run or night checks it again")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid collectionId);
}

/// <summary>
/// Checks a collection's keyword statistics after a run of one of its sources ends (knowledge design §6.2), one collection at
/// a time and off the finalizer's transaction. In process: a check lost to a restart is made by the nightly maintenance.
/// At start it first checks every collection once (<see cref="KeywordStatistics.CheckAllAsync"/>), so a collection without
/// rows (a new installation, an upgrade, rows lost) gets its stop list within minutes instead of at the next run or night;
/// collections with current rows are only counted. Several workers starting at once skip each other's collections (the
/// advisory lock).
/// </summary>
public sealed partial class KeywordStatisticsService(KeywordStatistics statistics, ILogger<KeywordStatisticsService> logger) : BackgroundService
{
    private readonly Channel<Guid> _collections = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Asks for a check of <paramref name="collectionId"/>.</summary>
    public void Post(Guid collectionId) => _collections.Writer.TryWrite(collectionId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up the host's start
        try
        {
            await statistics.CheckAllAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogStartCheckFailed(logger, ex);
        }

        await foreach (var collection in _collections.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await statistics.CheckAsync(collection, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex, collection);
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, "The keyword statistics check of every collection at start failed; runs and the nightly maintenance check them again")]
    private static partial void LogStartCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "The keyword statistics check of collection {CollectionId} after a run failed; the nightly maintenance checks it again")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid collectionId);
}
