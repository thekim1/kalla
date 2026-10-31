using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Retrieval;

/// <summary>
/// Candidate generation (knowledge design §13, the OpenSearch seam): keyword and vector candidates as ids and numbers,
/// never chunk text. <see cref="Postgres.PostgresSearchIndex"/> is the only implementation; fusion, dedup, diversity and
/// coverage stay in <see cref="RetrievalEngine"/>.
/// </summary>
public interface ISearchIndex
{
    /// <summary>Statement K: up to <paramref name="limit"/> chunks matching any lexeme of the query search text.</summary>
    Task<IReadOnlyList<Candidate>> KeywordAsync(
        SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken);

    /// <summary>Statement V: the <paramref name="limit"/> nearest chunks by cosine distance, exactly or through HNSW.</summary>
    Task<IReadOnlyList<Candidate>> VectorAsync(
        SearchScope scope, ReadOnlyMemory<float> vector, RetrievalFilters? filters, int limit, VectorSearch search,
        CancellationToken cancellationToken);
}

/// <summary>How statement V searches (knowledge design §6.4).</summary>
public enum VectorSearch
{
    /// <summary>B-tree on <c>CollectionId</c> and a top-N sort over full-precision distances.</summary>
    Exact,

    /// <summary>The profile's partial HNSW index on the half-precision expression, relaxed-order iterative scan.</summary>
    Approximate,
}

/// <summary>
/// One candidate chunk (knowledge design §13): ids and numbers for fusion, dedup, diversity and coverage, plus the
/// document's <c>SourceId</c>, <c>Url</c>, <c>DocumentType</c> and <c>SourceModifiedAt</c> for the age-decay and
/// <c>demote</c> multipliers (§6.5).
/// </summary>
public sealed record Candidate(
    Guid ChunkId,
    Guid DocumentId,
    Guid ChunkSetId,
    int Ordinal,
    byte[] TextHash,
    Guid CollectionId,
    string Level,
    string? DocumentType,
    DateTimeOffset? SourceModifiedAt,
    string IdentityKind,
    byte[] IdentityHash,
    Guid SourceId,
    string? Url,
    double? Distance = null,
    double? KeywordScore = null)
{
    /// <summary>The identity group (§8): documents of different sources with the same identity are one group.</summary>
    public string IdentityKey { get; } = IdentityKind + ":" + Convert.ToHexString(IdentityHash);

    /// <summary>The <c>(DocumentId, TextHash)</c> dedup key (§8).</summary>
    public string TextKey { get; } = DocumentId.ToString("N") + ":" + Convert.ToHexString(TextHash);
}

/// <summary>Chunk counts per collection of a profile (for the exact/HNSW rule, knowledge design §6.4).</summary>
public interface IScopeSizeSource
{
    /// <summary>Searchable child chunks per (profile, collection).</summary>
    Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken);
}

public sealed record CollectionChunkCount(Guid EmbeddingProfileId, Guid CollectionId, long Chunks);
