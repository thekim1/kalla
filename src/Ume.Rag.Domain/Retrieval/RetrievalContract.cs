namespace Ume.Rag.Domain.Retrieval;

/// <summary>Section expansion (knowledge design §9).</summary>
public enum ExpandMode { None, Section }

/// <summary>Who asks, for the action checked and for metrics.</summary>
public enum RetrievalPurpose { Chat, Http, Mcp, TestPanel }

/// <summary>
/// A retrieval (knowledge design §2.2, §3.1). <c>AssistantId</c> (chat, <c>rag-internal</c>) searches the published
/// version's collections (§4.2); <c>CollectionIds</c> narrows, each must be visible (404 otherwise); <c>RunId</c> is sent
/// to the gateway as <c>x-ume-run-id</c> (chat: the message id). <c>RerankCeiling</c> asks for the rerank call (knowledge design
/// §6.7) at that ceiling: chat passes the turn's ceiling when the assistant reranks; null (every other caller) keeps the fused order.
/// </summary>
public sealed record RetrievalRequest(
    string Query,
    Guid? AssistantId,
    IReadOnlyList<Guid>? CollectionIds,
    int TopK = 10,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    RetrievalFilters? Filters = null,
    ExpandMode Expand = ExpandMode.None,
    RetrievalPurpose Purpose = RetrievalPurpose.Chat,
    bool AllowDegraded = true,
    Guid? RunId = null,
    Residency? RerankCeiling = null);

/// <summary>Stage milliseconds, for metrics and the test panel (never in production response headers, §3.2).</summary>
public sealed record RetrievalTimings(double PolicyMs, double KeywordMs, double EmbeddingMs, double VectorMs, double HydrateMs, double TotalMs);

/// <summary>
/// A citation (knowledge design §10.1, JSON <c>document</c>). <c>SourceType</c>: <c>web</c> (<c>url</c>), <c>file</c>
/// (<c>upload</c>), <c>sharepoint</c> (P2). <c>DownloadUrl</c>: the grant link of a document without a public URL (§4.6),
/// null for <c>url</c> documents. <c>Modified</c>: the chunk's <c>SourceModifiedAt</c>, else the document's, else
/// <c>FetchedAt</c>.
/// </summary>
public sealed record Citation(
    Guid Id, string Title, string? DisplayName, string? Url, string? DownloadUrl, string SourceType, string? DocumentType, string? MediaType,
    string? Language, DateTimeOffset? Modified);

/// <summary>One result (knowledge design §2.2): the chunk text without its heading prefix, scores and citation.</summary>
public sealed record RetrievedChunk(
    int Rank, Guid ChunkId, Guid DocumentId, byte[] TextHash, string Text, string HeadingPath, int? Page, int? PageEnd, double Score,
    ChunkScores Scores, int? SectionRef, Guid CollectionId, string CollectionSlug, string Level, Citation Citation);

/// <summary>An expanded section (knowledge design §9); empty unless <see cref="ExpandMode.Section"/>.</summary>
public sealed record SectionText(
    int Id, Guid DocumentId, string HeadingPath, string Text, int TokenEstimate, int? PageStart, int? PageEnd, IReadOnlyList<Guid> ChunkIds, bool Truncated);

/// <summary>The outcome of a retrieval (knowledge design §2.2).</summary>
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Results,
    IReadOnlyList<SectionText> Sections,
    Coverage Coverage,
    string? Degraded,
    long ConfigVersion,
    RetrievalTimings Timings);

/// <summary>A document's citation plus its page count (<c>GET /v1/documents/{id}</c>, §3.4).</summary>
public sealed record DocumentInfo(Citation Citation, int? PageCount, Guid CollectionId, string CollectionSlug);

/// <summary>A collection the caller may search (<c>GET /v1/collections</c>, §3.3).</summary>
public sealed record CollectionInfo(Guid Id, string Slug, string Name, string Classification, string? Department, DateTimeOffset? PublishedAt);

/// <summary>Stable problem codes of the knowledge routes (knowledge design §3.6).</summary>
public static class KnowledgeCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string AssistantIdNotAllowed = "assistant_id_not_allowed";
    public const string CollectionNotFound = "collection_not_found";
    public const string NotFound = "not_found";
    public const string EmbeddingUnavailable = "embedding_unavailable";
    public const string SearchUnavailable = "search_unavailable";
    public const string ContentNotServed = "content_not_served";
    public const string ContentUnavailable = "content_unavailable";
    public const string GrantInvalid = "grant_invalid";
    public const string GrantExpired = "grant_expired";
}

/// <summary>
/// A refused retrieval (knowledge design §2.2): the HTTP routes answer it as a problem with <see cref="Code"/>; the chat
/// role translates it (404 → <c>model_not_found</c>).
/// </summary>
public sealed class RetrievalDeniedException(int status, string code, string? subject = null)
    : Exception($"Retrieval refused: {status} {code}.")
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    /// <summary>What the caller sent and may see echoed (an invisible slug, §4.4); never a hidden name.</summary>
    public string? Subject { get; } = subject;

    /// <summary>The real reason behind a 404 <c>not_found</c>, for event 5002 only; never sent to the caller (§4.4, §4.6).</summary>
    public string? Reason { get; init; }

    /// <summary>For events 5002/5005: the policy step that denied (<c>7</c> = ceiling), when the policy decided.</summary>
    public string? Step { get; init; }

    /// <summary>For events 5002/5005: <c>collection</c>, <c>assistant</c> or <c>document</c>, and its id.</summary>
    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    /// <summary>For events 5002/5005: the action asked for (<c>Search</c>, <c>Chat</c>, <c>ReadDocument</c>).</summary>
    public string? Action { get; init; }

    /// <summary>An explicit denial that events 5002/5005 record (a policy decision, an invisible requested resource, a grant re-check).</summary>
    public bool IsAccessDenial => Reason is not null && ResourceType is not null;
}
