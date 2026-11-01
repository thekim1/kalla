using System.Collections.Concurrent;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// The search index of the <c>hybrid-no-decompounder</c> variant (weakness review §2, processing design §13.4): "without
/// the decompounder" on both sides, so statement K searches the <c>reindex-fts</c> scratch copy of the collection's
/// keyword side, the stored <c>SearchVector</c> recomputed from <c>SearchText</c> without compound parts (data-model
/// §3.2). Without the decompounder the chunker's <c>SearchText</c> is the chunk's <c>Text</c>, so the copy is the
/// generated column's own expression over <c>"Text"</c>; it is computed in the statement for the chunks the stored vector
/// matches, which are a superset (the parts are only appended to the text), so no table is written and nothing is left
/// behind. Ranking, limits and the candidate columns are statement K's (<see cref="RetrievalStatements.Keyword"/>);
/// statement V is <paramref name="vectors"/>' unchanged. The evaluator searches without filters (processing design §13.5).
/// </summary>
public sealed class NoDecompounderSearchIndex(NpgsqlDataSource dataSource, ISearchIndex vectors, TimeSpan statementTimeout) : ISearchIndex
{
    private static readonly ConcurrentDictionary<(Guid Profile, int Limit), string> Statements = new();

    public async Task<IReadOnlyList<Candidate>> KeywordAsync(
        SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(querySearchText);
        if (RetrievalStatements.FragmentsOf(filters) != FilterFragments.None)
        {
            throw new NotSupportedException("The evaluation's scratch keyword index searches without filters.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        var sql = Statements.GetOrAdd((scope.EmbeddingProfileId, limit), static key => Keyword(key.Profile, key.Limit));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(statementTimeout);
        var candidates = new List<Candidate>();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
#pragma warning disable CA2100 // Built from a constant and config literals (a Guid and an int); values are parameters.
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(statementTimeout.TotalSeconds)) + 1;
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = querySearchText });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = scope.CollectionIds.ToArray() });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = scope.AllowedLevels.ToArray() });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = scope.KeywordStopLexemes.ToArray() });
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            while (await reader.ReadAsync(timeout.Token))
            {
                candidates.Add(new Candidate(
                    ChunkId: reader.GetGuid(0),
                    DocumentId: reader.GetGuid(1),
                    ChunkSetId: reader.GetGuid(2),
                    Ordinal: reader.GetInt32(3),
                    TextHash: reader.GetFieldValue<byte[]>(4),
                    CollectionId: reader.GetGuid(5),
                    Level: reader.GetString(6),
                    DocumentType: reader.IsDBNull(7) ? null : reader.GetString(7),
                    SourceModifiedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                    IdentityKind: reader.GetString(10),
                    IdentityHash: reader.GetFieldValue<byte[]>(11),
                    SourceId: reader.GetGuid(12),
                    Url: reader.IsDBNull(13) ? null : reader.GetString(13),
                    KeywordScore: reader.GetDouble(9)));
            }
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is OperationCanceledException or NpgsqlException { InnerException: TimeoutException }
                                      or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
        {
            throw new SearchUnavailableException("The scratch keyword statement exceeded its time limit.", e);
        }

        return candidates;
    }

    public Task<IReadOnlyList<Candidate>> VectorAsync(
        SearchScope scope, ReadOnlyMemory<float> vector, RetrievalFilters? filters, int limit, VectorSearch search, CancellationToken cancellationToken) =>
        vectors.VectorAsync(scope, vector, filters, limit, search, cancellationToken);

    /// <summary>
    /// Statement K over the scratch copy: <c>scratch</c> is the chunks the stored vector matches (the partial GIN index),
    /// each with <c>setweight(heading path, A) || setweight(text, B)</c>, the generated column with <c>"Text"</c> for
    /// <c>"SearchText"</c> (migration <c>IngestionSchema</c>); then K's match, rank, order and limit on that vector. The
    /// query drops the scope's keyword stop list (<c>$4</c>) as K does.
    /// </summary>
    internal static string Keyword(Guid profileId, int limit) => $"""
        WITH q AS MATERIALIZED (
            SELECT content.rag_or_query(ts_delete(to_tsvector('swedish', $1), $4::text[])) AS tsq
        ), scratch AS MATERIALIZED (
            SELECT c."Id", c."DocumentId", c."ChunkSetId", c."Ordinal", c."TextHash", c."CollectionId", c."Classification",
                   c."DocumentType", c."SourceModifiedAt",
                   setweight(to_tsvector('swedish'::regconfig, coalesce(c."HeadingPath", '')), 'A') ||
                   setweight(to_tsvector('swedish'::regconfig, c."Text"), 'B') AS sv
            FROM content."Chunks" c, q
            WHERE c."SearchVector" @@ q.tsq
              AND c."Kind" = 'child' AND c."DeletedAt" IS NULL
              AND c."EmbeddingProfileId" = '{profileId.ToString("D", CultureInfo.InvariantCulture)}'::uuid
              AND c."CollectionId" = ANY ($2)
              AND c."Classification" = ANY ($3)
        ), kw AS MATERIALIZED (
            SELECT s."Id", s."DocumentId", s."ChunkSetId", s."Ordinal", s."TextHash", s."CollectionId", s."Classification",
                   s."DocumentType", s."SourceModifiedAt",
                   ts_rank_cd(s.sv, q.tsq)::double precision AS score
            FROM scratch s, q
            WHERE s.sv @@ q.tsq
            ORDER BY score DESC, s."Id"
            LIMIT {limit.ToString(CultureInfo.InvariantCulture)}
        )
        SELECT kw."Id", kw."DocumentId", kw."ChunkSetId", kw."Ordinal", kw."TextHash", kw."CollectionId", kw."Classification",
               kw."DocumentType", kw."SourceModifiedAt", kw.score, d."IdentityKind", d."IdentityHash", d."SourceId", d."Url"
        FROM kw JOIN content."Documents" d ON d."Id" = kw."DocumentId"
        """;
}
