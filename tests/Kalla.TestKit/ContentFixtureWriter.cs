using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Kalla.Retrieval.Postgres;
using Kalla.Retrieval.Text;

namespace Kalla.TestKit;

/// <summary>
/// Writes documents, chunk sets and chunks with embeddings straight into <c>content</c>, the way the index writer leaves
/// them (engine design §14; data-model §2.2), so retrieval and chat tests need no worker until J4 (P1 README §4):
/// a <c>live</c> set's chunks have <c>DeletedAt</c> null and are the document's <c>LiveChunkSetId</c>; chunks of
/// <c>writing</c>, <c>held</c> and <c>superseded</c> sets carry <c>DeletedAt</c>. Chunks go in by binary <c>COPY</c>, as
/// the writer does. <c>SearchText</c> defaults to the worker's rule: text plus the compound parts of the heading path and
/// text (<see cref="SwedishDecompounder"/>, data-model §3.1). Content references config by id only, so collections and
/// sources need not exist in <c>config</c>.
/// </summary>
/// <param name="workerConnectionString">ragdb as <c>kalla_worker</c> (the content writer).</param>
/// <param name="adminConnectionString">ragdb as <c>kalla_admin</c> (embedding profiles live in <c>config</c>).</param>
/// <param name="embed">Default embedding of a chunk without one (for example the FakeGateway's feature hashing).</param>
public sealed class ContentFixtureWriter(string workerConnectionString, string adminConnectionString, Func<string, int, float[]>? embed = null)
    : IAsyncDisposable
{
    private readonly NpgsqlDataSource _worker = RetrievalDataSource.Create(workerConnectionString);

    public ValueTask DisposeAsync() => _worker.DisposeAsync();

    /// <summary>
    /// An active embedding profile of <paramref name="dimensions"/> with its HNSW index, created as the worker does
    /// (<c>content.rag_ensure_profile_index</c>) before any chunk of the profile exists.
    /// </summary>
    public async Task<Guid> AddEmbeddingProfileAsync(int dimensions, CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7();
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens",
                    "IsDefault", "State", "MaxResidency", "UpdatedAt", "UpdatedBy")
                VALUES ($1, $2, 'fake-embed', $3, 'cosine', 8192, false, 'active', 'Eu', now(), 'test')
                """, admin);
            insert.Parameters.Add(new NpgsqlParameter { Value = id });
            insert.Parameters.Add(new NpgsqlParameter { Value = "fixture-" + id.ToString("N") });
            insert.Parameters.Add(new NpgsqlParameter { Value = dimensions });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var connection = await _worker.OpenConnectionAsync(cancellationToken);
        await using var ensure = new NpgsqlCommand("SELECT content.rag_ensure_profile_index($1, $2)", connection);
        ensure.Parameters.Add(new NpgsqlParameter { Value = id });
        ensure.Parameters.Add(new NpgsqlParameter { Value = dimensions });
        await ensure.ExecuteScalarAsync(cancellationToken);
        return id;
    }

    /// <summary>A new document with one chunk set in <see cref="FixtureDocumentSpec.SetState"/>.</summary>
    public async Task<FixtureDocument> AddDocumentAsync(FixtureDocumentSpec spec, IReadOnlyList<FixtureChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var documentId = Guid.CreateVersion7();
        var sourceId = spec.SourceId ?? Guid.NewGuid();
        var canonical = spec.CanonicalIdentity ?? spec.Url ?? (spec.IdentityKind == "upload" ? "upload:" + Guid.NewGuid() : "https://fixture.invalid/" + documentId);
        await using var connection = await _worker.OpenConnectionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash",
                "ContentHashVersion", "Title", "Url", "MediaType", "Language", "DisplayName", "SourceModifiedAt", "FetchedAt", "PageCount",
                "ConsecutiveFailures", "FirstSeenAt", "LastSeenAt")
            VALUES ($1, $2, $3, $4, $5, $6, 1, $7, $8, $9, $10, $11, $12, $13, $14, 0, now(), now())
            """, connection))
        {
            AddValues(insert, documentId, sourceId, spec.Classification, spec.IdentityKind, canonical,
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)), spec.Title, spec.Url, spec.MediaType, spec.Language, spec.DisplayName,
                spec.SourceModifiedAt?.ToUniversalTime(), spec.FetchedAt?.ToUniversalTime(), spec.PageCount);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var document = new FixtureDocument(documentId, sourceId, spec.CollectionId, spec.EmbeddingProfileId, spec.Classification, null, []);
        var (setId, chunkIds) = await WriteSetAsync(connection, document, chunks, spec.SetState, cancellationToken);
        return spec.SetState == "live" ? document with { LiveChunkSetId = setId, ChunkIds = chunkIds } : document with { ChunkIds = chunkIds };
    }

    /// <summary>
    /// The index writer's swap (engine design §14): a new live set for <paramref name="document"/>, its previous live set
    /// <c>superseded</c> with its chunks deleted, in one transaction.
    /// </summary>
    public async Task<FixtureDocument> SwapAsync(FixtureDocument document, IReadOnlyList<FixtureChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await using var connection = await _worker.OpenConnectionAsync(cancellationToken);
        var (setId, chunkIds) = await WriteSetAsync(connection, document, chunks, "writing", cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var previous = (object?)document.LiveChunkSetId ?? Guid.Empty;
        foreach (var (sql, values) in new (string, object[])[]
                 {
                     ("""UPDATE content."Chunks" SET "DeletedAt" = NULL WHERE "ChunkSetId" = $1""", [setId]),
                     ("""UPDATE content."ChunkSets" SET "State" = 'live', "LiveAt" = now() WHERE "Id" = $1""", [setId]),
                     ("""UPDATE content."Chunks" SET "DeletedAt" = now() WHERE "ChunkSetId" = $1""", [previous]),
                     ("""UPDATE content."ChunkSets" SET "State" = 'superseded', "SupersededAt" = now() WHERE "Id" = $1""", [previous]),
                     ("""UPDATE content."Documents" SET "LiveChunkSetId" = $1 WHERE "Id" = $2""", [setId, document.Id]),
                 })
        {
#pragma warning disable CA2100 // Constant statements of this fixture.
            await using var swap = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            AddValues(swap, values);
            await swap.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return document with { LiveChunkSetId = setId, ChunkIds = chunkIds };
    }

    /// <summary>
    /// Another set of <paramref name="document"/> in <paramref name="state"/> (<c>writing</c>, <c>held</c> or
    /// <c>superseded</c>): its chunks carry <c>DeletedAt</c>; the document keeps its live set.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AddInvisibleSetAsync(
        FixtureDocument document, IReadOnlyList<FixtureChunk> chunks, string state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (state == "live")
        {
            throw new ArgumentException("Use SwapAsync for a new live set.", nameof(state));
        }

        await using var connection = await _worker.OpenConnectionAsync(cancellationToken);
        var (_, chunkIds) = await WriteSetAsync(connection, document, chunks, state, cancellationToken);
        return chunkIds;
    }

    /// <summary>
    /// Writes <paramref name="collectionId"/>'s <c>content."KeywordStatistics"</c> rows as the worker does (A-29,
    /// <c>Kalla.Worker.Engine.KeywordStatistics</c>): per profile with at least 100 live child chunks, the lexemes of
    /// <c>ts_stat</c> in at least 10 % of them, with that count; the collection's other rows go. As the worker role, in one
    /// transaction. Tests of kalla-api's stop list call it where the worker would have run.
    /// </summary>
    public async Task RefreshKeywordStatisticsAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _worker.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new NpgsqlCommand("""DELETE FROM content."KeywordStatistics" WHERE "CollectionId" = $1""", connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = collectionId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand("""
            WITH counts AS (
                SELECT "EmbeddingProfileId" AS profile, count(*) AS chunks
                FROM content."Chunks"
                WHERE "CollectionId" = $1 AND "DeletedAt" IS NULL AND "Kind" = 'child' AND "EmbeddingProfileId" IS NOT NULL
                GROUP BY 1
                HAVING count(*) >= 100
            )
            INSERT INTO content."KeywordStatistics" ("EmbeddingProfileId", "CollectionId", "Lexeme", "Ndoc", "ChunkCount", "ComputedAt")
            SELECT counts.profile, $1, s.word, s.ndoc, counts.chunks, now()
            FROM counts, LATERAL ts_stat(format(
                'SELECT "SearchVector" FROM content."Chunks" WHERE "Kind" = ''child'' AND "DeletedAt" IS NULL AND "EmbeddingProfileId" = %L AND "CollectionId" = %L',
                counts.profile, $1::uuid)) s
            WHERE s.ndoc >= counts.chunks * 0.10
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = collectionId });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Runs SQL as the worker (soft deletes and other edits a test needs).</summary>
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await _worker.OpenConnectionAsync(cancellationToken);
#pragma warning disable CA2100 // Test fixture SQL written by the test itself.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(Guid SetId, IReadOnlyList<Guid> ChunkIds)> WriteSetAsync(
        NpgsqlConnection connection, FixtureDocument document, IReadOnlyList<FixtureChunk> chunks, string state, CancellationToken cancellationToken)
    {
        var setId = Guid.CreateVersion7();
        var dimensions = await DimensionsAsync(connection, document.EmbeddingProfileId, cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO content."ChunkSets" ("Id", "DocumentId", "Classification", "CollectionId", "PipelineVersionId", "EmbeddingProfileId",
                "ContentHash", "InputHash", "State", "PiiFlagged", "ChunkCount", "CreatedAt", "LiveAt")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, false, $10, now(), CASE WHEN $9 = 'live' THEN now() END)
            """, connection))
        {
            AddValues(insert, setId, document.Id, document.Classification, document.CollectionId, Guid.NewGuid(), document.EmbeddingProfileId,
                setId.ToByteArray(), setId.ToByteArray(), state, chunks.Count);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var deletedAt = state == "live" ? (DateTimeOffset?)null : DateTimeOffset.UtcNow;
        var ids = new List<Guid>(chunks.Count);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY content."Chunks" ("Id", "ChunkSetId", "DocumentId", "CollectionId", "Classification", "Kind", "Ordinal", "HeadingPath",
                "Page", "PageEnd", "Text", "SearchText", "OverlapChars", "TextHash", "TokenCount", "Tags", "DocumentType", "SourceModifiedAt",
                "EmbeddingProfileId", "Embedding", "DeletedAt") FROM STDIN (FORMAT BINARY)
            """, cancellationToken))
        {
            for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
            {
                var chunk = chunks[ordinal];
                var id = Guid.CreateVersion7();
                ids.Add(id);
                var embedding = chunk.Kind == "child" ? chunk.Embedding ?? embed?.Invoke(chunk.Text, dimensions) : null;
                if (embedding is not null && embedding.Length != dimensions)
                {
                    throw new ArgumentException($"Chunk {ordinal} has {embedding.Length} dimensions; the profile has {dimensions}.", nameof(chunks));
                }

                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(setId, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(document.Id, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(document.CollectionId, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(chunk.Classification ?? document.Classification, NpgsqlDbType.Varchar, cancellationToken);
                await copy.WriteAsync(chunk.Kind, NpgsqlDbType.Varchar, cancellationToken);
                await copy.WriteAsync(chunk.Ordinal ?? ordinal, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(copy, chunk.HeadingPath, NpgsqlDbType.Text, cancellationToken);
                await WriteNullableAsync(copy, chunk.Page, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(copy, chunk.PageEnd, NpgsqlDbType.Integer, cancellationToken);
                await copy.WriteAsync(chunk.Text, NpgsqlDbType.Text, cancellationToken);
                await copy.WriteAsync(chunk.SearchText ?? SearchTextOf(chunk), NpgsqlDbType.Text, cancellationToken);
                await copy.WriteAsync(chunk.OverlapChars, NpgsqlDbType.Smallint, cancellationToken);
                await copy.WriteAsync(chunk.TextHash ?? SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text)), NpgsqlDbType.Bytea, cancellationToken);
                await copy.WriteAsync(chunk.TokenCount ?? Math.Max(1, chunk.Text.Length / 4), NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(copy, chunk.Tags, NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
                await WriteNullableAsync(copy, chunk.DocumentType, NpgsqlDbType.Varchar, cancellationToken);
                await WriteNullableAsync(copy, chunk.SourceModifiedAt?.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                if (chunk.Kind == "child")
                {
                    await copy.WriteAsync(document.EmbeddingProfileId, NpgsqlDbType.Uuid, cancellationToken);
                }
                else
                {
                    await copy.WriteNullAsync(cancellationToken);
                }

                if (embedding is null)
                {
                    await copy.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await copy.WriteAsync(new Vector(embedding), "vector", cancellationToken);
                }

                await WriteNullableAsync(copy, chunk.DeletedAt?.ToUniversalTime() ?? deletedAt, NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await copy.CompleteAsync(cancellationToken);
        }

        if (state == "live")
        {
            await using var live = new NpgsqlCommand("""UPDATE content."Documents" SET "LiveChunkSetId" = $1 WHERE "Id" = $2""", connection);
            AddValues(live, setId, document.Id);
            await live.ExecuteNonQueryAsync(cancellationToken);
        }

        return (setId, ids);
    }

    /// <summary>The worker's <c>SearchText</c>: text plus the compound parts of the heading path and text.</summary>
    public static string SearchTextOf(FixtureChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var parts = SwedishDecompounder.Default.PartsOf((chunk.HeadingPath ?? string.Empty) + "\n" + chunk.Text);
        return parts.Count == 0 ? chunk.Text : chunk.Text + "\n" + string.Join(' ', parts);
    }

    private static async Task<int> DimensionsAsync(NpgsqlConnection connection, Guid profileId, CancellationToken cancellationToken)
    {
        // The worker cannot read config."EmbeddingProfiles"; the profile's index definition carries the dimensions.
        await using var command = new NpgsqlCommand("""
            SELECT substring(indexdef FROM 'halfvec\((\d+)\)')::int FROM pg_indexes
            WHERE schemaname = 'content' AND indexname = 'IX_Chunks_Embedding_' || replace($1::text, '-', '')
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = profileId });
        return await command.ExecuteScalarAsync(cancellationToken) is int dimensions
            ? dimensions
            : throw new InvalidOperationException($"Profile {profileId} has no HNSW index; create it with AddEmbeddingProfileAsync.");
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await copy.WriteNullAsync(cancellationToken);
        }
        else
        {
            await copy.WriteAsync(value, type, cancellationToken);
        }
    }

    private static void AddValues(NpgsqlCommand command, params object?[] values)
    {
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
    }
}

/// <summary>What <see cref="ContentFixtureWriter.AddDocumentAsync"/> writes for a document.</summary>
public sealed record FixtureDocumentSpec
{
    public required Guid CollectionId { get; init; }

    public required Guid EmbeddingProfileId { get; init; }

    /// <summary>Defaults to a new source id.</summary>
    public Guid? SourceId { get; init; }

    public string Classification { get; init; } = "public";

    public string IdentityKind { get; init; } = "url";

    /// <summary>Defaults to <see cref="Url"/>, else a unique identity.</summary>
    public string? CanonicalIdentity { get; init; }

    public string? Url { get; init; }

    public string? Title { get; init; }

    public string? DisplayName { get; init; }

    public string? MediaType { get; init; } = "text/html";

    public string? Language { get; init; } = "sv";

    public DateTimeOffset? SourceModifiedAt { get; init; }

    public DateTimeOffset? FetchedAt { get; init; }

    public int? PageCount { get; init; }

    /// <summary><c>live</c> (searchable), or <c>writing</c> / <c>held</c> (chunks deleted, no live set).</summary>
    public string SetState { get; init; } = "live";
}

/// <summary>One chunk; unset values follow the worker's rules (ordinal by position, SHA-256 text hash, decompounded search text).</summary>
public sealed record FixtureChunk(string Text)
{
    public string? HeadingPath { get; init; }

    public string? SearchText { get; init; }

    public float[]? Embedding { get; init; }

    public byte[]? TextHash { get; init; }

    public int? Ordinal { get; init; }

    public string Kind { get; init; } = "child";

    /// <summary>Defaults to the document's level.</summary>
    public string? Classification { get; init; }

    public string[]? Tags { get; init; }

    public string? DocumentType { get; init; }

    public DateTimeOffset? SourceModifiedAt { get; init; }

    public int? Page { get; init; }

    public int? PageEnd { get; init; }

    public short OverlapChars { get; init; }

    public int? TokenCount { get; init; }

    /// <summary>Overrides the set's rule (a soft-deleted chunk in a live set).</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>A written document: ids for assertions.</summary>
public sealed record FixtureDocument(
    Guid Id,
    Guid SourceId,
    Guid CollectionId,
    Guid EmbeddingProfileId,
    string Classification,
    Guid? LiveChunkSetId,
    IReadOnlyList<Guid> ChunkIds);
