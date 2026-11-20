namespace Kalla.Domain.Retrieval;

// Request and result records of retrieval shared by the search core (Kalla.Retrieval), the knowledge role and the
// evaluator (knowledge design §2.2).

/// <summary>Which candidate lists a search uses (knowledge design §6.5).</summary>
public enum RetrievalMode
{
    Hybrid,
    Vector,
    Keyword,
}

/// <summary>
/// Optional filters (knowledge design §6.2): each one is appended to the statements only when present; an empty list
/// counts as absent.
/// </summary>
public sealed record RetrievalFilters(
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? DocumentTypes = null,
    DateTimeOffset? ModifiedAfter = null)
{
    public bool HasTags => Tags is { Count: > 0 };

    public bool HasDocumentTypes => DocumentTypes is { Count: > 0 };

    public bool HasModifiedAfter => ModifiedAfter is not null;
}

/// <summary>The coverage outcome of a search (knowledge design §7).</summary>
public enum CoverageStatus
{
    Ok,
    Low,
    Unknown,
}

/// <summary>
/// The coverage signal: the status, the best cosine distance with its threshold and collection, and the best keyword
/// score (knowledge design §2.2, §7).
/// </summary>
public sealed record Coverage(
    CoverageStatus Status,
    double? BestDistance,
    double? Threshold,
    Guid? CollectionId,
    double? BestKeywordScore);

/// <summary>A result's scores per list (knowledge design §2.2): fused RRF score, vector and keyword side, rerank (P2).</summary>
public sealed record ChunkScores(
    double? Fused,
    double? VectorDistance,
    int? VectorRank,
    double? KeywordScore,
    int? KeywordRank,
    double? RerankScore);

/// <summary>The <c>degraded</c> values of a retrieval result (knowledge design §2.2, §5.5, §6.7).</summary>
public static class RetrievalDegradation
{
    public const string EmbeddingUnavailable = "embedding_unavailable";
    public const string EmbeddingResidency = "embedding_residency";
    public const string RerankUnavailable = "rerank_unavailable";
}
