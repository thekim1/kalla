using System.Globalization;
using System.Text.Json;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Connectors.Persistence;
using static Ume.Rag.Worker.Processing.Persistence.VectorText;

namespace Ume.Rag.Worker.Processing.Persistence;

/// <summary>Vectors cross the wire as pgvector's text form (<c>[1,2,3]</c>): the <c>vector</c> columns are not in the EF model.</summary>
internal static class VectorText
{
    /// <summary>The first row of a read, or null.</summary>
    public static T? First<T>(IReadOnlyList<T> rows) where T : class => rows.Count > 0 ? rows[0] : null;

    public static string Format(IReadOnlyList<float> vector)
    {
        var builder = new System.Text.StringBuilder(vector.Count * 10 + 2).Append('[');
        for (var i = 0; i < vector.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }
}

/// <summary><see cref="INodeOutputStore"/> over <c>content."NodeOutputs"</c> (engine design §8).</summary>
public sealed class PostgresNodeOutputStore(JobDatabase database) : INodeOutputStore
{
    private const string FindSql = """
        SELECT "CacheKey", "Classification", "NodeType", "NodeVersion", "ParamsHash", "InputHash", "OutputHash", "BlobPath", "IsParseOutput",
               "CreatedAt", "LastUsedAt"
        FROM content."NodeOutputs" WHERE "CacheKey" = $1
        """;

    private const string AddSql = """
        INSERT INTO content."NodeOutputs" ("CacheKey", "Classification", "NodeType", "NodeVersion", "ParamsHash", "InputHash", "OutputHash",
                                          "BlobPath", "IsParseOutput", "CreatedAt", "LastUsedAt")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
        ON CONFLICT ("CacheKey") DO NOTHING
        """;

    private const string TouchSql = """
        UPDATE content."NodeOutputs" SET "LastUsedAt" = $2 WHERE "CacheKey" = $1 AND "LastUsedAt" < $2 - interval '1 day'
        """;

    public async Task<NodeOutputRecord?> FindAsync(byte[] cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FindSql, connection) { Parameters = { Db.P(cacheKey) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NodeOutputRecord(reader.GetFieldValue<byte[]>(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6), reader.GetString(7), reader.GetBoolean(8),
            reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10));
    }

    public async Task<bool> TryAddAsync(NodeOutputRecord output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = Add(output, connection, null);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task TouchAsync(byte[] cacheKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TouchSql, connection) { Parameters = { Db.P(cacheKey), Db.P(now.ToUniversalTime()) } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The insert as a command on the caller's connection, so the engine writes the rows in its segment commit.</summary>
    internal static NpgsqlCommand Add(NodeOutputRecord output, NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        new(AddSql, connection, transaction)
        {
            Parameters =
            {
                Db.P(output.CacheKey), Db.P(output.Classification), Db.P(output.NodeType), Db.P(output.NodeVersion), Db.P(output.ParamsHash),
                Db.P(output.InputHash), Db.P(output.OutputHash), Db.P(output.BlobPath), Db.P(output.IsParseOutput),
                Db.P(output.CreatedAt.ToUniversalTime()), Db.P(output.LastUsedAt.ToUniversalTime()),
            },
        };
}

/// <summary><see cref="IEmbeddingProfileStore"/> over <c>config."EmbeddingProfiles"</c>; the canary vector is read as <c>real[]</c>.</summary>
public sealed class PostgresEmbeddingProfileStore(JobDatabase database) : IEmbeddingProfileStore
{
    private const string Columns = """
        "Id", "Name", "GatewayModel", "Dimensions", "MaxInputTokens", "MaxResidency", "Distance", "CanaryText", "CanaryEmbedding"::real[],
        "IsDefault", "State", "RequestDimensions"
        """;

    public async Task<EmbeddingProfileRecord?> GetAsync(Guid profileId, CancellationToken cancellationToken) =>
        First(await ReadAsync($"""SELECT {Columns} FROM config."EmbeddingProfiles" WHERE "Id" = $1""", profileId, cancellationToken));

    public Task<IReadOnlyList<EmbeddingProfileRecord>> ActiveAsync(CancellationToken cancellationToken) =>
        ReadAsync($"""SELECT {Columns} FROM config."EmbeddingProfiles" WHERE "State" = 'active' ORDER BY "Name" """, null, cancellationToken);

    private async Task<IReadOnlyList<EmbeddingProfileRecord>> ReadAsync(string sql, Guid? id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (id is { } value)
        {
            command.Parameters.Add(Db.P(value));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var profiles = new List<EmbeddingProfileRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!ResidencyCeiling.TryParse(reader.GetString(5), out var residency))
            {
                continue;
            }

            profiles.Add(new EmbeddingProfileRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), residency)
            {
                Distance = reader.GetString(6),
                CanaryText = Db.String(reader, 7),
                CanaryEmbedding = reader.IsDBNull(8) ? null : reader.GetFieldValue<float[]>(8),
                IsDefault = reader.GetBoolean(9),
                State = reader.GetString(10),
                RequestDimensions = reader.GetBoolean(11),
            });
        }

        return profiles;
    }
}

/// <summary><see cref="IEmbeddingCache"/> over <c>content."EmbeddingCache"</c> (processing design §11); hashes are hex here, <c>bytea</c> there.</summary>
public sealed class PostgresEmbeddingCache(JobDatabase database) : IEmbeddingCache
{
    private const string GetSql = """
        SELECT "TextHash", "Embedding"::real[] FROM content."EmbeddingCache"
        WHERE "Classification" = $1 AND "EmbeddingProfileId" = $2 AND "TextHash" = ANY($3)
        """;

    private const string PutSql = """
        INSERT INTO content."EmbeddingCache" ("Classification", "EmbeddingProfileId", "TextHash", "Embedding", "LastUsedAt")
        SELECT $1, $2, u.h, u.e::vector, $5 FROM unnest($3::bytea[], $4::text[]) AS u(h, e)
        ON CONFLICT ("Classification", "EmbeddingProfileId", "TextHash") DO NOTHING
        """;

    private const string TouchSql = """
        UPDATE content."EmbeddingCache" SET "LastUsedAt" = $4
        WHERE "Classification" = $1 AND "EmbeddingProfileId" = $2 AND "TextHash" = ANY($3) AND "LastUsedAt" < $4 - interval '1 day'
        """;

    public async Task<IReadOnlyDictionary<string, float[]>> GetAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textHashes);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (textHashes.Count == 0)
        {
            return vectors;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(GetSql, connection)
        {
            Parameters = { Db.P(classification), Db.P(profileId), Db.P(textHashes.Select(Db.Bytes).ToArray()) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            vectors[Db.Hex(reader.GetFieldValue<byte[]>(0))] = reader.GetFieldValue<float[]>(1);
        }

        return vectors;
    }

    public async Task PutAsync(string classification, Guid profileId, IReadOnlyDictionary<string, float[]> vectors, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PutSql, connection)
        {
            Parameters =
            {
                Db.P(classification), Db.P(profileId), Db.P(vectors.Keys.Select(Db.Bytes).ToArray()),
                Db.P(vectors.Values.Select(v => VectorText.Format(v)).ToArray()), Db.P(now.ToUniversalTime()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TouchAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textHashes);
        if (textHashes.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TouchSql, connection)
        {
            Parameters = { Db.P(classification), Db.P(profileId), Db.P(textHashes.Select(Db.Bytes).ToArray()), Db.P(now.ToUniversalTime()) },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary><see cref="IParseObservationStore"/> over <c>content."ParseObservations"</c>.</summary>
public sealed class PostgresParseObservationStore(JobDatabase database) : IParseObservationStore
{
    private const string AddSql = """
        INSERT INTO content."ParseObservations" ("Id", "Classification", "DocumentId", "Domain", "PathPattern", "DocumentType", "Strategy", "Score",
                                                "Metrics", "ObservedAt")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb, $10)
        ON CONFLICT ("Id") DO NOTHING
        """;

    public async Task AddAsync(ParseObservationRecord observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(AddSql, connection)
        {
            Parameters =
            {
                Db.P(observation.Id), Db.P(observation.Classification), Db.P(observation.DocumentId), Db.P(observation.Domain),
                Db.P(observation.PathPattern), Db.P(observation.DocumentType), Db.P(observation.Strategy), Db.P(observation.Score),
                Db.P(observation.Metrics), Db.P(observation.ObservedAt.ToUniversalTime()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// <see cref="IChunkSetStore"/> over <c>content."ChunkSets"</c>, <c>"Chunks"</c> and the clearance columns of
/// <c>"Documents"</c>, for the personal-data lifecycle's clear and sweep paths (processing design §9.5–§9.6). The engine's
/// own write and fenced swap are <see cref="Engine.IndexWriter"/>'s; this store's <see cref="SwapAsync"/> is the unfenced
/// state change those paths need.
/// </summary>
public sealed class PostgresChunkSetStore(JobDatabase database) : IChunkSetStore
{
    private const string SetColumns = """
        "Id", "DocumentId", "PipelineVersionId", "SiteProfileVersionId", "EmbeddingProfileId", "ContentHash", "Classification", "CollectionId",
        "InputHash", "StageKeys"::text, "State", "PiiFlagged", "PiiCounts"::text, "ChunkCount", "CreatedAt", "LiveAt", "SupersededAt"
        """;

    public async Task InsertWritingAsync(ChunkSetRecord set, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(chunks);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = Engine.IndexWriter.InsertSet(set, connection, transaction))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var batch in chunks.Chunk(Engine.IndexWriter.BatchSize))
        {
            await using var command = Engine.IndexWriter.InsertChunks(batch, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ChunkSetRecord?> GetAsync(Guid chunkSetId, CancellationToken cancellationToken) =>
        First(await SetsAsync($"""SELECT {SetColumns} FROM content."ChunkSets" WHERE "Id" = $1""", cancellationToken, Db.P(chunkSetId)));

    public async Task<ChunkSetRecord?> GetCurrentAsync(Guid documentId, CancellationToken cancellationToken) =>
        First(await SetsAsync($"""
            SELECT {SetColumns} FROM content."ChunkSets"
            WHERE "Id" = (SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1)
            """, cancellationToken, Db.P(documentId)));

    public async Task SwapAsync(Guid chunkSetId, string state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var documentId = await ScalarAsync<Guid>("""SELECT "DocumentId" FROM content."ChunkSets" WHERE "Id" = $1""", connection, transaction, cancellationToken, Db.P(chunkSetId));
        await Engine.IndexWriter.SwapInAsync(connection, transaction, documentId, chunkSetId, state, pipelineVersionId: null, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetStateAsync(Guid chunkSetId, string state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Engine.IndexWriter.SetStateAsync(connection, transaction, chunkSetId, state, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdatePiiAsync(Guid chunkSetId, PiiCounts counts, bool flagged, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE content."ChunkSets" SET "PiiCounts" = $2::jsonb, "PiiFlagged" = $3 WHERE "Id" = $1
            """, connection)
        {
            Parameters = { Db.P(chunkSetId), Db.P(ContractJson.Serialize(counts)), Db.P(flagged) },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PiiClearance?> GetClearanceAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT "PiiClearedAt", "PiiClearedBy", "PiiClearedCounts"::text FROM content."Documents" WHERE "Id" = $1 AND "PiiClearedAt" IS NOT NULL
            """, connection)
        {
            Parameters = { Db.P(documentId) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PiiClearance(reader.GetFieldValue<DateTimeOffset>(0), Db.String(reader, 1) ?? string.Empty, Counts(Db.String(reader, 2)) ?? new PiiCounts());
    }

    public async Task SetClearanceAsync(Guid documentId, PiiClearance clearance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clearance);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE content."Documents" SET "PiiClearedAt" = $2, "PiiClearedBy" = $3, "PiiClearedCounts" = $4::jsonb WHERE "Id" = $1
            """, connection)
        {
            Parameters = { Db.P(documentId), Db.P(clearance.ClearedAt.ToUniversalTime()), Db.P(clearance.ClearedBy), Db.P(ContractJson.Serialize(clearance.Counts)) },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ChunkSetRecord>> PendingNerAsync(int limit, CancellationToken cancellationToken) =>
        SetsAsync($"""
            SELECT {SetColumns} FROM content."ChunkSets" s
            WHERE s."State" IN ('live', 'held') AND s."PiiCounts" ->> 'ner' = 'pending'
              AND EXISTS (SELECT 1 FROM content."Documents" d WHERE d."LiveChunkSetId" = s."Id")
            ORDER BY s."CreatedAt" LIMIT $1
            """, cancellationToken, Db.P(limit));

    public async Task<IReadOnlyList<ChunkRecord>> ChunksAsync(Guid chunkSetId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT "Id", "ChunkSetId", "DocumentId", "CollectionId", "Classification", "Ordinal", "HeadingPath", "Page", "PageEnd", "Text", "SearchText",
                   "OverlapChars", "TextHash", "TokenCount", "Tags", "DocumentType", "SourceModifiedAt", "EmbeddingProfileId", "Embedding"::real[],
                   "Kind", "DeletedAt"
            FROM content."Chunks" WHERE "ChunkSetId" = $1 ORDER BY "Ordinal"
            """, connection)
        {
            Parameters = { Db.P(chunkSetId) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var chunks = new List<ChunkRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var heading = Db.String(reader, 6) ?? string.Empty;
            var draft = new ChunkDraft
            {
                Ordinal = reader.GetInt32(5),
                Prefix = heading,
                HeadingPath = heading,
                Page = Db.Nullable<int>(reader, 7),
                PageEnd = Db.Nullable<int>(reader, 8),
                Text = reader.GetString(9),
                SearchText = reader.GetString(10),
                OverlapChars = reader.GetInt16(11),
                TextHash = Db.Hex(reader.GetFieldValue<byte[]>(12)),
                TokenCount = reader.GetInt32(13),
                Tags = reader.IsDBNull(14) ? [] : reader.GetFieldValue<string[]>(14),
                DocumentType = Db.String(reader, 15) ?? string.Empty,
                SourceModifiedAt = Db.Nullable<DateTimeOffset>(reader, 16) is { } modified ? DateOnly.FromDateTime(modified.UtcDateTime) : null,
            };
            chunks.Add(new ChunkRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), draft.Ordinal, draft,
                Db.Nullable<Guid>(reader, 17) ?? Guid.Empty, reader.IsDBNull(18) ? [] : reader.GetFieldValue<float[]>(18))
            {
                Kind = reader.GetString(19),
                DeletedAt = Db.Nullable<DateTimeOffset>(reader, 20),
            });
        }

        return chunks;
    }

    internal static PiiCounts? Counts(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return ContractJson.Deserialize<PiiCounts>(document.RootElement);
    }

    private async Task<IReadOnlyList<ChunkSetRecord>> SetsAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sets = new List<ChunkSetRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new ChunkSetKey(reader.GetGuid(1), reader.GetGuid(2), Db.Nullable<Guid>(reader, 3), reader.GetGuid(4), reader.GetFieldValue<byte[]>(5));
            sets.Add(new ChunkSetRecord(reader.GetGuid(0), key, reader.GetString(6), reader.GetGuid(7), reader.GetFieldValue<byte[]>(8))
            {
                StageKeys = Db.String(reader, 9),
                State = reader.GetString(10),
                PiiFlagged = reader.GetBoolean(11),
                PiiCounts = Counts(Db.String(reader, 12)),
                ChunkCount = reader.GetInt32(13),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
                LiveAt = Db.Nullable<DateTimeOffset>(reader, 15),
                SupersededAt = Db.Nullable<DateTimeOffset>(reader, 16),
            });
        }

        return sets;
    }

    private static async Task<T> ScalarAsync<T>(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync(cancellationToken) is T value ? value : throw new InvalidOperationException("Unknown chunk set.");
    }
}
