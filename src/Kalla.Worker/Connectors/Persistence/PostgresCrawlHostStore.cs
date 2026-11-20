using Npgsql;
using Kalla.Infrastructure.Jobs;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Robots;

namespace Kalla.Worker.Connectors.Persistence;

/// <summary>Plain-SQL helpers of the connector stores: positional parameters and text forms of the enums.</summary>
internal static class Db
{
    public static NpgsqlParameter<T> P<T>(T value) => new() { TypedValue = value };

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>The lower-case text a CHECK constraint lists (<c>Pending</c> → <c>pending</c>).</summary>
    public static string Text<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    public static TEnum Enum<TEnum>(string text) where TEnum : struct, Enum => System.Enum.Parse<TEnum>(text, ignoreCase: true);

    public static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();

    public static T? Nullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    public static string? String(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

/// <summary>
/// <see cref="ICrawlHostStore"/> over <c>content."CrawlHosts"</c> (data-model §2.2, connectors design §6.3) on the worker's
/// pool. The slot is the design's single <c>UPDATE … RETURNING</c> on the database clock, so every source, preview and
/// instance shares one budget per host (when it takes no slot, a read of the free time follows); responses are applied
/// with <see cref="HostBudgetPolicy.Apply"/> under a row lock.
/// </summary>
public sealed class PostgresCrawlHostStore(JobDatabase database) : ICrawlHostStore
{
    private const string Columns = """
        "Host", "Kind", "RequestsPerSecond", "MaxConnections", "NextAllowedAt", "RobotsStatus", "RobotsFetchedAt",
        "CrawlDelaySeconds", "BackoffFactor", "PausedUntil", "LatencyEwmaMs", "BlockedAt"
        """;

    private const string EnsureSql = $"""
        INSERT INTO content."CrawlHosts" ("Host", "Kind", "RequestsPerSecond", "MaxConnections", "NextAllowedAt", "BackoffFactor")
        VALUES ($1, $2, $3, $4, now(), 1)
        ON CONFLICT ("Host") DO UPDATE
           SET "Kind" = EXCLUDED."Kind", "RequestsPerSecond" = EXCLUDED."RequestsPerSecond", "MaxConnections" = EXCLUDED."MaxConnections"
         WHERE (content."CrawlHosts"."Kind", content."CrawlHosts"."RequestsPerSecond", content."CrawlHosts"."MaxConnections")
               IS DISTINCT FROM (EXCLUDED."Kind", EXCLUDED."RequestsPerSecond", EXCLUDED."MaxConnections")
        RETURNING {Columns}
        """;

    private const string SelectSql = $"""SELECT {Columns} FROM content."CrawlHosts" WHERE "Host" = $1""";

    private const string LockSql = $"""SELECT {Columns}, now() FROM content."CrawlHosts" WHERE "Host" = $1 FOR UPDATE""";

    // Connectors design §6.3, taking the slot only when it is at most $3 away (HostBudgetPolicy.TakeSlot).
    private const string SlotSql = """
        UPDATE content."CrawlHosts"
           SET "NextAllowedAt" = greatest("NextAllowedAt", now()) + $2::interval
         WHERE "Host" = $1 AND "BlockedAt" IS NULL AND ("PausedUntil" IS NULL OR "PausedUntil" < now())
           AND greatest("NextAllowedAt", now()) <= now() + $3::interval
        RETURNING "NextAllowedAt" - $2::interval
        """;

    // No slot taken: the next free time, left as it is; no row when the host is blocked, paused or unknown.
    private const string FreeSql = """
        SELECT greatest("NextAllowedAt", now()) FROM content."CrawlHosts"
         WHERE "Host" = $1 AND "BlockedAt" IS NULL AND ("PausedUntil" IS NULL OR "PausedUntil" < now())
        """;

    private const string ApplySql = """
        UPDATE content."CrawlHosts"
           SET "NextAllowedAt" = $2, "BackoffFactor" = $3, "PausedUntil" = $4, "LatencyEwmaMs" = $5
         WHERE "Host" = $1
        """;

    private const string PauseSql = """
        UPDATE content."CrawlHosts" SET "PausedUntil" = greatest(coalesce("PausedUntil", $2), $2) WHERE "Host" = $1
        """;

    private const string RobotsSql = """
        UPDATE content."CrawlHosts" SET "RobotsStatus" = $2, "RobotsFetchedAt" = $3, "CrawlDelaySeconds" = $4 WHERE "Host" = $1
        """;

    public async Task<CrawlHostState> EnsureAsync(CrawlHostState initial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(EnsureSql, connection)
        {
            Parameters =
            {
                Db.P(initial.Host), Db.P(Db.Text(initial.Kind)), Db.P((decimal)initial.RequestsPerSecond), Db.P((short)initial.MaxConnections),
            },
        })
        await using (var reader = await insert.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                return Read(reader);
            }
        }

        // Unchanged budget: the row as it is.
        await using var select = new NpgsqlCommand(SelectSql, connection) { Parameters = { Db.P(initial.Host) } };
        await using var existing = await select.ExecuteReaderAsync(cancellationToken);
        await existing.ReadAsync(cancellationToken);
        return Read(existing);
    }

    public async Task<SlotTake?> TryTakeSlotAsync(string host, TimeSpan interval, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var take = new NpgsqlCommand(SlotSql, connection) { Parameters = { Db.P(host), Db.P(interval), Db.P(maxWait) } })
        {
            if (await take.ExecuteScalarAsync(cancellationToken) is DateTime slot)
            {
                return new SlotTake(AsUtc(slot), Taken: true);
            }
        }

        // The take's condition decides; this read only says when to come back.
        await using var free = new NpgsqlCommand(FreeSql, connection) { Parameters = { Db.P(host) } };
        return await free.ExecuteScalarAsync(cancellationToken) is DateTime at ? new SlotTake(AsUtc(at), Taken: false) : null;
    }

    public async Task<CrawlHostState> RecordResponseAsync(string host, HostResponse response, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        CrawlHostState state;
        DateTimeOffset now;
        await using (var select = new NpgsqlCommand(LockSql, connection, transaction) { Parameters = { Db.P(host) } })
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            state = Read(reader);
            now = reader.GetFieldValue<DateTimeOffset>(12);
        }

        var updated = HostBudgetPolicy.Apply(state, response, now);
        await using (var update = new NpgsqlCommand(ApplySql, connection, transaction)
        {
            Parameters =
            {
                Db.P(host), Db.P(updated.NextAllowedAt.ToUniversalTime()), Db.P(Math.Round((decimal)updated.BackoffFactor, 2)),
                Db.P(Db.Utc(updated.PausedUntil)), Db.P(updated.LatencyEwmaMs),
            },
        })
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated with { BackoffFactor = (double)Math.Round((decimal)updated.BackoffFactor, 2) };
    }

    public async Task PauseAsync(string host, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PauseSql, connection) { Parameters = { Db.P(host), Db.P(until.ToUniversalTime()) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordRobotsAsync(string host, RobotsStatus status, DateTimeOffset fetchedAt, int? crawlDelaySeconds, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RobotsSql, connection)
        {
            Parameters = { Db.P(host), Db.P(Db.Text(status)), Db.P(fetchedAt.ToUniversalTime()), Db.P(crawlDelaySeconds) },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static CrawlHostState Read(NpgsqlDataReader reader) => new()
    {
        Host = reader.GetString(0),
        Kind = Db.Enum<HostKind>(reader.GetString(1)),
        RequestsPerSecond = (double)reader.GetDecimal(2),
        MaxConnections = reader.GetInt16(3),
        NextAllowedAt = reader.GetFieldValue<DateTimeOffset>(4),
        RobotsStatus = Db.String(reader, 5) is { } robots ? Db.Enum<RobotsStatus>(robots) : null,
        RobotsFetchedAt = Db.Nullable<DateTimeOffset>(reader, 6),
        CrawlDelaySeconds = Db.Nullable<int>(reader, 7),
        BackoffFactor = (double)reader.GetDecimal(8),
        PausedUntil = Db.Nullable<DateTimeOffset>(reader, 9),
        LatencyEwmaMs = Db.Nullable<int>(reader, 10),
        BlockedAt = Db.Nullable<DateTimeOffset>(reader, 11),
    };
}
