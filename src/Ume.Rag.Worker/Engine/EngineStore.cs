using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Persistence;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>The <c>content."Documents"</c> columns the engine reads (data-model §2.2).</summary>
public sealed record DocumentRow(
    Guid Id,
    Guid SourceId,
    string Classification,
    string IdentityKind,
    string CanonicalIdentity,
    byte[]? ContentHash,
    string? Url,
    string? MediaType,
    string? DisplayName,
    Guid? SiteProfileVersionId,
    string? ParseStrategy,
    Guid? LiveChunkSetId,
    Guid? CurrentExecutionId,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? HttpLastModified,
    DateTimeOffset? SitemapLastMod);

/// <summary>A fetched document as the fetch stage reports it (connectors design §3, §12).</summary>
public sealed record FetchedDocument(Guid SourceId, string Classification, string IdentityKind, string CanonicalIdentity, byte[] ContentHash,
    short ContentHashVersion, string? Url, string? DisplayName, string? CanonicalHint)
{
    /// <summary>The response's <c>Last-Modified</c> as a date (<see cref="DocumentRow.HttpLastModified"/>).</summary>
    public DateTimeOffset? HttpLastModified { get; init; }

    /// <summary>The frontier row's sitemap <c>lastmod</c> (<see cref="DocumentRow.SitemapLastMod"/>).</summary>
    public DateTimeOffset? SitemapLastMod { get; init; }

    /// <summary><c>Documents.FetchMode</c>: how this content was fetched (<c>http</c>, or <c>browser</c> for a rendered DOM).</summary>
    public string FetchMode { get; init; } = Domain.Entities.FetchModes.Http;
}

/// <param name="Document">The row after the upsert.</param>
/// <param name="ContentChanged">New or changed content: a new execution id was set (engine design §7.5).</param>
public sealed record DocumentUpsert(DocumentRow Document, bool ContentChanged);

/// <summary>The document facts a segment commit writes (engine design §7.6: last fenced writer wins).</summary>
public sealed record DocumentFacts(string? MediaType, string? Language, string? Title, string? Description, IReadOnlyList<string> Keywords,
    string? ParseStrategy, byte[]? ParseCacheKey, int? PageCount, Guid? SiteProfileVersionId, string ParseOutcome);

/// <summary>A <c>content."RunItems"</c> row, written only at a terminal outcome (engine design §10).</summary>
public sealed record RunItemRow(Guid RunId, string Classification, Guid? DocumentId, string Identity, Outcome Outcome)
{
    public string? Reason { get; init; }

    public string? Strategy { get; init; }

    public Guid? PipelineVersionId { get; init; }

    public Guid? ExecutionId { get; init; }

    public int? DurationMs { get; init; }

    public decimal? CostUsd { get; init; }

    /// <summary>
    /// The content the outcome is about. An <c>upload:&lt;slot&gt;</c> item with it sets its slot's state while the slot still
    /// holds these bytes (<see cref="EngineCommit.AddRunItemAsync"/>); without it the slot is left alone.
    /// </summary>
    public byte[]? ContentHash { get; init; }
}

/// <summary>The response facts the fetch writes to the source's frontier row, with <c>State = idle</c>.</summary>
public sealed record FrontierCompletion(Guid SourceId, string Classification, byte[] UrlHash, Guid RunId, DateTimeOffset FetchedAt, short? Status,
    string? ETag, string? LastModified, byte[]? ContentHash)
{
    /// <summary>The crawl's redirect target (<c>CrawlFrontier.RedirectTo</c>).</summary>
    public string? RedirectTo { get; init; }

    /// <summary>No request was made (a crawl's <c>lastmod</c> skip): only <c>State = idle</c>, the last fetch's facts stay.</summary>
    public bool KeepFacts { get; init; }

    /// <summary>A crawl seed that failed: <c>CrawlRuns.SeedsFailed</c> counts it in the same commit.</summary>
    public bool SeedFailed { get; init; }
}

/// <summary>Run item outcomes and identity hashes as the tables hold them.</summary>
public static class EngineText
{
    /// <summary><c>RunItems.Outcome</c>: the kebab-case outcome name (<c>too-large</c>).</summary>
    public static string Outcome(Outcome outcome) => outcome switch
    {
        Pipelines.Jobs.RunItemOutcome.TooLarge => "too-large",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    /// <summary><c>Documents.IdentityHash</c>: SHA-256 of the canonical identity.</summary>
    public static byte[] IdentityHash(string canonicalIdentity) => SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));

    /// <summary>A text cut to a column's length.</summary>
    public static string? Cut(string? text, int length) => text is null || text.Length <= length ? text : text[..length];
}

/// <summary>
/// The document side of the engine (data-model §2.2; connectors design §13.3–§13.4): the fetch upsert that sets a new
/// execution id on new content, the seen and failure facts, and the rows the reconcile phase decides on.
/// </summary>
public sealed class DocumentStore(JobDatabase database)
{
    internal const string Columns = """
        "Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "ContentHash", "Url", "MediaType", "DisplayName",
        "SiteProfileVersionId", "ParseStrategy", "LiveChunkSetId", "CurrentExecutionId", "DeletedAt",
        "HttpLastModified", "SitemapLastMod"
        """;

    private const string FindSql = $"""
        SELECT {Columns} FROM content."Documents" WHERE "SourceId" = $1 AND "IdentityKind" = $2 AND "IdentityHash" = $3
        """;

    private const string GetSql = $"""SELECT {Columns} FROM content."Documents" WHERE "Id" = $1""";

    private const string FetchModeSql = """SELECT "FetchMode" FROM content."Documents" WHERE "Id" = $1""";

    private const string InsertSql = $"""
        INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash", "ContentHash",
                                        "ContentHashVersion", "Url", "DisplayName", "CanonicalHint", "FetchedAt", "CurrentExecutionId", "LastRunId",
                                        "FirstSeenAt", "LastSeenAt", "HttpLastModified", "SitemapLastMod", "FetchMode")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $12, $12, $15, $16, $17)
        ON CONFLICT ("SourceId", "IdentityKind", "IdentityHash") DO NOTHING
        RETURNING {Columns}
        """;

    // Seen in the run: a hold cleared, the failure count reset, a soft-deleted document revived (its live set's chunks with
    // it; the unique identity index allows no second document, so a revival past the grace re-processes as new content).
    // The dates are the response's: a later re-processing without a fetch reads the last 200's (processing §7).
    private const string UpdateSql = $"""
        UPDATE content."Documents"
           SET "LastSeenAt" = $2, "LastRunId" = $3, "FetchedAt" = $2, "Url" = coalesce($4, "Url"), "DisplayName" = coalesce($5, "DisplayName"),
               "CanonicalHint" = $6, "HeldAt" = NULL, "HeldBy" = NULL, "ConsecutiveFailures" = 0,
               "CurrentExecutionId" = CASE WHEN $9 THEN $7 ELSE "CurrentExecutionId" END,
               "ContentHash" = CASE WHEN $9 THEN $8 ELSE "ContentHash" END,
               "ContentHashVersion" = $10, "HttpLastModified" = $11, "SitemapLastMod" = $12, "FetchMode" = $13,
               "DeletedAt" = NULL, "PurgeAfter" = NULL
         WHERE "Id" = $1
        RETURNING {Columns}
        """;

    private const string LockSql = """
        SELECT "Id", "ContentHash", "DeletedAt" IS NOT NULL AND ("PurgeAfter" IS NULL OR "PurgeAfter" <= $4), "LiveChunkSetId",
               "DeletedAt" IS NOT NULL
        FROM content."Documents" WHERE "SourceId" = $1 AND "IdentityKind" = $2 AND "IdentityHash" = $3 FOR UPDATE
        """;

    private const string ReviveChunksSql = """
        UPDATE content."Chunks" c SET "DeletedAt" = NULL
          FROM content."ChunkSets" s
         WHERE s."Id" = $1 AND s."State" = 'live' AND c."ChunkSetId" = s."Id" AND c."DeletedAt" IS NOT NULL
        """;

    // A URL given (a 304's final URL, a crawl row's kept path form; #228) replaces Documents.Url.
    private const string SeenSql = """
        UPDATE content."Documents"
           SET "LastSeenAt" = $2, "LastRunId" = $3, "HeldAt" = NULL, "HeldBy" = NULL, "ConsecutiveFailures" = 0, "Url" = coalesce($4, "Url")
         WHERE "Id" = $1 AND "DeletedAt" IS NULL
        """;

    private const string FailedSql = """
        UPDATE content."Documents" SET "ConsecutiveFailures" = least("ConsecutiveFailures" + 1, 32000) WHERE "Id" = $1
        """;

    // With the FetchUrl of the identity's frontier row (crawls and URL lists; #228).
    private const string SourceDocumentsSql = """
        SELECT d."Id", d."CanonicalIdentity", d."LastRunId", d."LastSeenAt", d."ConsecutiveFailures", d."HeldAt", d."HeldBy", d."DeletedAt", d."PurgeAfter",
               d."Url", f."FetchUrl"
        FROM content."Documents" d
        LEFT JOIN content."CrawlFrontier" f ON f."Classification" = d."Classification" AND f."SourceId" = d."SourceId" AND f."UrlHash" = d."IdentityHash"
        WHERE d."SourceId" = $1
        """;

    private const string LiveDocumentsSql = $"""
        SELECT {Columns} FROM content."Documents"
        WHERE "SourceId" = $1 AND "DeletedAt" IS NULL AND ($2::uuid IS NULL OR "Id" > $2)
        ORDER BY "Id"
        LIMIT $3
        """;

    public async Task<DocumentRow?> GetAsync(Guid documentId, CancellationToken cancellationToken) =>
        await ReadOneAsync(GetSql, cancellationToken, Db.P(documentId));

    /// <summary><c>Documents.FetchMode</c> of a document: how its current content was fetched; null when there is no such document.</summary>
    public async Task<string?> FetchModeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FetchModeSql, connection) { Parameters = { Db.P(documentId) } };
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<DocumentRow?> FindAsync(Guid sourceId, string identityKind, string canonicalIdentity, CancellationToken cancellationToken) =>
        await ReadOneAsync(FindSql, cancellationToken, Db.P(sourceId), Db.P(identityKind), Db.P(EngineText.IdentityHash(canonicalIdentity)));

    /// <summary>
    /// The fetch stage's commit of a document (connectors design §2.2): created with a new execution id, or updated as seen;
    /// a changed content hash (the hash alone; the noise rules version is a label), or a revival past the grace, sets a new execution id (engine design §7.5).
    /// </summary>
    public async Task<DocumentUpsert> UpsertFetchedAsync(FetchedDocument fetched, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        var at = now.ToUniversalTime();
        var identityHash = EngineText.IdentityHash(fetched.CanonicalIdentity);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? id = null;
        var changed = true;
        Guid? liveSet = null;
        var revive = false;
        await using (var lockRow = new NpgsqlCommand(LockSql, connection, transaction)
        {
            Parameters = { Db.P(fetched.SourceId), Db.P(fetched.IdentityKind), Db.P(identityHash), Db.P(at) },
        })
        await using (var reader = await lockRow.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetGuid(0);
                var pastGrace = reader.GetBoolean(2);
                // The hash alone decides (noise rules v2 tag the HTML hash with their version; ContentHashVersion is a label).
                changed = pastGrace || reader.IsDBNull(1) || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(fetched.ContentHash);
                liveSet = Db.Nullable<Guid>(reader, 3);
                revive = reader.GetBoolean(4) && !changed;
            }
        }

        DocumentRow row;
        if (id is null)
        {
            await using var insert = new NpgsqlCommand(InsertSql, connection, transaction)
            {
                Parameters =
                {
                    Db.P(Guid.CreateVersion7()), Db.P(fetched.SourceId), Db.P(fetched.Classification), Db.P(fetched.IdentityKind),
                    Db.P(fetched.CanonicalIdentity), Db.P(identityHash), Db.P(fetched.ContentHash), Db.P(fetched.ContentHashVersion), Db.P(fetched.Url),
                    Db.P(fetched.DisplayName), Db.P(fetched.CanonicalHint), Db.P(at), Db.P(Guid.CreateVersion7()), Db.P(runId),
                    Db.P(Db.Utc(fetched.HttpLastModified)), Db.P(Db.Utc(fetched.SitemapLastMod)), Db.P(fetched.FetchMode),
                },
            };
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                // A concurrent fetch of the same identity inserted it first: the caller retries and updates.
                throw new StageTransientException("document.concurrent_insert", TimeSpan.FromSeconds(1), countsAttempt: false);
            }

            row = Read(reader);
        }
        else
        {
            await using var update = new NpgsqlCommand(UpdateSql, connection, transaction)
            {
                Parameters =
                {
                    Db.P(id.Value), Db.P(at), Db.P(runId), Db.P(fetched.Url), Db.P(fetched.DisplayName), Db.P(fetched.CanonicalHint),
                    Db.P(Guid.CreateVersion7()), Db.P(fetched.ContentHash), Db.P(changed), Db.P(fetched.ContentHashVersion),
                    Db.P(Db.Utc(fetched.HttpLastModified)), Db.P(Db.Utc(fetched.SitemapLastMod)), Db.P(fetched.FetchMode),
                },
            };
            await using (var reader = await update.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                row = Read(reader);
            }

            if (revive && liveSet is { } set)
            {
                await using var chunks = new NpgsqlCommand(ReviveChunksSql, connection, transaction) { Parameters = { Db.P(set) } };
                await chunks.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new DocumentUpsert(row, changed);
    }

    /// <summary>
    /// A 304 or an unchanged listing: the document was seen in the run (connectors design §13.3); <paramref name="url"/>, when
    /// given, becomes <c>Documents.Url</c> (identity, content and chunks unchanged).
    /// </summary>
    public async Task SeenAsync(Guid documentId, Guid runId, DateTimeOffset now, string? url, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SeenSql, connection) { Parameters = { Db.P(documentId), Db.P(now.ToUniversalTime()), Db.P(runId), Db.P(url) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A failed fetch of a known document (401/403/5xx/TLS): one more consecutive failure (§13.1 <c>persistent-failure</c>).</summary>
    public async Task FailedAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FailedSql, connection) { Parameters = { Db.P(documentId) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The source's documents as the reconcile phase reads them.</summary>
    public async Task<IReadOnlyList<SourceDocument>> SourceDocumentsAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SourceDocumentsSql, connection) { Parameters = { Db.P(sourceId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<SourceDocument>();
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new SourceDocument
            {
                Id = reader.GetGuid(0),
                CanonicalIdentity = reader.GetString(1),
                LastRunId = Db.Nullable<Guid>(reader, 2),
                LastSeenAt = reader.GetFieldValue<DateTimeOffset>(3),
                ConsecutiveFailures = reader.GetInt16(4),
                HeldAt = Db.Nullable<DateTimeOffset>(reader, 5),
                HeldBy = Db.String(reader, 6),
                DeletedAt = Db.Nullable<DateTimeOffset>(reader, 7),
                PurgeAfter = Db.Nullable<DateTimeOffset>(reader, 8),
                Url = Db.String(reader, 9),
                FetchUrl = Db.String(reader, 10),
            });
        }

        return documents;
    }

    /// <summary>The source's live (not soft-deleted) documents after <paramref name="after"/> in id order, for a <c>reprocess</c> run.</summary>
    public async Task<IReadOnlyList<DocumentRow>> LiveDocumentsAsync(Guid sourceId, Guid? after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LiveDocumentsSql, connection) { Parameters = { Db.P(sourceId), Db.P(after), Db.P((long)limit) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DocumentRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    internal static DocumentRow Read(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5), Db.String(reader, 6), Db.String(reader, 7), Db.String(reader, 8),
            Db.Nullable<Guid>(reader, 9), Db.String(reader, 10), Db.Nullable<Guid>(reader, 11), Db.Nullable<Guid>(reader, 12),
            Db.Nullable<DateTimeOffset>(reader, 13), Db.Nullable<DateTimeOffset>(reader, 14),
            Db.Nullable<DateTimeOffset>(reader, 15));

    private async Task<DocumentRow?> ReadOneAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }
}

/// <summary>
/// One short engine transaction (engine design §7.4–§7.6, §14): the fence on the document's execution, the stage output
/// rows, the document facts, the frontier row, the continuation jobs, the run item and the job's own completion, committed
/// together or not at all. A failed fence or a lost lease rolls everything back.
/// </summary>
public sealed class EngineCommit : IAsyncDisposable
{
    private const string FenceSql = """
        SELECT 1 FROM content."Documents"
         WHERE "Id" = $1 AND "Classification" = $2 AND "CurrentExecutionId" = $3 AND "ContentHash" = $4
           FOR SHARE
        """;

    private const string FactsSql = """
        UPDATE content."Documents"
           SET "MediaType" = $2, "Language" = $3, "Title" = $4, "Description" = $5, "Keywords" = $6, "ParseStrategy" = $7, "ParseCacheKey" = $8,
               "PageCount" = $9, "SiteProfileVersionId" = $10, "ParseOutcome" = $11
         WHERE "Id" = $1
        """;

    private const string OutcomeSql = """UPDATE content."Documents" SET "ParseOutcome" = $2 WHERE "Id" = $1""";

    private const string RunItemSql = """
        INSERT INTO content."RunItems" ("RunId", "Classification", "DocumentId", "Identity", "Outcome", "Reason", "Strategy", "PipelineVersionId",
                                       "ExecutionId", "DurationMs", "CostUsd")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
        """;

    // The slot of an upload item (connectors design §9.1), while it holds the item's bytes and is not deleted: indexed when the
    // document's live pointer names a set (live or held) built from those bytes, so a replace in between is never marked by the
    // old bytes' set and a failed reprocess of indexed bytes leaves them indexed; else the failure's state ($4) and reason.
    private const string UploadSlotSql = """
        WITH live AS (
            SELECT 1 FROM content."Documents" d JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId" WHERE d."Id" = $3 AND s."ContentHash" = $2)
        UPDATE content."Uploads"
           SET "State" = CASE WHEN EXISTS (SELECT 1 FROM live) THEN 'indexed' ELSE $4 END,
               "Reason" = CASE WHEN EXISTS (SELECT 1 FROM live) THEN NULL ELSE $5 END,
               "DocumentId" = coalesce($3, "DocumentId")
         WHERE "Id" = $1 AND "Sha256" = $2 AND "DeletedAt" IS NULL AND (EXISTS (SELECT 1 FROM live) OR $4 IS NOT NULL)
        """;

    private const string FrontierSql = """
        UPDATE content."CrawlFrontier"
           SET "State" = 'idle', "LastFetchedAt" = $5, "LastStatus" = $6, "ETag" = $7, "LastModified" = $8, "ContentHash" = coalesce($9, "ContentHash"),
               "RedirectTo" = $10
         WHERE "Classification" = $1 AND "SourceId" = $2 AND "UrlHash" = $3 AND "RunId" = $4
        """;

    private const string FrontierIdleSql = """
        UPDATE content."CrawlFrontier" SET "State" = 'idle'
         WHERE "Classification" = $1 AND "SourceId" = $2 AND "UrlHash" = $3 AND "RunId" = $4
        """;

    private const string SeedFailedSql = """UPDATE content."CrawlRuns" SET "SeedsFailed" = "SeedsFailed" + 1 WHERE "RunId" = $1""";

    private (JobUsage Usage, int Written)? _usage;

    private EngineCommit(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public static async Task<EngineCommit> BeginAsync(JobDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var connection = await database.OpenAsync(cancellationToken);
        try
        {
            return new EngineCommit(connection, await connection.BeginTransactionAsync(cancellationToken));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The fence of engine design §7.5: false when the execution was superseded (the caller rolls back).</summary>
    public async Task<bool> FenceAsync(Guid documentId, string classification, Guid executionId, byte[] contentHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FenceSql, Connection, Transaction)
        {
            Parameters = { Db.P(documentId), Db.P(classification), Db.P(executionId), Db.P(contentHash) },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task AddOutputsAsync(IEnumerable<NodeOutputRecord> outputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        foreach (var output in outputs)
        {
            await using var command = PostgresNodeOutputStore.Add(output, Connection, Transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SetFactsAsync(Guid documentId, DocumentFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        await using var command = new NpgsqlCommand(FactsSql, Connection, Transaction)
        {
            Parameters =
            {
                Db.P(documentId), Db.P(EngineText.Cut(facts.MediaType, 200)), Db.P(EngineText.Cut(facts.Language, 20)), Db.P(facts.Title),
                Db.P(facts.Description), Db.P(facts.Keywords.ToArray()), Db.P(EngineText.Cut(facts.ParseStrategy, 30)), Db.P(facts.ParseCacheKey),
                Db.P(facts.PageCount), Db.P(facts.SiteProfileVersionId), Db.P(EngineText.Cut(facts.ParseOutcome, 30)),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A document that ended at a stage (<c>skipped</c>, <c>unsupported</c>, …) keeps that as its parse outcome.</summary>
    public async Task SetParseOutcomeAsync(Guid documentId, string outcome, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(OutcomeSql, Connection, Transaction) { Parameters = { Db.P(documentId), Db.P(EngineText.Cut(outcome, 30)) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddRunItemAsync(RunItemRow item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var command = new NpgsqlCommand(RunItemSql, Connection, Transaction)
        {
            Parameters =
            {
                Db.P(item.RunId), Db.P(item.Classification), Db.P(item.DocumentId), Db.P(item.Identity), Db.P(EngineText.Outcome(item.Outcome)),
                Db.P(EngineText.Cut(item.Reason, 500)), Db.P(EngineText.Cut(item.Strategy, 30)), Db.P(item.PipelineVersionId), Db.P(item.ExecutionId),
                Db.P(item.DurationMs), Db.P(item.CostUsd),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        // A superseded item leaves the slot to the newer execution; a deleted one's slot is closed by its batch.
        if (item.ContentHash is { } contentHash && item.Outcome is not (Outcome.Superseded or Outcome.Deleted) && UploadConnector.SlotOf(item.Identity) is { } slotId)
        {
            await using var slot = new NpgsqlCommand(UploadSlotSql, Connection, Transaction)
            {
                Parameters = { Db.P(slotId), Db.P(contentHash), Db.P(item.DocumentId), Db.P(FailedSlotState(item.Outcome)), Db.P(EngineText.Cut(item.Reason, 500)) },
            };
            await slot.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// An item's state for its upload slot when its bytes have no live set: <c>malware</c> or <c>refused</c> for a failure, null
    /// for an indexing outcome (only <c>indexed</c> is written, <see cref="UploadSlotSql"/>).
    /// </summary>
    private static string? FailedSlotState(Outcome outcome) => outcome switch
    {
        Outcome.Added or Outcome.Updated or Outcome.Unchanged or Outcome.Degraded => null,
        Outcome.Malware => Domain.Entities.UploadStates.Malware,
        _ => Domain.Entities.UploadStates.Refused,
    };

    public async Task CompleteFrontierAsync(FrontierCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        await using (var command = completion.KeepFacts
            ? new NpgsqlCommand(FrontierIdleSql, Connection, Transaction)
            {
                Parameters = { Db.P(completion.Classification), Db.P(completion.SourceId), Db.P(completion.UrlHash), Db.P(completion.RunId) },
            }
            : new NpgsqlCommand(FrontierSql, Connection, Transaction)
            {
                Parameters =
                {
                    Db.P(completion.Classification), Db.P(completion.SourceId), Db.P(completion.UrlHash), Db.P(completion.RunId),
                    Db.P(completion.FetchedAt.ToUniversalTime()), Db.P(completion.Status), Db.P(completion.ETag), Db.P(completion.LastModified),
                    Db.P(completion.ContentHash), Db.P(completion.RedirectTo),
                },
            })
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (completion.SeedFailed)
        {
            await using var seed = new NpgsqlCommand(SeedFailedSql, Connection, Transaction) { Parameters = { Db.P(completion.RunId) } };
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public Task EnqueueAsync(IJobQueue queue, IReadOnlyList<JobRequest> jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return jobs.Count == 0 ? Task.CompletedTask : queue.EnqueueAsync(jobs, Transaction, cancellationToken);
    }

    /// <summary>The job's completion, fenced by its lease: false when the lease was lost (the caller rolls back).</summary>
    public Task<bool> CompleteJobAsync(IJobQueue queue, Guid jobId, Guid owner, string? resultJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return queue.CompleteAsync(jobId, owner, resultJson, Transaction, cancellationToken);
    }

    /// <summary>
    /// The job attempt's model calls added to <c>CollectionDailyUsage</c> (engine design §10.6): the last statement before
    /// <see cref="CommitAsync"/>, which drops them from <paramref name="usage"/> once committed. A rollback leaves them pending.
    /// </summary>
    public async Task AddUsageAsync(JobUsage usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _usage = (usage, await usage.WriteAsync(Connection, Transaction, cancellationToken));
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await Transaction.CommitAsync(cancellationToken);
        if (_usage is { } added)
        {
            added.Usage.Committed(added.Written);
            _usage = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
