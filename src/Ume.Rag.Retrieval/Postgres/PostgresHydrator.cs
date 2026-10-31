using Npgsql;
using NpgsqlTypes;

namespace Ume.Rag.Retrieval.Postgres;

/// <summary>What statement H+D reads (knowledge design §6.3).</summary>
/// <param name="HitIds">The final hits (≤ 50).</param>
/// <param name="DocumentIds">The hits' documents.</param>
/// <param name="AllowedLevels">The caller's levels.</param>
/// <param name="CollectionIds">The scope (re-checked for every hit).</param>
/// <param name="VisibleSourceIds">The sources the caller may see: those of the scope's collections (config snapshot).</param>
/// <param name="NeighbourRadius">±ordinals fetched around each hit (0 for <c>expand = none</c>; 8 for sections, §9.2).</param>
public sealed record HydrationRequest(
    IReadOnlyList<Guid> HitIds,
    IReadOnlyList<Guid> DocumentIds,
    IReadOnlyList<string> AllowedLevels,
    IReadOnlyList<Guid> CollectionIds,
    IReadOnlyList<Guid> VisibleSourceIds,
    int NeighbourRadius = 0);

/// <summary>A hit or one of its section neighbours (the hit itself has <see cref="ChunkId"/> = <see cref="HitId"/>).</summary>
public sealed record HydratedChunk(
    Guid HitId,
    Guid ChunkId,
    Guid ChunkSetId,
    int Ordinal,
    string? HeadingPath,
    string Text,
    short OverlapChars,
    int TokenCount,
    int? Page,
    int? PageEnd,
    DateTimeOffset? SourceModifiedAt,
    string? DocumentType);

public sealed record HydratedDocument(
    Guid Id,
    string? Title,
    string? Url,
    string IdentityKind,
    string? MediaType,
    string? Language,
    DateTimeOffset? SourceModifiedAt,
    DateTimeOffset? FetchedAt,
    int? PageCount,
    Guid SourceId,
    string? DisplayName,
    bool Visible);

/// <summary>
/// The rows of H+D. A hit missing from <see cref="Chunks"/> is stale (deleted, swapped out, out of scope or level) and
/// is dropped by the caller; a document missing or not <see cref="HydratedDocument.Visible"/> likewise.
/// </summary>
public sealed record Hydration(IReadOnlyList<HydratedChunk> Chunks, IReadOnlyList<HydratedDocument> Documents)
{
    public bool HasHit(Guid hitId) => Chunks.Any(c => c.HitId == hitId && c.ChunkId == hitId);
}

/// <summary>
/// Statement H+D (knowledge design §6.3) in one <see cref="NpgsqlBatch"/>, one round trip: the hits with their section
/// neighbours and the hits' documents, re-checking scope, levels, <c>DeletedAt</c> and the live set. Section assembly
/// and citations are the knowledge role's (§9, §10). Also reads the texts the reranker scores (§6.7).
/// </summary>
public sealed class PostgresHydrator(NpgsqlDataSource dataSource, PostgresSearchOptions? options = null) : IPassageTextReader
{
    private readonly PostgresSearchOptions _options = options ?? new PostgresSearchOptions();

    public async Task<Hydration> HydrateAsync(HydrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.NeighbourRadius);
        var chunks = new List<HydratedChunk>();
        var documents = new List<HydratedDocument>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.StatementTimeout);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(limit.Token);
            await using var batch = new NpgsqlBatch(connection)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand(RetrievalStatements.Hits)
                    {
                        Parameters =
                        {
                            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = request.HitIds.ToArray() },
                            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = request.AllowedLevels.ToArray() },
                            new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = request.NeighbourRadius },
                            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = request.CollectionIds.ToArray() },
                        },
                    },
                    new NpgsqlBatchCommand(RetrievalStatements.Documents)
                    {
                        Parameters =
                        {
                            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = request.DocumentIds.ToArray() },
                            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = request.AllowedLevels.ToArray() },
                            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = request.VisibleSourceIds.ToArray() },
                        },
                    },
                },
                Timeout = Math.Max(1, (int)Math.Ceiling(_options.StatementTimeout.TotalSeconds)) + 1,
            };
            await using var reader = await batch.ExecuteReaderAsync(limit.Token);
            while (await reader.ReadAsync(limit.Token))
            {
                chunks.Add(new HydratedChunk(
                    HitId: reader.GetGuid(0),
                    ChunkId: reader.GetGuid(1),
                    ChunkSetId: reader.GetGuid(11),
                    Ordinal: reader.GetInt32(2),
                    HeadingPath: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Text: reader.GetString(4),
                    OverlapChars: reader.GetInt16(5),
                    TokenCount: reader.GetInt32(6),
                    Page: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    PageEnd: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    SourceModifiedAt: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    DocumentType: reader.IsDBNull(10) ? null : reader.GetString(10)));
            }

            await reader.NextResultAsync(limit.Token);
            while (await reader.ReadAsync(limit.Token))
            {
                documents.Add(new HydratedDocument(
                    Id: reader.GetGuid(0),
                    Title: reader.IsDBNull(1) ? null : reader.GetString(1),
                    Url: reader.IsDBNull(2) ? null : reader.GetString(2),
                    IdentityKind: reader.GetString(3),
                    MediaType: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Language: reader.IsDBNull(5) ? null : reader.GetString(5),
                    SourceModifiedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    FetchedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                    PageCount: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    SourceId: reader.GetGuid(9),
                    DisplayName: reader.IsDBNull(10) ? null : reader.GetString(10),
                    Visible: reader.GetBoolean(11)));
            }
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && PostgresSearchIndex.IsTimeout(e))
        {
            throw new SearchUnavailableException("The hydrate statement exceeded its time limit.", e);
        }

        return new Hydration(chunks, documents);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> TextsAsync(SearchScope scope, IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var texts = new Dictionary<Guid, string>();
        var index = new PostgresSearchIndex(dataSource, _options);
        await index.RunAsync(RetrievalStatements.Texts,
        [
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = chunkIds.ToArray() },
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = scope.AllowedLevels.ToArray() },
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = scope.CollectionIds.ToArray() },
        ], async (reader, ct) =>
        {
            while (await reader.ReadAsync(ct))
            {
                texts[reader.GetGuid(0)] = reader.GetString(1);
            }
        }, _options.StatementTimeout, cancellationToken);
        return texts;
    }
}
