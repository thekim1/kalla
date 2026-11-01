using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Documents (admin design §4.5 "Documents", P1b item 9): the list by keyset, a document with its chunk sets, PII state
/// and run items of 90 days, the live set's chunks, and the decisions as <c>command</c> jobs: <c>clear-pii-flag</c>
/// (reason ≥ 10, V26) and <c>reprocess-document</c>. Also a source's documents kept by a deletion decision
/// (<c>Documents.HeldAt</c>, connectors §13.3) and <c>release-hold</c>. A document is in scope when its source is.
/// </summary>
public static class DocumentEndpoints
{
    public const int MaxLimit = 200;

    /// <summary>The live set's chunks returned at most (a document's sections; more is <c>truncated</c>).</summary>
    public const int MaxChunks = 1000;

    /// <summary>How far back a document's run items go (admin design §4.5).</summary>
    public static readonly TimeSpan RunItemWindow = TimeSpan.FromDays(90);

    public static RouteGroupBuilder MapDocuments(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var documents = api.MapGroup("/documents").WithTags("Documents");

        documents.MapGet("", async (Guid? collectionId, Guid? sourceId, string? q, string? pii, string? outcome, Guid? after, int? limit,
                AdminContext admin, CancellationToken ct) =>
            {
                var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
                var sources = SourceEndpoints.Scoped(admin);
                if (collectionId is { } collection)
                {
                    sources = sources.Where(s => s.CollectionId == collection);
                }

                if (sourceId is { } source)
                {
                    sources = sources.Where(s => s.Id == source);
                }

                var ids = sources.Select(s => s.Id);
                var query = admin.Db.Documents.AsNoTracking().Where(d => d.DeletedAt == null && ids.Contains(d.SourceId));
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var pattern = $"%{Like.Escape(q.Trim())}%";
                    query = query.Where(d => EF.Functions.ILike(d.Title ?? "", pattern, Like.EscapeCharacter)
                        || EF.Functions.ILike(d.Url ?? "", pattern, Like.EscapeCharacter)
                        || EF.Functions.ILike(d.DisplayName ?? "", pattern, Like.EscapeCharacter));
                }

                query = pii switch
                {
                    null or "" => query,
                    PiiFilters.Flagged => query.Where(d => admin.Db.ChunkSets.Any(cs => cs.DocumentId == d.Id && cs.PiiFlagged
                        && (cs.State == ChunkSetStates.Held || (cs.State == ChunkSetStates.Live && cs.Id == d.LiveChunkSetId)))),
                    PiiFilters.Cleared => query.Where(d => d.PiiClearedAt != null),
                    _ => throw AdminFaults.Field("pii", AdminErrorCodes.InvalidValue),
                };
                if (!string.IsNullOrWhiteSpace(outcome))
                {
                    query = query.Where(d => d.ParseOutcome == outcome);
                }

                if (after is { } last)
                {
                    query = query.Where(d => d.Id.CompareTo(last) > 0);
                }

                var rows = await query.OrderBy(d => d.Id).Take(take + 1)
                    .Select(d => new DocumentSummary(d.Id, d.SourceId, d.Title, d.Url, d.DisplayName, d.MediaType, d.ParseOutcome,
                        admin.Db.ChunkSets.Any(cs => cs.DocumentId == d.Id && cs.PiiFlagged
                            && (cs.State == ChunkSetStates.Held || (cs.State == ChunkSetStates.Live && cs.Id == d.LiveChunkSetId))),
                        d.PiiClearedAt != null, d.HeldAt != null, d.LiveChunkSetId != null, d.SourceModifiedAt, d.FetchedAt, d.LastSeenAt))
                    .ToListAsync(ct);
                return new KeysetPage<DocumentSummary, Guid?>([.. rows.Take(take)], rows.Count > take ? rows[take - 1].Id : null);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListDocuments")
            .WithSummary("Documents (keyset; collectionId, sourceId, q, pii flagged|cleared, outcome)");

        documents.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var document = await ScopedAsync(admin, id, ct);
                var sets = await admin.Db.ChunkSets.AsNoTracking().Where(cs => cs.DocumentId == id).OrderByDescending(cs => cs.CreatedAt).ToListAsync(ct);
                var since = admin.Now - RunItemWindow;
                var items = await admin.Db.RunItems.AsNoTracking().Where(i => i.DocumentId == id && i.CreatedAt >= since)
                    .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(MaxLimit)
                    .Select(i => new DocumentRunItem(i.RunId, i.Id, i.CreatedAt, i.Outcome, i.Reason, i.Strategy, i.DurationMs))
                    .ToListAsync(ct);
                var live = sets.FirstOrDefault(cs => cs.Id == document.LiveChunkSetId);
                return new DocumentDetail(
                    document.Id, document.SourceId, document.Classification, document.IdentityKind, document.CanonicalIdentity, document.Title,
                    document.Url, document.DisplayName, document.MediaType, document.Language, document.PageCount, document.ParseStrategy,
                    document.ParseOutcome, document.PipelineVersionId, document.SiteProfileVersionId, document.LiveChunkSetId,
                    live is null ? null : PiiState.Of(live), document.PiiClearedAt, document.PiiClearedBy, document.HeldAt, document.HeldBy,
                    document.SourceModifiedAt, document.FetchedAt, document.FirstSeenAt, document.LastSeenAt,
                    [.. sets.Select(ChunkSetDto.Of)], items);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetDocument")
            .WithSummary("A document: its chunk sets (pipeline and site-profile version), PII state and run items of 90 days");

        documents.MapGet("/{id:guid}/chunks", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var document = await ScopedAsync(admin, id, ct);
                if (document.LiveChunkSetId is not { } set)
                {
                    return new DocumentChunks(id, null, [], false);
                }

                var rows = await admin.Db.Chunks.AsNoTracking().Where(c => c.ChunkSetId == set && c.Kind == ChunkKinds.Child)
                    .OrderBy(c => c.Ordinal).Take(MaxChunks + 1)
                    .Select(c => new ChunkDto(c.Id, c.Ordinal, c.HeadingPath, c.Text, c.TokenCount, c.Page, c.PageEnd, c.ContentKind))
                    .ToListAsync(ct);
                return new DocumentChunks(id, set, [.. rows.Take(MaxChunks)], rows.Count > MaxChunks);
            })
            .RequireAuthorization(AdminPolicies.EditPipelines).WithName("GetDocumentChunks")
            .WithSummary("The live chunk set's chunks: text, heading path, tokens");

        documents.MapPost("/{id:guid}/pii-clear", async (Guid id, PiiClearRequest request, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var reason = Reasons.Check(request.Reason, Reasons.PiiClearMin);
                    var document = await ScopedAsync(admin, id, attempt);
                    var flagged = await admin.Db.ChunkSets.AsNoTracking().AnyAsync(cs => cs.DocumentId == id && cs.PiiFlagged
                        && (cs.State == ChunkSetStates.Held || (cs.State == ChunkSetStates.Live && cs.Id == document.LiveChunkSetId)), attempt);
                    if (!flagged)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.PiiNotFlagged);
                    }

                    var (department, _) = await DepartmentAsync(admin, document.SourceId, attempt);
                    var job = CommandJobs.ClearPiiFlagJob(document.Id, admin.Actor, department, document.Classification);
                    return new AdminChange<CommandStarted>(new CommandStarted(job.Id), "pii-clear", AuditEntityTypes.Document, document.Id, null,
                        new { jobId = job.Id })
                    {
                        DepartmentId = department, Classification = document.Classification, Reason = reason, Jobs = [job],
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.Manage).WithName("ClearPiiFlag")
            .WithSummary("Release a PII-flagged document (reason ≥ 10; command clear-pii-flag)");

        documents.MapPost("/{id:guid}/reprocess", async (Guid id, ReprocessRequest request, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var document = await ScopedAsync(admin, id, attempt);
                    var (department, _) = await DepartmentAsync(admin, document.SourceId, attempt);
                    var job = CommandJobs.ReprocessDocumentJob(document.Id, request.FromStage, department, document.Classification);
                    return new AdminChange<CommandStarted>(new CommandStarted(job.Id), "reprocess", AuditEntityTypes.Document, document.Id, null,
                        new { jobId = job.Id, fromStage = request.FromStage })
                    {
                        DepartmentId = department, Classification = document.Classification, Jobs = [job],
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.Manage).WithName("ReprocessDocument")
            .WithSummary("Process a document again, optionally from a stage (command reprocess-document)");

        api.MapGet("/sources/{id:guid}/held", async (Guid id, Guid? after, int? limit, AdminContext admin, CancellationToken ct) =>
            {
                _ = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
                var query = admin.Db.Documents.AsNoTracking().Where(d => d.SourceId == id && d.DeletedAt == null && d.HeldAt != null);
                if (after is { } last)
                {
                    query = query.Where(d => d.Id.CompareTo(last) > 0);
                }

                var rows = await query.OrderBy(d => d.Id).Take(take + 1)
                    .Select(d => new HeldDocument(d.Id, d.Title, d.Url, d.DisplayName, d.HeldAt!.Value, d.HeldBy, d.LastSeenAt))
                    .ToListAsync(ct);
                return new KeysetPage<HeldDocument, Guid?>([.. rows.Take(take)], rows.Count > take ? rows[take - 1].Id : null);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Sources").WithName("ListHeldDocuments")
            .WithSummary("A source's documents kept by a rejected deletion (keyset)");

        api.MapPost("/sources/{id:guid}/held/release", async (Guid id, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var source = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    var department = await SourceEndpoints.DepartmentOfAsync(admin, source, attempt);
                    var job = CommandJobs.ReleaseHoldJob(source.Id, department, source.Classification);
                    return new AdminChange<CommandStarted>(new CommandStarted(job.Id), "release-hold", AuditEntityTypes.Source, source.Id, null,
                        new { jobId = job.Id })
                    {
                        DepartmentId = department, Classification = source.Classification, Jobs = [job],
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.Manage).WithTags("Sources").WithName("ReleaseHeldDocuments")
            .WithSummary("Release the source's held documents: candidates again at the next reconcile (command release-hold)");

        return api;
    }

    private static async Task<Document> ScopedAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        var sources = SourceEndpoints.Scoped(admin).Select(s => s.Id);
        return await admin.Db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.DeletedAt == null && sources.Contains(d.SourceId), ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }

    internal static async Task<(Guid Department, Source Source)> DepartmentAsync(AdminContext admin, Guid sourceId, CancellationToken ct)
    {
        var source = await admin.Db.Sources.AsNoTracking().SingleAsync(s => s.Id == sourceId, ct);
        return (await SourceEndpoints.DepartmentOfAsync(admin, source, ct), source);
    }
}

/// <summary>The <c>pii</c> filter of the document list.</summary>
public static class PiiFilters
{
    /// <summary>A held flagged set, or a flagged live set.</summary>
    public const string Flagged = "flagged";

    /// <summary>Released by an admin (<c>PiiClearedAt</c>).</summary>
    public const string Cleared = "cleared";
}

public sealed record DocumentSummary(
    Guid Id, Guid SourceId, string? Title, string? Url, string? DisplayName, string? MediaType, string? ParseOutcome, bool PiiFlagged, bool PiiCleared,
    bool Held, bool Live, DateTimeOffset? SourceModifiedAt, DateTimeOffset? FetchedAt, DateTimeOffset LastSeenAt);

/// <summary>A chunk set's PII state: flagged, counts per type and fired rules (numbers only, never values).</summary>
public sealed record PiiState(bool Flagged, DateTimeOffset? FlaggedAt, IReadOnlyDictionary<string, int> Counts, int RulesFired)
{
    public static PiiState Of(ChunkSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var counts = PiiReportEndpoints.Counts(set.PiiCounts, out var rules);
        return new PiiState(set.PiiFlagged, set.PiiFlaggedAt, counts, rules);
    }
}

public sealed record ChunkSetDto(
    Guid Id, string State, Guid CollectionId, Guid PipelineVersionId, Guid? SiteProfileVersionId, Guid EmbeddingProfileId, int ChunkCount, PiiState Pii,
    DateTimeOffset CreatedAt, DateTimeOffset? LiveAt, DateTimeOffset? SupersededAt)
{
    public static ChunkSetDto Of(ChunkSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new(set.Id, set.State, set.CollectionId, set.PipelineVersionId, set.SiteProfileVersionId, set.EmbeddingProfileId, set.ChunkCount,
            PiiState.Of(set), set.CreatedAt, set.LiveAt, set.SupersededAt);
    }
}

public sealed record DocumentRunItem(Guid RunId, long ItemId, DateTimeOffset At, string Outcome, string? Reason, string? Strategy, int? DurationMs);

public sealed record DocumentDetail(
    Guid Id, Guid SourceId, string Classification, string IdentityKind, string CanonicalIdentity, string? Title, string? Url, string? DisplayName,
    string? MediaType, string? Language, int? PageCount, string? ParseStrategy, string? ParseOutcome, Guid? PipelineVersionId, Guid? SiteProfileVersionId,
    Guid? LiveChunkSetId, PiiState? LivePii, DateTimeOffset? PiiClearedAt, string? PiiClearedBy, DateTimeOffset? HeldAt, string? HeldBy,
    DateTimeOffset? SourceModifiedAt, DateTimeOffset? FetchedAt, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    IReadOnlyList<ChunkSetDto> ChunkSets, IReadOnlyList<DocumentRunItem> RunItems);

public sealed record ChunkDto(Guid Id, int Ordinal, string? HeadingPath, string Text, int TokenCount, int? Page, int? PageEnd, string ContentKind);

public sealed record DocumentChunks(Guid DocumentId, Guid? ChunkSetId, IReadOnlyList<ChunkDto> Chunks, bool Truncated);

public sealed record HeldDocument(Guid Id, string? Title, string? Url, string? DisplayName, DateTimeOffset HeldAt, string? HeldBy, DateTimeOffset LastSeenAt);

/// <summary>The command job a decision started.</summary>
public sealed record CommandStarted(Guid JobId);

/// <summary>V26: why a flagged document may be served (≥ 10 characters after trimming, audited; <see cref="Reasons"/>).</summary>
public sealed record PiiClearRequest : AdminRequest
{
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>V26's free-text reasons (admin design §4.6): trimmed, at least a minimum length, else <c>reason_too_short</c>.</summary>
public static class Reasons
{
    public const int PiiClearMin = 10;

    public static string Check(string? reason, int min)
    {
        var trimmed = reason?.Trim() ?? "";
        return trimmed.Length >= min
            ? trimmed
            : throw AdminFaults.Field("reason", AdminErrorCodes.ReasonTooShort, new Dictionary<string, string> { ["min"] = min.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
}

public sealed record ReprocessRequest : AdminRequest
{
    /// <summary>The first stage to run again; omitted, the whole pipeline (<c>command.reprocess-document</c>).</summary>
    [AllowedValues(null, "fetch", "scan", "detect", "parse", "clean", "chunk", "personal-data", "embed", "index")]
    public string? FromStage { get; init; }
}
