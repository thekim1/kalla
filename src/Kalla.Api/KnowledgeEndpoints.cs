using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Kalla.Answering;
using Kalla.Domain.Entities;
using Kalla.Domain.Retrieval;
using Kalla.Infrastructure.Access;
using Kalla.Infrastructure.Security;
using Kalla.Retrieval;

namespace Kalla.Api;

/// <summary>
/// The knowledge role's HTTP surface (knowledge design §2.3, §3): <c>POST /v1/retrieve</c>, <c>GET /v1/collections</c>,
/// <c>GET /v1/documents/{id}</c> and <c>GET /v1/documents/{id}/content</c>, over <see cref="KnowledgeService"/>.
/// </summary>
public static partial class KnowledgeEndpoints
{
    public const string DownloadsPolicy = "knowledge-downloads";

    private static readonly HashSet<string> DocumentTypes = new(StringComparer.Ordinal)
    {
        "page", "pdf", "word", "presentation", "spreadsheet", "text", "email", "image",
    };

    public static IEndpointRouteBuilder MapKnowledge(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var v1 = app.MapGroup("/v1").WithTags("Knowledge").AddEndpointFilter<KnowledgeProblems>();
        v1.MapPost("/retrieve", RetrieveAsync)
            .WithSummary("Search the collections the caller may search")
            .Accepts<RetrieveRequest>("application/json")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        v1.MapGet("/collections", CollectionsAsync).WithSummary("The collections the caller may search")
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);
        v1.MapGet("/documents/{id}", DocumentAsync).WithSummary("A document's citation and page count (key or download grant)")
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        v1.MapGet("/documents/{id}/content", ContentAsync).WithSummary("The stored original of an uploaded document (key or download grant)")
            .Produces(200, contentType: "application/octet-stream")
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503)
            .RequireRateLimiting(DownloadsPolicy);
        return app;
    }

    private static async Task<Ok<RetrieveResponse>> RetrieveAsync(HttpContext http, KnowledgeCallers callers, KnowledgeService knowledge,
        IPolicySnapshotSource snapshots, IOptions<JsonOptions> json, TimeProvider time, CancellationToken ct)
    {
        var who = await callers.AuthenticateAsync(http.Request, ct);
        RetrieveRequest? body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<RetrieveRequest>(json.Value.SerializerOptions, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw Invalid("body", "The body must be a JSON object with the documented members only.");
        }

        var snapshot = snapshots.Current;
        var request = ToRequest(body ?? throw Invalid("body", "A body is required."), who.Client.Kind, snapshot, time.GetUtcNow());
        RetrievalResult result;
        try
        {
            result = await knowledge.RetrieveAsync(who, request, ct);
        }
        catch (RetrievalDeniedException denied) when (denied.Code == KnowledgeCodes.CollectionNotFound && Guid.TryParse(denied.Subject, out var id))
        {
            // Echo the slug the caller sent, never a name it may not see (§4.4).
            throw new RetrievalDeniedException(404, KnowledgeCodes.CollectionNotFound,
                body.Collections?.FirstOrDefault(s => snapshot.CollectionsBySlug.TryGetValue(s, out var c) && c.Id == id) ?? id.ToString())
            {
                Reason = denied.Reason, Step = denied.Step, ResourceType = denied.ResourceType, ResourceId = denied.ResourceId, Action = denied.Action,
            };
        }

        var slugs = snapshot.Collections.ToDictionary(c => c.Key, c => c.Value.Slug);
        return TypedResults.Ok(new RetrieveResponse(
            [.. result.Results.Select(r => new RetrieveResult(r.Rank, r.ChunkId, r.Text, r.HeadingPath, r.Page, r.PageEnd, r.Score,
                new ScoresResponse(r.Scores.Fused, r.Scores.VectorDistance, r.Scores.VectorRank, r.Scores.KeywordScore, r.Scores.KeywordRank),
                r.SectionRef, r.CollectionSlug, CitationResponse.From(r.Citation)))],
            [.. result.Sections.Select(s => new SectionResponse(s.Id, s.DocumentId, s.HeadingPath, s.Text, s.TokenEstimate, s.PageStart, s.PageEnd,
                s.ChunkIds, s.Truncated))],
            new CoverageResponse(result.Coverage.Status.ToString().ToLowerInvariant(), result.Coverage.BestDistance, result.Coverage.Threshold,
                result.Coverage.CollectionId is { } cid ? slugs.GetValueOrDefault(cid) : null, result.Coverage.BestKeywordScore),
            result.Degraded, result.ConfigVersion));
    }

    private static async Task<Ok<CollectionsResponse>> CollectionsAsync(HttpContext http, KnowledgeCallers callers, KnowledgeService knowledge, CancellationToken ct)
    {
        var who = await callers.AuthenticateAsync(http.Request, ct);
        var list = await knowledge.ListCollectionsAsync(who, ct);
        return TypedResults.Ok(new CollectionsResponse([.. list.Select(c => new CollectionResponse(c.Slug, c.Name, c.Classification, c.Department, c.PublishedAt))]));
    }

    private static async Task<Ok<DocumentResponse>> DocumentAsync(string id, HttpContext http, KnowledgeCallers callers, KnowledgeService knowledge,
        DownloadGrants grants, CancellationToken ct)
    {
        var documentId = ParseId(id);
        DocumentInfo info;
        string? link = null;
        if (GrantOf(http) is { } token)
        {
            info = await knowledge.GetDocumentAsync(ReadGrant(grants, token, http), documentId, ct);
            link = grants.Link(documentId, token);
        }
        else
        {
            info = await knowledge.GetDocumentAsync(await callers.AuthenticateAsync(http.Request, ct), documentId, ct);
        }

        var c = CitationResponse.From(info.Citation, link);
        return TypedResults.Ok(new DocumentResponse(c.Id, c.Title, c.DisplayName, c.Url, c.DownloadUrl, c.SourceType, c.DocumentType, c.MediaType,
            c.Modified, c.Checked, c.Language, info.PageCount, info.CollectionSlug));
    }

    private static async Task<IResult> ContentAsync(string id, HttpContext http, KnowledgeCallers callers, KnowledgeService knowledge,
        DownloadGrants grants, IDocumentContentReader reader, KnowledgeSecurity security, ILoggerFactory loggers, CancellationToken ct)
    {
        var documentId = ParseId(id);
        DocumentContent content;
        if (GrantOf(http) is { } token)
        {
            DownloadGrant grant;
            try
            {
                grant = ReadGrant(grants, token, http);
            }
            catch (KnowledgeFaultException fault) when (fault.Status == 401 && AcceptsHtml(http))
            {
                // A browser following an old citation link gets a page, not JSON (§4.6).
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
                http.Response.Headers.CacheControl = "no-store";
                return Results.Content(ContentHeaders.ExpiredPage, "text/html; charset=utf-8", statusCode: 401);
            }

            content = await Counted(security, "grant", documentId, grant.ClientId, () => knowledge.FindContentAsync(grant, documentId, ct));
        }
        else
        {
            var who = await callers.AuthenticateAsync(http.Request, ct);
            content = await Counted(security, "key", documentId, who.Client.Id, () => knowledge.FindContentAsync(who, documentId, ct));
        }

        var kind = GrantOf(http) is null ? "key" : "grant";
        var clientId = (http.Items[KnowledgeCallers.PrincipalItem] as Domain.Access.RagPrincipal)?.Client.Id
            ?? (http.Items[KnowledgeCallers.GrantItem] as DownloadGrant)?.ClientId;
        DocumentBytes? bytes;
        try
        {
            bytes = await reader.OpenAsync(content, ct);
        }
        catch (RetrievalDeniedException)
        {
            security.Download("unavailable", kind, documentId, clientId);
            throw;
        }

        if (bytes is null)
        {
            security.Download("missing", kind, documentId, clientId);
            LogBlobMissing(loggers.CreateLogger(typeof(KnowledgeEndpoints)), documentId);
            throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound);
        }

        foreach (var (name, value) in ContentHeaders.Fixed(content))
        {
            http.Response.Headers[name] = value;
        }

        http.Response.Headers.ContentDisposition = ContentHeaders.ContentDisposition(content);
        http.Response.ContentLength = bytes.Length;
        // Counted as it ends: ok when fully sent, integrity when a damaged segment aborts the response (§3.5).
        var body = security.CountDownload(bytes.Content, bytes.Length, kind, documentId, clientId);
        return Results.Stream(body, ContentHeaders.ContentType(content.MediaType), enableRangeProcessing: false);
    }

    /// <summary>A refused download is counted (<c>not_served</c> for a web page, else <c>not_found</c>) before the problem is answered.</summary>
    private static async Task<DocumentContent> Counted(KnowledgeSecurity security, string kind, Guid documentId, Guid clientId, Func<Task<DocumentContent>> find)
    {
        try
        {
            return await find();
        }
        catch (RetrievalDeniedException denied) when (denied.Status == 404)
        {
            security.Download(denied.Code == KnowledgeCodes.ContentNotServed ? "not_served" : "not_found", kind, documentId, clientId);
            throw;
        }
    }

    /// <summary>§3.1 validation; collections by slug (an unknown slug is 404 <c>collection_not_found</c>, as an invisible one).</summary>
    internal static RetrievalRequest ToRequest(RetrieveRequest body, string clientKind, Domain.Access.PolicySnapshot snapshot, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        string query = body.Query ?? "";
        try
        {
            query = QueryNormalizer.NormalizeAndValidate(query);
            if (!query.Any(char.IsLetterOrDigit))
            {
                errors["query"] = ["The query needs at least one letter or digit."];
            }
        }
        catch (ArgumentException ex)
        {
            errors["query"] = [ex.Message];
        }

        if (body.Collections is { } slugs && (slugs.Count is < 1 or > KnowledgeService.MaxCollections || slugs.Distinct(StringComparer.Ordinal).Count() != slugs.Count ||
            slugs.Any(s => s is null || s.Length is < 1 or > 100)))
        {
            errors["collections"] = ["1–20 distinct collection slugs, each at most 100 characters."];
        }

        if (body.TopK is < 1 or > SearchRequest.MaxTopK)
        {
            errors["top_k"] = [$"top_k is 1–{SearchRequest.MaxTopK}."];
        }

        var mode = body.Mode switch
        {
            null or "hybrid" => RetrievalMode.Hybrid,
            "vector" => RetrievalMode.Vector,
            "keyword" => RetrievalMode.Keyword,
            _ => (RetrievalMode?)null,
        };
        if (mode is null)
        {
            errors["mode"] = ["mode is hybrid, vector or keyword."];
        }

        var expand = body.Expand switch
        {
            null or "none" => ExpandMode.None,
            "section" => ExpandMode.Section,
            _ => (ExpandMode?)null,
        };
        if (expand is null)
        {
            errors["expand"] = [body.Expand == "parent" ? "expand: parent is now expand: section." : "expand is none or section."];
        }

        var filters = Filters(body.Filters, now, errors);
        if (errors.Count > 0)
        {
            throw new KnowledgeFaultException(400, KnowledgeCodes.InvalidRequest, "The request is not valid.") { Errors = errors };
        }

        if (body.AssistantId is not null && clientKind != ClientKinds.Internal)
        {
            throw new RetrievalDeniedException(400, KnowledgeCodes.AssistantIdNotAllowed);
        }

        List<Guid>? ids = null;
        if (body.Collections is { } requested)
        {
            ids = [];
            foreach (var slug in requested)
            {
                ids.Add(snapshot.CollectionsBySlug.TryGetValue(slug, out var collection)
                    ? collection.Id
                    : throw new RetrievalDeniedException(404, KnowledgeCodes.CollectionNotFound, slug)
                    {
                        Reason = "collection_unknown", ResourceType = "collection", ResourceId = SecurityEvents.Printable(slug), Action = "Search",
                    });
            }
        }

        return new RetrievalRequest(query, body.AssistantId, ids, body.TopK ?? 10, mode!.Value, filters, expand!.Value, RetrievalPurpose.Http);
    }

    private static RetrievalFilters? Filters(RetrieveFilters? filters, DateTimeOffset now, Dictionary<string, string[]> errors)
    {
        if (filters is null)
        {
            return null;
        }

        if (filters.Tags is { } tags && (tags.Count > 20 || tags.Any(t => t is null || t.Length is < 1 or > 100)))
        {
            errors["filters.tags"] = ["At most 20 tags, each 1–100 characters."];
        }

        if (filters.DocumentTypes is { } types && types.Any(t => t is null || !DocumentTypes.Contains(t)))
        {
            errors["filters.document_types"] = [$"Document types are {string.Join(", ", DocumentTypes)}."];
        }

        DateTimeOffset? after = null;
        if (filters.ModifiedAfter is { } text)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) &&
                parsed >= new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero) && parsed <= now.AddDays(1))
            {
                after = parsed;
            }
            else
            {
                errors["filters.modified_after"] = ["An ISO 8601 date or date-time from 1990-01-01 to tomorrow."];
            }
        }

        return new RetrievalFilters(filters.Tags, filters.DocumentTypes, after);
    }

    /// <summary>Malformed ids are 404 <c>not_found</c>, so the format reveals nothing (§3.4).</summary>
    private static Guid ParseId(string id) =>
        Guid.TryParse(id, out var parsed) ? parsed : throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound);

    private static string? GrantOf(HttpContext http) => http.Request.Query.TryGetValue("grant", out var value) ? value.ToString() : null;

    /// <summary>A valid grant, kept for events; an invalid or expired one is 401 and counted as 5001 with credential <c>grant</c> (§4.6).</summary>
    private static DownloadGrant ReadGrant(DownloadGrants grants, string token, HttpContext http)
    {
        var status = grants.Read(token, out var grant);
        if (status == GrantStatus.Valid)
        {
            http.Items[KnowledgeCallers.GrantItem] = grant;
            return grant!;
        }

        var code = status == GrantStatus.Expired ? KnowledgeCodes.GrantExpired : KnowledgeCodes.GrantInvalid;
        http.RequestServices.GetRequiredService<KnowledgeSecurity>().AuthFailed(http, code, "grant");
        throw new KnowledgeFaultException(401, code,
            status == GrantStatus.Expired ? "The download link has expired. Ask again for a new link." : "The download link is not valid.");
    }

    private static bool AcceptsHtml(HttpContext http) =>
        http.Request.Headers.Accept.Any(a => a is not null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));

    private static KnowledgeFaultException Invalid(string field, string message) =>
        new(400, KnowledgeCodes.InvalidRequest, "The request is not valid.") { Errors = new Dictionary<string, string[]> { [field] = [message] } };

    [LoggerMessage(LogLevel.Error, "The blob of visible document {DocumentId} is missing")]
    private static partial void LogBlobMissing(ILogger logger, Guid documentId);
}
