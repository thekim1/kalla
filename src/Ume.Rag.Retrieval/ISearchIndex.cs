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
    /// <summary>
    /// The identity group (§8): documents of different sources with the same identity are one group. A value over the
    /// candidate's own fields, built on access without allocating (#333: the hex strings cost ≈ 1 KB per candidate).
    /// </summary>
    public IdentityGroupKey IdentityKey => new(IdentityKind, new HashBytes(IdentityHash));

    /// <summary>The <c>(DocumentId, TextHash)</c> dedup key (§8), as <see cref="IdentityKey"/> a value built on access.</summary>
    public TextKey TextKey => new(DocumentId, new HashBytes(TextHash));
}

/// <summary>A candidate's identity group: identity kind and identity hash (§8).</summary>
public readonly record struct IdentityGroupKey(string Kind, HashBytes Hash);

/// <summary>A candidate's dedup key: document and text hash (§8).</summary>
public readonly record struct TextKey(Guid DocumentId, HashBytes Hash);

/// <summary>A hash compared by its bytes (the array itself is never copied or changed).</summary>
public readonly struct HashBytes(byte[] bytes) : IEquatable<HashBytes>
{
    private readonly byte[] _bytes = bytes ?? [];

    public bool Equals(HashBytes other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is HashBytes other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(_bytes);

    public static bool operator ==(HashBytes left, HashBytes right) => left.Equals(right);

    public static bool operator !=(HashBytes left, HashBytes right) => !left.Equals(right);
}

/// <summary>
/// Chunk counts per collection of a profile (for the exact/HNSW rule, knowledge design §6.4) and the keyword statistics
/// behind the keyword stop list (§6.2, §20).
/// </summary>
public interface IScopeSizeSource
{
    /// <summary>
    /// Searchable child chunks per (profile, collection, tag set): a collection's rows add up to its count, and the rows
    /// whose tags meet a link's topics to that link's count (§6.4; Decisions "M-2 open design calls").
    /// </summary>
    Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The rows of <c>content."KeywordStatistics"</c>, which the worker keeps (A-29): per (profile, collection) of at least
    /// <see cref="ScopeSizeCache.KeywordStatisticsMinChunks"/> live child chunks, the lexemes in at least
    /// <see cref="ScopeSizeCache.KeywordStatisticsShare"/> of them. rag-api runs no <c>ts_stat</c> (#499 step 2, B-33).
    /// </summary>
    Task<IReadOnlyList<FrequentLexeme>> KeywordStatisticsAsync(CancellationToken cancellationToken);
}

/// <summary>The searchable child chunks of one (profile, collection) that carry exactly <see cref="Tags"/> (empty = untagged).</summary>
public sealed record CollectionChunkCount(Guid EmbeddingProfileId, Guid CollectionId, long Chunks)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// A lexeme of one (profile, collection): <see cref="Ndoc"/> of its <see cref="ChunkCount"/> live child chunks contain it,
/// as the worker counted them (both from one <c>KeywordStatistics</c> row).
/// </summary>
public sealed record FrequentLexeme(Guid EmbeddingProfileId, Guid CollectionId, string Lexeme, long Ndoc, long ChunkCount);
