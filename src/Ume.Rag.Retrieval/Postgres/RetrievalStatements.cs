using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Retrieval.Postgres;

/// <summary>Which optional filter fragments a statement carries (knowledge design §6.2: appended only when present).</summary>
[Flags]
public enum FilterFragments
{
    None = 0,
    Tags = 1,
    DocumentTypes = 2,
    ModifiedAfter = 4,
    All = Tags | DocumentTypes | ModifiedAfter,
}

/// <summary>
/// The texts of statements K and V (knowledge design §6.2; data-model §3.3), one per variant: K or V × exact or
/// approximate × 2³ filter combinations, per profile and candidate depth. Parameters: <c>$1</c> the query search text (K) or the query
/// vector (V), <c>$2 uuid[]</c> the scope, <c>$3 text[]</c> the allowed levels, then only the filters present (from <c>$4</c>) in the
/// order tags, document types, modified after. The profile id, the dimensions and the candidate depth (<c>LIMIT</c>) are inlined literals from config (a
/// <see cref="Guid"/> and an <see cref="int"/>, never user input), so a generic plan still matches the per-profile
/// partial indexes; the vector is always a parameter. Texts are cached per variant, so each distinct text is one
/// auto-prepared statement per connection.
/// </summary>
public static class RetrievalStatements
{
    private static readonly ConcurrentDictionary<(char Kind, Guid Profile, int Dimensions, int Limit, FilterFragments Filters), string> Cache = new();

    public static FilterFragments FragmentsOf(RetrievalFilters? filters) =>
        (filters?.HasTags == true ? FilterFragments.Tags : 0)
        | (filters?.HasDocumentTypes == true ? FilterFragments.DocumentTypes : 0)
        | (filters?.HasModifiedAfter == true ? FilterFragments.ModifiedAfter : 0);

    /// <summary>
    /// K: chunks matching any lexeme of the query search text (<c>content.rag_or_query</c>, OR semantics), ranked by
    /// <c>ts_rank_cd</c>, on the partial GIN index (<c>"Kind" = 'child' AND "DeletedAt" IS NULL</c>). The query CTE is
    /// <c>MATERIALIZED</c>: inlined, a generic plan (auto-prepared statements) evaluates
    /// <c>rag_or_query(to_tsvector($1))</c> once per candidate row in the filter and in <c>ts_rank_cd</c>, which more
    /// than doubled K at 100k chunks (to-verify #5); materialised it runs once and feeds the GIN index scan.
    /// </summary>
    public static string Keyword(Guid profileId, int limit, FilterFragments filters) =>
        Cache.GetOrAdd(('K', profileId, 0, limit, filters), static key => $"""
            WITH q AS MATERIALIZED (
                SELECT content.rag_or_query(to_tsvector('swedish', $1)) AS tsq
            ), kw AS MATERIALIZED (
                SELECT c."Id", c."DocumentId", c."ChunkSetId", c."Ordinal", c."TextHash", c."CollectionId", c."Classification",
                       c."DocumentType", c."SourceModifiedAt",
                       ts_rank_cd(c."SearchVector", q.tsq)::double precision AS score
                FROM content."Chunks" c, q
                WHERE c."SearchVector" @@ q.tsq
                  AND c."Kind" = 'child' AND c."DeletedAt" IS NULL
                  AND c."EmbeddingProfileId" = {ProfileLiteral(key.Profile)}
                  AND c."CollectionId" = ANY ($2)
                  AND c."Classification" = ANY ($3){Filters(key.Filters)}
                ORDER BY score DESC, c."Id"
                LIMIT {Limit(key.Limit)}
            )
            SELECT kw."Id", kw."DocumentId", kw."ChunkSetId", kw."Ordinal", kw."TextHash", kw."CollectionId", kw."Classification",
                   kw."DocumentType", kw."SourceModifiedAt", kw.score, d."IdentityKind", d."IdentityHash", d."SourceId", d."Url"
            FROM kw JOIN content."Documents" d ON d."Id" = kw."DocumentId"
            """);

    /// <summary>
    /// V, approximate: the profile's partial HNSW index on <c>("Embedding"::halfvec(N))</c> (data-model §3.2); the
    /// distance is the half-precision one the index orders by. HNSW settings are role defaults (§6.4), no <c>SET</c>.
    /// </summary>
    public static string VectorApproximate(Guid profileId, int dimensions, int limit, FilterFragments filters) =>
        Cache.GetOrAdd(('A', profileId, dimensions, limit, filters), static key =>
        {
            var distance = $"""c."Embedding"::halfvec({Dims(key.Dimensions)}) <=> $1::halfvec({Dims(key.Dimensions)})""";
            return Vector(key.Profile, key.Limit, key.Filters, distance, distance);
        });

    /// <summary>
    /// V, exact: full-precision distance, ordered by <c>distance + 0</c>, which is no index ordering, so the plan is the
    /// <c>IX_Chunks_CollectionId</c> B-tree and a top-N sort (knowledge design §6.4). The HNSW index is on the
    /// half-precision expression and cannot serve the <c>vector</c> operator either.
    /// </summary>
    public static string VectorExact(Guid profileId, int dimensions, int limit, FilterFragments filters) =>
        Cache.GetOrAdd(('E', profileId, dimensions, limit, filters), static key =>
            Vector(key.Profile, key.Limit, key.Filters, $"""(c."Embedding"::vector({Dims(key.Dimensions)}) <=> $1) + 0""", "distance"));

    /// <summary>
    /// Searchable child chunks per (profile, collection), for the exact/HNSW rule (§6.4; grouped by profile as well, so
    /// a scope's share of its profile's index is known).
    /// </summary>
    public const string CountChunks = """
        SELECT "EmbeddingProfileId", "CollectionId", count(*)
        FROM content."Chunks"
        WHERE "DeletedAt" IS NULL AND "Kind" = 'child' AND "EmbeddingProfileId" IS NOT NULL
        GROUP BY 1, 2
        """;

    /// <summary>
    /// H: the hits and their section neighbours (knowledge design §6.3). <c>$1</c> hit ids, <c>$2</c> allowed levels,
    /// <c>$3</c> the neighbour radius (0 for <c>expand = none</c>: the hit row only), <c>$4</c> the scope. Re-checks
    /// scope, levels and <c>DeletedAt</c>, and that the hit's set is still its document's live set (a candidate whose set
    /// was swapped out between V and H drops out). Neighbours come only from that live set and with <c>DeletedAt</c> null,
    /// so a section never brings back a held, superseded or soft-deleted chunk; their level is not re-checked because every
    /// chunk of a set carries its document's level (the chunk writer, <c>ContentJobHandlers</c>).
    /// </summary>
    public const string Hits = """
        WITH hit AS (
            SELECT h."Id", h."ChunkSetId", h."Ordinal", h."HeadingPath"
            FROM content."Chunks" h
            JOIN content."Documents" hd ON hd."Id" = h."DocumentId"
                                       AND hd."LiveChunkSetId" = h."ChunkSetId"
            WHERE h."Id" = ANY ($1) AND h."Classification" = ANY ($2)
              AND h."DeletedAt" IS NULL AND h."CollectionId" = ANY ($4)
        )
        SELECT hit."Id" AS "HitId", n."Id", n."Ordinal", n."HeadingPath", n."Text", n."OverlapChars", n."TokenCount",
               n."Page", n."PageEnd", n."SourceModifiedAt", n."DocumentType", n."ChunkSetId"
        FROM hit
        JOIN LATERAL (
            SELECT n.* FROM content."Chunks" n
            WHERE n."ChunkSetId" = hit."ChunkSetId"
              AND n."Ordinal" BETWEEN hit."Ordinal" - $3 AND hit."Ordinal" + $3
              AND n."DeletedAt" IS NULL
              AND (n."HeadingPath" IS NOT DISTINCT FROM hit."HeadingPath"
                   OR starts_with(n."HeadingPath", hit."HeadingPath" || ' › '))
        ) n ON true
        ORDER BY hit."Id", n."Ordinal"
        """;

    /// <summary>
    /// D: the hits' documents (knowledge design §6.3). <c>$1</c> document ids, <c>$2</c> allowed levels, <c>$3</c> the
    /// ids of the sources the caller may see; <c>Visible</c> needs one of them and a live chunk set.
    /// </summary>
    public const string Documents = """
        SELECT d."Id", d."Title", d."Url", d."IdentityKind", d."MediaType", d."Language", d."SourceModifiedAt",
               d."FetchedAt", d."PageCount", d."SourceId", d."DisplayName",
               d."SourceId" = ANY ($3)
                 AND EXISTS (SELECT 1 FROM content."ChunkSets" cs
                             WHERE cs."Id" = d."LiveChunkSetId" AND cs."State" = 'live') AS "Visible"
        FROM content."Documents" d
        WHERE d."Id" = ANY ($1) AND d."Classification" = ANY ($2) AND d."DeletedAt" IS NULL
        """;

    /// <summary>The texts the reranker scores (§6.7): searchable chunks in the scope at an allowed level.</summary>
    public const string Texts = """
        SELECT c."Id", c."Text"
        FROM content."Chunks" c
        WHERE c."Id" = ANY ($1) AND c."Classification" = ANY ($2) AND c."CollectionId" = ANY ($3) AND c."DeletedAt" IS NULL
        """;

    private static string Vector(Guid profile, int limit, FilterFragments filters, string distance, string orderBy) => $"""
        WITH vec AS MATERIALIZED (
            SELECT c."Id", c."DocumentId", c."ChunkSetId", c."Ordinal", c."TextHash", c."CollectionId", c."Classification",
                   c."DocumentType", c."SourceModifiedAt",
                   {distance} AS distance
            FROM content."Chunks" c
            WHERE c."EmbeddingProfileId" = {ProfileLiteral(profile)}
              AND c."DeletedAt" IS NULL
              AND c."CollectionId" = ANY ($2)
              AND c."Classification" = ANY ($3){Filters(filters)}
            ORDER BY {orderBy}
            LIMIT {Limit(limit)}
        )
        SELECT vec."Id", vec."DocumentId", vec."ChunkSetId", vec."Ordinal", vec."TextHash", vec."CollectionId", vec."Classification",
               vec."DocumentType", vec."SourceModifiedAt", vec.distance, d."IdentityKind", d."IdentityHash", d."SourceId", d."Url"
        FROM vec JOIN content."Documents" d ON d."Id" = vec."DocumentId"
        """;

    /// <summary>The optional predicates, numbered from <c>$4</c> in the order tags, document types, modified after.</summary>
    private static string Filters(FilterFragments filters)
    {
        var sql = new StringBuilder();
        var next = 4;
        if (filters.HasFlag(FilterFragments.Tags))
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n      AND c.\"Tags\" && ${next++}");
        }

        if (filters.HasFlag(FilterFragments.DocumentTypes))
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n      AND c.\"DocumentType\" = ANY (${next++})");
        }

        if (filters.HasFlag(FilterFragments.ModifiedAfter))
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n      AND c.\"SourceModifiedAt\" >= ${next}");
        }

        return sql.ToString();
    }

    private static string Limit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        return limit.ToString(CultureInfo.InvariantCulture);
    }

    private static string ProfileLiteral(Guid profileId) => "'" + profileId.ToString("D", CultureInfo.InvariantCulture) + "'::uuid";

    private static string Dims(int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4000);
        return dimensions.ToString(CultureInfo.InvariantCulture);
    }
}
