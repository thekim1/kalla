using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>A chunk set found by its key or as a document's live pointer.</summary>
public sealed record ChunkSetRow(Guid Id, ChunkSetKey Key, string State, byte[] InputHash, int ChunkCount);

/// <summary>
/// The index writer of engine design §14 (data-model §2.2, §3.2). A chunk is searchable iff <c>Chunks."DeletedAt"</c> is
/// null. A set of at most <see cref="BatchSize"/> chunks is written <b>once</b>, inside the caller's fenced swap transaction
/// (<see cref="WriteAndSwapInAsync"/>, rule 2a, #501): the set row goes in as <c>live</c> or <c>held</c> and its chunks
/// visible (or deleted when held), so the swap updates only the previous set's chunks. A larger set is written
/// <b>invisible</b> (<c>writing</c>, chunks deleted) in batches of ≤ 1,000 rows, each its own short transaction, and goes
/// live in <b>one short swap</b> inside the caller's fenced transaction (<see cref="SwapInAsync"/>): the live pointer moves,
/// the new set becomes <c>live</c> (chunks visible) or <c>held</c> (chunks stay deleted), the previous set becomes
/// <c>superseded</c> with its chunks deleted. A set with the wanted key that
/// is <c>superseded</c> (rollback within 14 days) is swapped back without a write; a noise-only change re-keys the live set
/// in one row. Sets never swapped (a crash between the batches and the swap) stay invisible until the orphan sweep removes
/// them. Vectors are written as pgvector text through <c>unnest</c> (the <c>vector</c> column is not in the EF model);
/// each profile's partial HNSW index is created before its first chunk (<c>content.rag_ensure_profile_index</c>).
/// </summary>
public sealed class IndexWriter(JobDatabase database)
{
    public const int BatchSize = 1000;

    /// <summary>Writing sets older than this, and superseded sets past the rollback window, are swept (engine design §14 rule 5).</summary>
    public static readonly TimeSpan WritingOrphanAge = TimeSpan.FromHours(24);

    public static readonly TimeSpan RollbackWindow = TimeSpan.FromDays(14);

    private const string InsertSetSql = """
        INSERT INTO content."ChunkSets" ("Id", "DocumentId", "Classification", "CollectionId", "PipelineVersionId", "SiteProfileVersionId",
                                        "EmbeddingProfileId", "ContentHash", "InputHash", "StageKeys", "State", "PiiFlagged", "PiiCounts", "ChunkCount",
                                        "ParseRuleKey", "LiveAt")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, $11, $12, $13::jsonb, $14, $15, $16)
        """;

    private const string InsertChunksSql = """
        INSERT INTO content."Chunks" ("Id", "ChunkSetId", "DocumentId", "CollectionId", "Classification", "Kind", "Ordinal", "HeadingPath", "Page",
                                     "PageEnd", "Text", "SearchText", "OverlapChars", "TextHash", "TokenCount", "Tags", "DocumentType",
                                     "SourceModifiedAt", "EmbeddingProfileId", "Embedding", "DeletedAt", "ContentKind", "LargeTable")
        SELECT u.id, u.cs, u.doc, u.coll, u.lvl, u.kind, u.ord, u.heading, u.page, u.page_end, u.body, u.search, u.overlap, u.hash, u.tokens,
               ARRAY(SELECT jsonb_array_elements_text(u.tags)), u.dtype, u.modified, u.profile, u.emb::vector, u.deleted, u.content_kind, u.large_table
        FROM unnest($1::uuid[], $2::uuid[], $3::uuid[], $4::uuid[], $5::varchar[], $6::varchar[], $7::int[], $8::text[], $9::int[], $10::int[],
                    $11::text[], $12::text[], $13::smallint[], $14::bytea[], $15::int[], $16::jsonb[], $17::varchar[], $18::timestamptz[],
                    $19::uuid[], $20::text[], $21::timestamptz[], $22::varchar[], $23::bool[])
             AS u(id, cs, doc, coll, lvl, kind, ord, heading, page, page_end, body, search, overlap, hash, tokens, tags, dtype, modified, profile, emb, deleted,
                  content_kind, large_table)
        """;

    private const string SetColumns = """
        "Id", "DocumentId", "PipelineVersionId", "SiteProfileVersionId", "EmbeddingProfileId", "ContentHash", "State", "InputHash", "ChunkCount",
        "ParseRuleKey"
        """;

    private const string FindSql = $"""
        SELECT {SetColumns} FROM content."ChunkSets"
        WHERE "DocumentId" = $1 AND "PipelineVersionId" = $2 AND "SiteProfileVersionId" IS NOT DISTINCT FROM $3 AND "EmbeddingProfileId" = $4
          AND "ContentHash" = $5 AND "ParseRuleKey" IS NOT DISTINCT FROM $6
        """;

    private const string LiveSql = $"""
        SELECT {SetColumns} FROM content."ChunkSets" WHERE "Id" = (SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1)
        """;

    private const string DeleteWritingSql = """DELETE FROM content."ChunkSets" WHERE "Id" = $1 AND "State" = 'writing'""";

    private const string ProfileGuardSql = "SELECT content.rag_lock_profile_if_unchanged($1, $2, $3, $4)";

    private const string LockDocumentSql = """SELECT "LiveChunkSetId", "DeletedAt" IS NOT NULL FROM content."Documents" WHERE "Id" = $1 FOR UPDATE""";

    private const string SupersedeSetSql = """UPDATE content."ChunkSets" SET "State" = 'superseded', "SupersededAt" = $2 WHERE "Id" = $1""";

    private const string HideChunksSql = """UPDATE content."Chunks" SET "DeletedAt" = $2 WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NULL""";

    // A live set's chunks are visible unless the document itself is soft-deleted; a held set's stay deleted.
    private const string StateSql = """
        UPDATE content."ChunkSets"
           SET "State" = $2, "LiveAt" = CASE WHEN $2 = 'live' THEN $3 ELSE "LiveAt" END, "SupersededAt" = NULL
         WHERE "Id" = $1
        """;

    private const string ChunkStateSql = """
        UPDATE content."Chunks" SET "DeletedAt" = CASE WHEN $2 = 'live' AND NOT $4 THEN NULL ELSE coalesce("DeletedAt", $3) END
         WHERE "ChunkSetId" = $1
        """;

    private const string PointerSql = """
        UPDATE content."Documents" SET "LiveChunkSetId" = $2, "PipelineVersionId" = coalesce($3, "PipelineVersionId") WHERE "Id" = $1
        """;

    private const string ReKeySql = """
        UPDATE content."ChunkSets" SET "PipelineVersionId" = $2, "SiteProfileVersionId" = $3, "ContentHash" = $4, "ParseRuleKey" = $5 WHERE "Id" = $1
        """;

    private const string ReKeyDocumentSql = """UPDATE content."Documents" SET "PipelineVersionId" = $2 WHERE "LiveChunkSetId" = $1""";

    private const string PiiFlaggedSql = """UPDATE content."ChunkSets" SET "PiiFlaggedAt" = coalesce("PiiFlaggedAt", $2) WHERE "Id" = $1""";

    private const string SoftDeleteChunksSql = """
        UPDATE content."Chunks" SET "DeletedAt" = $2
         WHERE "ChunkSetId" IN (SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = ANY($1) AND "DeletedAt" IS NULL)
           AND "DeletedAt" IS NULL
        """;

    private const string SoftDeleteSql = """
        UPDATE content."Documents" SET "DeletedAt" = $2, "PurgeAfter" = $3 WHERE "Id" = ANY($1) AND "DeletedAt" IS NULL
        """;

    private const string SweepSql = """
        DELETE FROM content."ChunkSets" WHERE "Id" IN (
            SELECT s."Id" FROM content."ChunkSets" s
            WHERE ((s."State" = 'writing' AND s."CreatedAt" < $4 - $1::interval)
                OR (s."State" = 'superseded' AND s."SupersededAt" < $4 - $2::interval))
              AND NOT EXISTS (SELECT 1 FROM content."Documents" d WHERE d."LiveChunkSetId" = s."Id")
            LIMIT $3
            FOR UPDATE SKIP LOCKED)
        """;

    private readonly ConcurrentDictionary<Guid, bool> _profileIndexes = new();

    /// <summary>The set with <paramref name="key"/>, or null.</summary>
    public async Task<ChunkSetRow?> FindAsync(ChunkSetKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return await ReadSetAsync(FindSql, cancellationToken,
            Db.P(key.DocumentId), Db.P(key.PipelineVersionId), Db.P(key.SiteProfileVersionId), Db.P(key.EmbeddingProfileId), Db.P(key.ContentHash),
            Db.P(key.ParseRuleKey));
    }

    /// <summary>The set the document's live pointer names (<c>live</c> or <c>held</c>), or null.</summary>
    public Task<ChunkSetRow?> LiveAsync(Guid documentId, CancellationToken cancellationToken) => ReadSetAsync(LiveSql, cancellationToken, Db.P(documentId));

    /// <summary>Removes a <c>writing</c> set a crashed attempt left under the key this attempt needs (invisible, so safe).</summary>
    public async Task DeleteWritingAsync(Guid chunkSetId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DeleteWritingSql, connection) { Parameters = { Db.P(chunkSetId) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The profile's partial HNSW index exists (created on first use; a no-op afterwards, data-model §3.2).</summary>
    public async Task EnsureProfileIndexAsync(Guid profileId, int dimensions, CancellationToken cancellationToken)
    {
        if (_profileIndexes.ContainsKey(profileId))
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT content.rag_ensure_profile_index($1, $2)", connection, transaction)
        {
            Parameters = { Db.P(profileId), Db.P(dimensions) },
        })
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _profileIndexes[profileId] = true;
    }

    /// <summary>Rule 2: the set as <c>writing</c>, then its chunks invisible in batches of ≤ 1,000, each its own transaction.</summary>
    public async Task WriteInvisibleAsync(ChunkSetRecord set, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(chunks);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var insert = InsertSet(set, connection, null))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteChunksAsync(connection, chunks, cancellationToken);
    }

    /// <summary>
    /// Rule 2 for vectors embedded with <paramref name="embeddedWith"/>: the set is inserted only while the profile still has that
    /// model, size and <c>RequestDimensions</c>, with the profile row held FOR SHARE until the insert commits
    /// (<c>content.rag_lock_profile_if_unchanged</c>; admin design §4.5, Decisions "Editable embedding profiles before use"). An
    /// admin save locks the row FOR UPDATE before its in-use check, so either it sees this set and refuses, or it committed
    /// first and nothing is inserted here: <c>embed.profile_changed</c>, requeued without an attempt to embed with the new
    /// values. Once the set exists the profile cannot change, so its index is ensured at the profile's size after the insert.
    /// </summary>
    /// <exception cref="StageTransientException"><c>embed.profile_changed</c>.</exception>
    public async Task WriteInvisibleAsync(ChunkSetRecord set, IReadOnlyList<ChunkRecord> chunks, EmbeddingProfileRecord embeddedWith,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddedWith);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (!await ProfileUnchangedAsync(connection, transaction, embeddedWith, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw ProfileChanged();
            }

            await using (var insert = InsertSet(set, connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await EnsureProfileIndexAsync(embeddedWith.Id, embeddedWith.Dimensions, cancellationToken);
        await WriteChunksAsync(connection, chunks, cancellationToken);
    }

    private static async Task WriteChunksAsync(NpgsqlConnection connection, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        foreach (var batch in chunks.Chunk(BatchSize))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = InsertChunks(batch, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Rule 3's state change inside the caller's fenced transaction: the document's previous set superseded with its chunks
    /// deleted, <paramref name="chunkSetId"/> <c>live</c> (chunks visible unless the document is soft-deleted) or
    /// <c>held</c>, the live pointer moved and, when given, the document's pipeline version set.
    /// </summary>
    public static async Task SwapInAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId, Guid chunkSetId, string state,
        Guid? pipelineVersionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (previous, documentDeleted) = await LockDocumentAsync(connection, transaction, documentId, cancellationToken);
        var at = now.ToUniversalTime();
        await SupersedeAsync(connection, transaction, previous, chunkSetId, at, cancellationToken);
        await ApplyStateAsync(connection, transaction, chunkSetId, state, at, documentDeleted, cancellationToken);
        await PointAsync(connection, transaction, documentId, chunkSetId, pipelineVersionId, cancellationToken);
    }

    /// <summary>
    /// Rule 2a (#501) inside the caller's fenced transaction, after its fence and published-version checks: the profile guard
    /// (as <see cref="WriteInvisibleAsync(ChunkSetRecord, IReadOnlyList{ChunkRecord}, EmbeddingProfileRecord, CancellationToken)"/>),
    /// the profile's HNSW index, the set inserted as <paramref name="state"/> and its chunks inserted once (visible when
    /// <c>live</c> and the document is not soft-deleted, else deleted), the previous set superseded and the live pointer moved.
    /// At most <see cref="BatchSize"/> chunks: one <c>INSERT … SELECT FROM unnest</c>. Returns false, having written nothing,
    /// when the profile changed (<c>embed.profile_changed</c>); the caller rolls back. Call <see cref="Committed"/> after the
    /// commit, so a profile index created here is remembered only once it exists.
    /// </summary>
    public async Task<bool> WriteAndSwapInAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ChunkSetRecord set, string state,
        IReadOnlyList<ChunkRecord> chunks, EmbeddingProfileRecord embeddedWith, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddedWith);
        if (chunks.Count > BatchSize)
        {
            throw new ArgumentException($"A set of more than {BatchSize} chunks is written invisible first (rule 2).", nameof(chunks));
        }

        var (previous, documentDeleted) = await LockDocumentAsync(connection, transaction, set.Key.DocumentId, cancellationToken);
        if (!await ProfileUnchangedAsync(connection, transaction, embeddedWith, cancellationToken))
        {
            return false;
        }

        if (!_profileIndexes.ContainsKey(embeddedWith.Id))
        {
            await using var ensure = new NpgsqlCommand("SELECT content.rag_ensure_profile_index($1, $2)", connection, transaction)
            {
                Parameters = { Db.P(embeddedWith.Id), Db.P(embeddedWith.Dimensions) },
            };
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        var at = now.ToUniversalTime();
        await SupersedeAsync(connection, transaction, previous, set.Id, at, cancellationToken);
        await using (var insert = InsertSet(set with { State = state }, connection, transaction, state == ChunkSetState.Live ? at : null))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var deletedAt = state == ChunkSetState.Live && !documentDeleted ? (DateTimeOffset?)null : at;
        if (chunks.Count > 0)
        {
            await using var insertChunks = InsertChunks([.. chunks.Select(c => c with { DeletedAt = deletedAt })], connection, transaction);
            await insertChunks.ExecuteNonQueryAsync(cancellationToken);
        }

        await PointAsync(connection, transaction, set.Key.DocumentId, set.Id, set.Key.PipelineVersionId, cancellationToken);
        return true;
    }

    /// <summary>After the commit of <see cref="WriteAndSwapInAsync"/>: the profile's index exists from now on.</summary>
    public void Committed(Guid embeddingProfileId) => _profileIndexes[embeddingProfileId] = true;

    internal static StageTransientException ProfileChanged() => new("embed.profile_changed", null, countsAttempt: false);

    private static async Task<bool> ProfileUnchangedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EmbeddingProfileRecord profile,
        CancellationToken cancellationToken)
    {
        await using var guard = new NpgsqlCommand(ProfileGuardSql, connection, transaction)
        {
            Parameters = { Db.P(profile.Id), Db.P(profile.GatewayModel), Db.P(profile.Dimensions), Db.P(profile.RequestDimensions) },
        };
        return await guard.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<(Guid? Previous, bool DocumentDeleted)> LockDocumentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid documentId, CancellationToken cancellationToken)
    {
        await using var lockDocument = new NpgsqlCommand(LockDocumentSql, connection, transaction) { Parameters = { Db.P(documentId) } };
        await using var reader = await lockDocument.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Unknown document.");
        }

        return (Db.Nullable<Guid>(reader, 0), reader.GetBoolean(1));
    }

    private static async Task SupersedeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? previous, Guid chunkSetId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (previous is { } old && old != chunkSetId)
        {
            await ExecuteAsync(connection, transaction, SupersedeSetSql, cancellationToken, Db.P(old), Db.P(at));
            await ExecuteAsync(connection, transaction, HideChunksSql, cancellationToken, Db.P(old), Db.P(at));
        }
    }

    private static async Task PointAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId, Guid chunkSetId,
        Guid? pipelineVersionId, CancellationToken cancellationToken)
    {
        await using var pointer = new NpgsqlCommand(PointerSql, connection, transaction)
        {
            Parameters = { Db.P(documentId), Db.P(chunkSetId), Db.P(pipelineVersionId) },
        };
        await pointer.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary><c>live</c> or <c>held</c> on the current set without a swap (a clearance, the pending sweep, a collection level change).</summary>
    public static async Task SetStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid chunkSetId, string state, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool documentDeleted;
        await using (var deleted = new NpgsqlCommand("""
            SELECT d."DeletedAt" IS NOT NULL FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId" WHERE s."Id" = $1 FOR UPDATE OF d
            """, connection, transaction)
        {
            Parameters = { Db.P(chunkSetId) },
        })
        {
            documentDeleted = await deleted.ExecuteScalarAsync(cancellationToken) is true;
        }

        await ApplyStateAsync(connection, transaction, chunkSetId, state, now.ToUniversalTime(), documentDeleted, cancellationToken);
    }

    /// <summary>Rule 4 inside the caller's fenced transaction: the live set takes the new key (its salt too) in one row, no new chunks.</summary>
    public static async Task ReKeyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid chunkSetId, ChunkSetKey newKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        await ExecuteAsync(connection, transaction, ReKeySql, cancellationToken,
            Db.P(chunkSetId), Db.P(newKey.PipelineVersionId), Db.P(newKey.SiteProfileVersionId), Db.P(newKey.ContentHash), Db.P(newKey.ParseRuleKey));
        await ExecuteAsync(connection, transaction, ReKeyDocumentSql, cancellationToken, Db.P(chunkSetId), Db.P(newKey.PipelineVersionId));
    }

    /// <summary>
    /// The set's <c>PiiFlaggedAt</c> (processing design §9.5, #175) inside the caller's transaction: <paramref name="at"/>, the
    /// time of the event 5103 that follows, unless the set already has one (a re-flag keeps the first time).
    /// </summary>
    public static Task MarkPiiFlaggedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid chunkSetId, DateTimeOffset at,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, PiiFlaggedSql, cancellationToken, Db.P(chunkSetId), Db.P(at.ToUniversalTime()));

    /// <summary>
    /// Soft-deletes the documents (connectors design §13.4): <c>DeletedAt</c> now, <c>PurgeAfter</c> after the 14-day grace, their
    /// live sets' chunks deleted; in batches of 1,000, each its own transaction. Returns how many were deleted now.
    /// </summary>
    public async Task<int> SoftDeleteDocumentsAsync(IReadOnlyList<Guid> documentIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var deleted = 0;
        foreach (var batch in documentIds.Chunk(BatchSize))
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var at = now.ToUniversalTime();
            await using (var chunks = new NpgsqlCommand(SoftDeleteChunksSql, connection, transaction) { Parameters = { Db.P(batch), Db.P(at) } })
            {
                await chunks.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var documents = new NpgsqlCommand(SoftDeleteSql, connection, transaction)
            {
                Parameters = { Db.P(batch), Db.P(at), Db.P((now + DocumentLifecycle.Grace).ToUniversalTime()) },
            })
            {
                deleted += await documents.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return deleted;
    }

    /// <summary>
    /// Rule 5 (maintenance): sets left <c>writing</c> for 24 hours and <c>superseded</c> sets past the 14-day rollback window
    /// before <paramref name="now"/> are deleted with their chunks, 1,000 per transaction; a younger superseded set is kept for
    /// a rollback to swap back (§12.3). Returns how many sets were removed.
    /// </summary>
    public async Task<long> SweepOrphansAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(SweepSql, connection)
            {
                Parameters = { Db.P(WritingOrphanAge), Db.P(RollbackWindow), Db.P(BatchSize), Db.P(now.ToUniversalTime()) },
            };
            var removed = await command.ExecuteNonQueryAsync(cancellationToken);
            total += removed;
            if (removed < BatchSize)
            {
                return total;
            }
        }
    }

    internal static NpgsqlCommand InsertSet(ChunkSetRecord set, NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset? liveAt = null) =>
        new(InsertSetSql, connection, transaction)
        {
            Parameters =
            {
                Db.P(set.Id), Db.P(set.Key.DocumentId), Db.P(set.Classification), Db.P(set.CollectionId), Db.P(set.Key.PipelineVersionId),
                Db.P(set.Key.SiteProfileVersionId), Db.P(set.Key.EmbeddingProfileId), Db.P(set.Key.ContentHash), Db.P(set.InputHash),
                Db.P(set.StageKeys), Db.P(set.State), Db.P(set.PiiFlagged), Db.P(set.PiiCounts is null ? null : ContractJson.Serialize(set.PiiCounts)),
                Db.P(set.ChunkCount), Db.P(set.Key.ParseRuleKey), Db.P(Db.Utc(liveAt)),
            },
        };

    internal static NpgsqlCommand InsertChunks(IReadOnlyList<ChunkRecord> chunks, NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        new(InsertChunksSql, connection, transaction)
        {
            Parameters =
            {
                Db.P(chunks.Select(c => c.Id).ToArray()),
                Db.P(chunks.Select(c => c.ChunkSetId).ToArray()),
                Db.P(chunks.Select(c => c.DocumentId).ToArray()),
                Db.P(chunks.Select(c => c.CollectionId).ToArray()),
                Db.P(chunks.Select(c => c.Classification).ToArray()),
                Db.P(chunks.Select(c => c.Kind).ToArray()),
                Db.P(chunks.Select(c => c.Ordinal).ToArray()),
                Db.P(chunks.Select(c => c.Draft.HeadingPath).ToArray()),
                Db.P(chunks.Select(c => c.Draft.Page).ToArray()),
                Db.P(chunks.Select(c => c.Draft.PageEnd).ToArray()),
                Db.P(chunks.Select(c => c.Draft.Text).ToArray()),
                Db.P(chunks.Select(c => c.Draft.SearchText).ToArray()),
                Db.P(chunks.Select(c => (short)Math.Clamp(c.Draft.OverlapChars, 0, short.MaxValue)).ToArray()),
                Db.P(chunks.Select(c => Db.Bytes(c.Draft.TextHash)).ToArray()),
                Db.P(chunks.Select(c => c.Draft.TokenCount).ToArray()),
                Db.P(chunks.Select(c => JsonSerializer.Serialize(c.Draft.Tags)).ToArray()),
                Db.P(chunks.Select(c => c.Draft.DocumentType).ToArray()),
                Db.P(chunks.Select(c => c.Draft.SourceModifiedAt is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null).ToArray()),
                Db.P(chunks.Select(c => c.EmbeddingProfileId).ToArray()),
                Db.P(chunks.Select(c => VectorText.Format(c.Embedding)).ToArray()),
                Db.P(chunks.Select(c => Db.Utc(c.DeletedAt)).ToArray()),
                Db.P(chunks.Select(c => c.Draft.ContentKind).ToArray()),
                Db.P(chunks.Select(c => c.Draft.LargeTable).ToArray()),
            },
        };

    private static async Task ApplyStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid chunkSetId, string state, DateTimeOffset at,
        bool documentDeleted, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, StateSql, cancellationToken, Db.P(chunkSetId), Db.P(state), Db.P(at));
        await ExecuteAsync(connection, transaction, ChunkStateSql, cancellationToken, Db.P(chunkSetId), Db.P(state), Db.P(at), Db.P(documentDeleted));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ChunkSetRow?> ReadSetAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var key = new ChunkSetKey(reader.GetGuid(1), reader.GetGuid(2), Db.Nullable<Guid>(reader, 3), reader.GetGuid(4), reader.GetFieldValue<byte[]>(5),
            Db.String(reader, 9));
        return new ChunkSetRow(reader.GetGuid(0), key, reader.GetString(6), reader.GetFieldValue<byte[]>(7), reader.GetInt32(8));
    }
}
