using System.Text.Json.Serialization;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Api;

// The knowledge routes' JSON (knowledge design §3). Property names are snake_case through the api's naming policy;
// request types refuse unknown members, so a misspelt or rejected filter fails loudly (§3.1).

/// <summary><c>POST /v1/retrieve</c> (§3.1).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetrieveRequest(
    string? Query,
    IReadOnlyList<string>? Collections = null,
    int? TopK = null,
    string? Mode = null,
    RetrieveFilters? Filters = null,
    string? Expand = null,
    Guid? AssistantId = null);

/// <summary>Any-of tag and document-type filters and a modified-after date (§3.1).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetrieveFilters(IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? DocumentTypes = null, string? ModifiedAfter = null);

/// <summary>The 200 response of <c>POST /v1/retrieve</c> (§3.2).</summary>
public sealed record RetrieveResponse(
    IReadOnlyList<RetrieveResult> Results, IReadOnlyList<SectionResponse> Sections, CoverageResponse Coverage, string? Degraded, long ConfigVersion);

public sealed record RetrieveResult(
    int Rank, Guid ChunkId, string Text, string HeadingPath, int? Page, int? PageEnd, double Score, ScoresResponse Scores, int? SectionRef,
    string Collection, CitationResponse Document);

public sealed record ScoresResponse(double? Fused, double? VectorDistance, int? VectorRank, double? KeywordScore, int? KeywordRank);

/// <summary>A citation (§10.1).</summary>
public sealed record CitationResponse(
    Guid Id, string Title, string? DisplayName, string? Url, string? DownloadUrl, string SourceType, string? DocumentType, string? MediaType,
    DateTimeOffset? Modified, string? Language)
{
    public static CitationResponse From(Citation c, string? downloadUrl = null) =>
        new(c.Id, c.Title, c.DisplayName, c.Url, downloadUrl ?? c.DownloadUrl, c.SourceType, c.DocumentType, c.MediaType, c.Modified, c.Language);
}

public sealed record SectionResponse(
    int Id, Guid DocumentId, string HeadingPath, string Text, int TokenEstimate, int? PageStart, int? PageEnd, IReadOnlyList<Guid> ChunkIds, bool Truncated);

public sealed record CoverageResponse(string Status, double? BestDistance, double? Threshold, string? Collection, double? BestKeywordScore);

/// <summary><c>GET /v1/collections</c> (§3.3).</summary>
public sealed record CollectionsResponse(IReadOnlyList<CollectionResponse> Data);

public sealed record CollectionResponse(string Slug, string Name, string Classification, string? Department, DateTimeOffset? PublishedAt);

/// <summary><c>GET /v1/documents/{id}</c> (§3.4): the citation plus the page count.</summary>
public sealed record DocumentResponse(
    Guid Id, string Title, string? DisplayName, string? Url, string? DownloadUrl, string SourceType, string? DocumentType, string? MediaType,
    DateTimeOffset? Modified, string? Language, int? PageCount, string Collection);
