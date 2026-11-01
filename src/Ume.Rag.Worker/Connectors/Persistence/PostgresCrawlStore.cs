using System.Text.Json;
using Npgsql;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Crawl;

namespace Ume.Rag.Worker.Connectors.Persistence;

/// <summary>
/// <see cref="ICrawlFrontierStore"/> and <see cref="ICrawlRunStore"/> over <c>content."CrawlFrontier"</c> (P1: one frontier
/// per source, PK (<c>Classification</c>, <c>SourceId</c>, <c>UrlHash</c>), connectors design §6.0) and
/// <c>content."CrawlRuns"</c>. Admission locks the run's <c>CrawlRuns</c> row, so concurrent fetch jobs of one run count
/// <c>Admitted</c> exactly and never pass <c>MaxPages</c>; the upsert's <c>RunId IS DISTINCT FROM</c> condition makes a
/// repeated admission (a resumed listing, a link on many pages) a no-op.
/// </summary>
public sealed class PostgresCrawlStore(JobDatabase database) : ICrawlFrontierStore, ICrawlRunStore
{
    private const string FrontierColumns = """
        "Classification", "SourceId", "UrlHash", "Url", "Host", "State", "RunId", "Depth", "Leaf", "LastFetchedAt", "LastStatus",
        "RedirectTo", "ETag", "LastModified", "ContentHash", "SitemapLastMod", "RemovalCheck", "FetchUrl"
        """;

    private const string LockRunSql = """SELECT "Admitted", "MaxPages" FROM content."CrawlRuns" WHERE "RunId" = $1 FOR UPDATE""";

    private const string OfRunSql = """
        SELECT "UrlHash" FROM content."CrawlFrontier"
        WHERE "Classification" = $1 AND "SourceId" = $2 AND "RunId" = $3 AND "UrlHash" = ANY($4)
        """;

    // FetchUrl: the latest path form found wins; an admission without one keeps the stored one (#228).
    private const string AdmitSql = $"""
        INSERT INTO content."CrawlFrontier" ("Classification", "SourceId", "UrlHash", "Url", "Host", "State", "RunId", "Depth", "Leaf", "SitemapLastMod", "RemovalCheck",
                                            "FetchUrl")
        SELECT $1, $2, u.h, u.url, u.host, 'pending', $3, u.depth, u.leaf, u.lastmod, u.removal, u.fetch_url
        FROM unnest($4::bytea[], $5::text[], $6::text[], $7::smallint[], $8::boolean[], $9::timestamptz[], $10::boolean[], $11::text[])
             AS u(h, url, host, depth, leaf, lastmod, removal, fetch_url)
        ON CONFLICT ("Classification", "SourceId", "UrlHash") DO UPDATE
           SET "State" = 'pending', "RunId" = EXCLUDED."RunId", "Depth" = EXCLUDED."Depth", "Leaf" = EXCLUDED."Leaf", "RemovalCheck" = EXCLUDED."RemovalCheck",
               "SitemapLastMod" = coalesce(EXCLUDED."SitemapLastMod", content."CrawlFrontier"."SitemapLastMod"),
               "FetchUrl" = coalesce(EXCLUDED."FetchUrl", content."CrawlFrontier"."FetchUrl")
         WHERE content."CrawlFrontier"."RunId" IS DISTINCT FROM EXCLUDED."RunId"
        RETURNING {FrontierColumns}
        """;

    private const string CountSql = """
        UPDATE content."CrawlRuns" SET "Admitted" = "Admitted" + $2, "Seeds" = "Seeds" + $3, "LimitHit" = "LimitHit" OR $4 WHERE "RunId" = $1
        """;

    // An incremental run's unchanged seeds: listed for the run without a fetch (connectors design §5.4).
    private const string MarkListedSql = """
        UPDATE content."CrawlFrontier" f
           SET "RunId" = $3, "RemovalCheck" = false, "SitemapLastMod" = coalesce(u.lastmod, f."SitemapLastMod"), "FetchUrl" = coalesce(u.fetch_url, f."FetchUrl")
        FROM unnest($4::bytea[], $5::timestamptz[], $6::text[]) AS u(h, lastmod, fetch_url)
        WHERE f."Classification" = $1 AND f."SourceId" = $2 AND f."UrlHash" = u.h AND f."State" = 'idle'
          AND f."RunId" IS DISTINCT FROM $3 AND f."LastFetchedAt" IS NOT NULL
          AND (u.lastmod IS NULL OR u.lastmod <= f."LastFetchedAt")
        RETURNING f."UrlHash"
        """;

    private const string LeftSitemapSql = $"""
        SELECT {FrontierColumns} FROM content."CrawlFrontier"
        WHERE "Classification" = $1 AND "SourceId" = $2 AND "State" = 'idle' AND "RunId" IS DISTINCT FROM $3 AND "SitemapLastMod" IS NOT NULL
        ORDER BY "Url"
        """;

    private const string ClearSitemapSql = """
        UPDATE content."CrawlFrontier" SET "SitemapLastMod" = NULL
        WHERE "Classification" = $1 AND "SourceId" = $2 AND "UrlHash" = ANY($3)
        """;

    private const string GetSql = $"""
        SELECT {FrontierColumns} FROM content."CrawlFrontier" WHERE "Classification" = $1 AND "SourceId" = $2 AND "UrlHash" = $3
        """;

    private const string LoadSql = """
        SELECT "Host", count(*) FILTER (WHERE "State" = 'pending'), count(*) FILTER (WHERE "State" = 'released')
        FROM content."CrawlFrontier"
        WHERE "Classification" = $1 AND "SourceId" = $2 AND "RunId" = $3 AND "State" <> 'idle'
        GROUP BY "Host"
        """;

    // Removal checks first (as at their former depth -1), then lowest depth (BFS); SKIP LOCKED keeps two top-ups of one host from releasing a row twice.
    private const string ReleaseSql = $"""
        WITH next AS (
            SELECT "UrlHash" AS h FROM content."CrawlFrontier"
            WHERE "Classification" = $1 AND "SourceId" = $2 AND "RunId" = $3 AND "Host" = $4 AND "State" = 'pending'
            ORDER BY CASE WHEN "RemovalCheck" THEN -1 ELSE "Depth" END, "Url"
            LIMIT $5
            FOR UPDATE SKIP LOCKED
        )
        UPDATE content."CrawlFrontier" f SET "State" = 'released'
        FROM next WHERE f."Classification" = $1 AND f."SourceId" = $2 AND f."UrlHash" = next.h
        RETURNING {FrontierColumns}
        """;

    private const string IdleSql = """
        UPDATE content."CrawlFrontier" SET "State" = 'idle' WHERE "Classification" = $1 AND "SourceId" = $2 AND "UrlHash" = $3
        """;

    private const string CompleteSql = """
        UPDATE content."CrawlFrontier"
           SET "State" = 'idle', "LastFetchedAt" = $4, "LastStatus" = $5, "ETag" = $6, "LastModified" = $7, "ContentHash" = $8, "RedirectTo" = $9
         WHERE "Classification" = $1 AND "SourceId" = $2 AND "UrlHash" = $3
        """;

    private const string TargetSql = """
        INSERT INTO content."CrawlFrontier" ("Classification", "SourceId", "UrlHash", "Url", "Host", "State", "RunId", "Depth", "Leaf",
                                            "LastFetchedAt", "LastStatus", "ETag", "LastModified", "ContentHash", "RedirectTo", "RemovalCheck")
        VALUES ($1, $2, $3, $4, $5, 'idle', $6, $7, $8, $9, $10, $11, $12, $13, NULL, $14)
        ON CONFLICT ("Classification", "SourceId", "UrlHash") DO UPDATE
           SET "State" = 'idle', "RunId" = EXCLUDED."RunId", "Depth" = EXCLUDED."Depth", "Leaf" = EXCLUDED."Leaf", "RemovalCheck" = EXCLUDED."RemovalCheck",
               "LastFetchedAt" = EXCLUDED."LastFetchedAt", "LastStatus" = EXCLUDED."LastStatus", "ETag" = EXCLUDED."ETag",
               "LastModified" = EXCLUDED."LastModified", "ContentHash" = EXCLUDED."ContentHash", "RedirectTo" = NULL
        """;

    private const string StartSql = """
        INSERT INTO content."CrawlRuns" ("RunId", "Classification", "SourceId", "Admitted", "MaxPages", "MaxDepth", "LimitHit", "Seeds", "SeedsFailed", "Complete")
        VALUES ($1, $2, $3, 0, $4, $5, false, 0, 0, false)
        ON CONFLICT ("RunId") DO NOTHING
        """;

    private const string RunSql = """
        SELECT "RunId", "Classification", "SourceId", "Admitted", "MaxPages", "MaxDepth", "LimitHit", "Seeds", "SeedsFailed",
               "PausedHosts"::text, "TrapStats"::text, "Complete", "Incremental"
        FROM content."CrawlRuns" WHERE "RunId" = $1
        """;

    private const string SeedFailedSql = """UPDATE content."CrawlRuns" SET "SeedsFailed" = "SeedsFailed" + 1 WHERE "RunId" = $1""";

    // PausedHosts: { host: { count, reason, until } } (HostPause in camelCase, as JsonSerializerOptions.Web reads it).
    private const string PauseSql = """
        UPDATE content."CrawlRuns"
           SET "PausedHosts" = coalesce("PausedHosts", '{}'::jsonb) || jsonb_build_object($2::text, jsonb_build_object(
                   'count', coalesce(("PausedHosts" -> $2::text ->> 'count')::int, 0) + 1, 'reason', $3::text, 'until', $4::timestamptz))
         WHERE "RunId" = $1
        RETURNING ("PausedHosts" -> $2::text ->> 'count')::int
        """;

    private const string LockTrapsSql = """SELECT "TrapStats"::text FROM content."CrawlRuns" WHERE "RunId" = $1 FOR UPDATE""";

    private const string TrapsSql = """UPDATE content."CrawlRuns" SET "TrapStats" = $2::jsonb WHERE "RunId" = $1""";

    private const string CompleteRunSql = """UPDATE content."CrawlRuns" SET "Complete" = $2 WHERE "RunId" = $1""";

    private const string IncrementalSql = """UPDATE content."CrawlRuns" SET "Incremental" = $2 WHERE "RunId" = $1""";

    public async Task<AdmitResult> AdmitAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(urls);
        var distinct = FrontierAdmission.OnePerIdentity(urls);
        if (distinct.Count == 0)
        {
            return new AdmitResult([], false);
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int admitted, maxPages;
        await using (var lockRun = new NpgsqlCommand(LockRunSql, connection, transaction) { Parameters = { Db.P(run.RunId) } })
        await using (var reader = await lockRun.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The crawl run has no CrawlRuns row.");
            }

            (admitted, maxPages) = (reader.GetInt32(0), reader.GetInt32(1));
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        await using (var ofRun = new NpgsqlCommand(OfRunSql, connection, transaction)
        {
            Parameters = { Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.RunId), Db.P(distinct.Select(u => Db.Bytes(u.UrlHash)).ToArray()) },
        })
        await using (var reader = await ofRun.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                known.Add(Db.Hex(reader.GetFieldValue<byte[]>(0)));
            }
        }

        var fresh = distinct.Where(u => !known.Contains(u.UrlHash)).ToList();
        var room = Math.Max(0, maxPages - admitted);
        var take = fresh.Take(room).ToList();
        var limitHit = fresh.Count > take.Count;
        var rows = new List<FrontierRow>();
        if (take.Count > 0)
        {
            await using var admit = new NpgsqlCommand(AdmitSql, connection, transaction)
            {
                Parameters =
                {
                    Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.RunId),
                    Db.P(take.Select(u => Db.Bytes(u.UrlHash)).ToArray()), Db.P(take.Select(u => u.Url).ToArray()),
                    Db.P(take.Select(u => u.Host).ToArray()), Db.P(take.Select(u => u.Depth).ToArray()), Db.P(take.Select(u => u.Leaf).ToArray()),
                    Db.P(take.Select(u => Db.Utc(u.SitemapLastMod)).ToArray()), Db.P(take.Select(u => u.RemovalCheck).ToArray()),
                    Db.P(take.Select(u => u.StoredFetchUrl).ToArray()),
                },
            };
            await using var reader = await admit.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadRow(reader));
            }
        }

        await using (var count = new NpgsqlCommand(CountSql, connection, transaction)
        {
            Parameters = { Db.P(run.RunId), Db.P(rows.Count), Db.P(rows.Count(r => r.Depth == 0 && !r.IsRemovalCheck)), Db.P(limitHit) },
        })
        {
            await count.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AdmitResult(rows, limitHit);
    }

    public async Task<IReadOnlySet<string>> MarkListedAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> seeds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(seeds);
        var distinct = FrontierAdmission.OnePerIdentity(seeds);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        if (distinct.Count == 0)
        {
            return listed;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(MarkListedSql, connection)
        {
            Parameters =
            {
                Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.RunId),
                Db.P(distinct.Select(u => Db.Bytes(u.UrlHash)).ToArray()), Db.P(distinct.Select(u => Db.Utc(u.SitemapLastMod)).ToArray()),
                Db.P(distinct.Select(u => u.StoredFetchUrl).ToArray()),
            },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            listed.Add(Db.Hex(reader.GetFieldValue<byte[]>(0)));
        }

        return listed;
    }

    public async Task<IReadOnlyList<FrontierRow>> LeftSitemapAsync(CrawlRunKey run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LeftSitemapSql, connection) { Parameters = { Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.RunId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<FrontierRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    public async Task ClearSitemapLastModAsync(CrawlRunKey run, IReadOnlyList<string> urlHashes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(urlHashes);
        if (urlHashes.Count == 0)
        {
            return;
        }

        await ExecuteAsync(ClearSitemapSql, cancellationToken, Db.P(run.Classification), Db.P(run.SourceId), Db.P(urlHashes.Select(Db.Bytes).ToArray()));
    }

    public async Task<FrontierRow?> GetAsync(Guid sourceId, string classification, string urlHash, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(GetSql, connection) { Parameters = { Db.P(classification), Db.P(sourceId), Db.P(Db.Bytes(urlHash)) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRow(reader) : null;
    }

    public async Task<IReadOnlyList<HostLoad>> HostLoadAsync(CrawlRunKey run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LoadSql, connection) { Parameters = { Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.RunId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var loads = new List<HostLoad>();
        while (await reader.ReadAsync(cancellationToken))
        {
            loads.Add(new HostLoad(reader.GetString(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2)));
        }

        return loads;
    }

    public async Task<IReadOnlyList<FrontierRow>> ReleaseAsync(CrawlRunKey run, string host, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ReleaseSql, connection)
        {
            Parameters = { Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.RunId), Db.P(host), Db.P((long)max) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<FrontierRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRow(reader));
        }

        return [.. rows.OrderBy(r => r.IsRemovalCheck ? FrontierRow.LegacyRemovalCheckDepth : r.Depth).ThenBy(r => r.Url, StringComparer.Ordinal)];
    }

    public async Task CompleteAsync(CrawlRunKey run, string urlHash, FrontierFacts? facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = facts is null
            ? new NpgsqlCommand(IdleSql, connection) { Parameters = { Db.P(run.Classification), Db.P(run.SourceId), Db.P(Db.Bytes(urlHash)) } }
            : new NpgsqlCommand(CompleteSql, connection)
            {
                Parameters =
                {
                    Db.P(run.Classification), Db.P(run.SourceId), Db.P(Db.Bytes(urlHash)), Db.P(facts.FetchedAt.ToUniversalTime()), Db.P(facts.LastStatus),
                    Db.P(facts.ETag), Db.P(facts.LastModified), Db.P(facts.ContentHash is { } hash ? Db.Bytes(hash) : null), Db.P(facts.RedirectTo),
                },
            };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordTargetAsync(CrawlRunKey run, FrontierAdmission target, FrontierFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(facts);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TargetSql, connection)
        {
            Parameters =
            {
                Db.P(run.Classification), Db.P(run.SourceId), Db.P(Db.Bytes(target.UrlHash)), Db.P(target.Url), Db.P(target.Host), Db.P(run.RunId),
                Db.P(target.Depth), Db.P(target.Leaf), Db.P(facts.FetchedAt.ToUniversalTime()), Db.P(facts.LastStatus), Db.P(facts.ETag),
                Db.P(facts.LastModified), Db.P(facts.ContentHash is { } hash ? Db.Bytes(hash) : null), Db.P(target.RemovalCheck),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StartAsync(CrawlRunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await ExecuteAsync(StartSql, cancellationToken, Db.P(run.RunId), Db.P(run.Classification), Db.P(run.SourceId), Db.P(run.MaxPages), Db.P(run.MaxDepth));
    }

    public async Task<CrawlRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RunSql, connection) { Parameters = { Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrawlRunRecord
        {
            RunId = reader.GetGuid(0),
            Classification = reader.GetString(1),
            SourceId = reader.GetGuid(2),
            Admitted = reader.GetInt32(3),
            MaxPages = reader.GetInt32(4),
            MaxDepth = reader.GetInt32(5),
            LimitHit = reader.GetBoolean(6),
            Seeds = reader.GetInt32(7),
            SeedsFailed = reader.GetInt32(8),
            PausedHosts = Json<HostPause>(Db.String(reader, 9)),
            TrapStats = Json<TrapStat>(Db.String(reader, 10)),
            Complete = reader.GetBoolean(11),
            Incremental = reader.GetBoolean(12),
        };
    }

    public Task SeedFailedAsync(Guid runId, CancellationToken cancellationToken) => ExecuteAsync(SeedFailedSql, cancellationToken, Db.P(runId));

    public async Task<int> PauseHostAsync(Guid runId, string host, string reason, DateTimeOffset? until, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PauseSql, connection) { Parameters = { Db.P(runId), Db.P(host), Db.P(reason), Db.P(Db.Utc(until)) } };
        return await command.ExecuteScalarAsync(cancellationToken) is int count ? count : 0;
    }

    public async Task RecordTrapsAsync(Guid runId, IReadOnlyDictionary<string, TrapStat> traps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(traps);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? current;
        await using (var select = new NpgsqlCommand(LockTrapsSql, connection, transaction) { Parameters = { Db.P(runId) } })
        {
            current = await select.ExecuteScalarAsync(cancellationToken) as string;
        }

        var stats = new Dictionary<string, TrapStat>(Json<TrapStat>(current), StringComparer.Ordinal);
        foreach (var (kind, stat) in traps)
        {
            stats[kind] = stats.TryGetValue(kind, out var existing) ? existing with { Count = existing.Count + stat.Count } : stat;
        }

        await using (var update = new NpgsqlCommand(TrapsSql, connection, transaction)
        {
            Parameters = { Db.P(runId), Db.P(JsonSerializer.Serialize(stats, JsonSerializerOptions.Web)) },
        })
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task SetCompleteAsync(Guid runId, bool complete, CancellationToken cancellationToken) =>
        ExecuteAsync(CompleteRunSql, cancellationToken, Db.P(runId), Db.P(complete));

    public Task SetIncrementalAsync(Guid runId, bool incremental, CancellationToken cancellationToken) =>
        ExecuteAsync(IncrementalSql, cancellationToken, Db.P(runId), Db.P(incremental));

    private static Dictionary<string, T> Json<T>(string? json) =>
        json is null ? new(StringComparer.Ordinal) : new(JsonSerializer.Deserialize<Dictionary<string, T>>(json, JsonSerializerOptions.Web) ?? [], StringComparer.Ordinal);

    private static FrontierRow ReadRow(NpgsqlDataReader reader) => new()
    {
        Classification = reader.GetString(0),
        SourceId = reader.GetGuid(1),
        UrlHash = Db.Hex(reader.GetFieldValue<byte[]>(2)),
        Url = reader.GetString(3),
        Host = reader.GetString(4),
        State = Db.Enum<FrontierState>(reader.GetString(5)),
        RunId = Db.Nullable<Guid>(reader, 6),
        Depth = reader.GetInt16(7),
        Leaf = reader.GetBoolean(8),
        LastFetchedAt = Db.Nullable<DateTimeOffset>(reader, 9),
        LastStatus = Db.Nullable<short>(reader, 10),
        RedirectTo = Db.String(reader, 11),
        ETag = Db.String(reader, 12),
        LastModified = Db.String(reader, 13),
        ContentHash = reader.IsDBNull(14) ? null : Db.Hex(reader.GetFieldValue<byte[]>(14)),
        SitemapLastMod = Db.Nullable<DateTimeOffset>(reader, 15),
        RemovalCheck = reader.GetBoolean(16),
        FetchUrl = Db.String(reader, 17),
    };

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
