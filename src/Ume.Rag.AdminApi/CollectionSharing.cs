using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Sharing a collection with other departments and its topics (admin design §3.2, §4.5; Decisions "Organisation:
/// department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)"): the sharing route with V35,
/// the topics route, and which collections another department may see and link (V11). Sharing gives read use through
/// assistants only: a department the collection is shared with sees its summary and topics, never its sources,
/// documents, settings or evaluation (those routes stay scoped to the owner, so they answer 404).
/// </summary>
public static class CollectionSharing
{
    public static RouteGroupBuilder MapCollectionSharing(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var collections = api.MapGroup("/collections").WithTags("Collections");

        collections.MapPut("/{id:guid}/sharing", async (Guid id, SharingRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var collection = await CollectionEndpoints.LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(collection, request.Version);
                    var shares = await admin.Db.CollectionShares.Where(s => s.CollectionId == id).ToListAsync(attempt);
                    var requested = request.DepartmentIds.Distinct().ToList();
                    if (request.SharedWith != CollectionSharingLevels.Selected && requested.Count > 0)
                    {
                        throw AdminFaults.Field("departmentIds", AdminErrorCodes.InvalidValue);
                    }

                    if (request.SharedWith == CollectionSharingLevels.Selected)
                    {
                        // Active departments other than the owner, at least one.
                        var active = await admin.Db.Departments.AsNoTracking().Where(d => requested.Contains(d.Id) && d.IsActive && d.Id != collection.DepartmentId)
                            .CountAsync(attempt);
                        if (requested.Count == 0 || active != requested.Count)
                        {
                            throw AdminFaults.Field("departmentIds", AdminErrorCodes.InvalidValue);
                        }
                    }

                    // V35: a department that loses access must have no published assistant that links the collection.
                    var before = await ReachAsync(admin.Db, collection.DepartmentId, collection.SharedWith, shares.Select(s => s.DepartmentId), attempt);
                    var after = await ReachAsync(admin.Db, collection.DepartmentId, request.SharedWith, requested, attempt);
                    var losing = before.Except(after).ToList();
                    if (losing.Count > 0)
                    {
                        var cutOff = await PublishedLinkersAsync(admin.Db, id, losing, attempt);
                        if (cutOff.Count > 0)
                        {
                            throw AdminFaults.Of(AdminErrorCodes.CollectionSharedInUse, new Dictionary<string, string> { ["collection"] = collection.Name },
                                new Dictionary<string, object?>
                                {
                                    ["affected"] = cutOff.Select(a => $"{a.Name} ({a.DepartmentName})").ToList(),
                                    ["assistants"] = cutOff,
                                });
                        }
                    }

                    var auditBefore = new SharingAudit(collection.SharedWith,
                        collection.SharedWith == CollectionSharingLevels.Selected ? [.. shares.Select(s => s.DepartmentId).Order()] : []);
                    if (request.SharedWith == CollectionSharingLevels.Selected)
                    {
                        // The rows are the list; at the other levels they are kept but unused (data-model §2.1).
                        admin.Db.CollectionShares.RemoveRange(shares.Where(s => !requested.Contains(s.DepartmentId)));
                        foreach (var department in requested.Where(d => !shares.Any(s => s.DepartmentId == d)))
                        {
                            admin.Db.CollectionShares.Add(new CollectionShare { CollectionId = id, DepartmentId = department, SharedBy = admin.Actor, SharedAt = admin.Now });
                        }
                    }

                    collection.SharedWith = request.SharedWith;
                    // The aggregate root's stamp moves its xmin, so a concurrent sharing save conflicts (admin design §4.3).
                    collection.UpdatedAt = admin.Now;
                    collection.UpdatedBy = admin.Actor;
                    return new AdminChange<Collection>(collection, "share", AuditEntityTypes.Collection, collection.Id, auditBefore,
                        new SharingAudit(request.SharedWith, request.SharedWith == CollectionSharingLevels.Selected ? [.. requested.Order()] : []))
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification,
                    };
                }, ct);
                return await CollectionEndpoints.DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("SetCollectionSharing")
            .WithSummary("Share a collection with selected departments or the municipality (V35 refuses a narrowing that cuts off published assistants)");

        collections.MapGet("/{id:guid}/topics", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var collection = await Visible(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var owner = await admin.Departments().AnyAsync(d => d.Id == collection.DepartmentId, ct);
                var counts = await TopicCountsAsync(admin.Db, [id], ct);
                // The path rules are the sources' settings: only the owning department sees them (admin design §3.2).
                var rules = owner ? await PathRulesAsync(admin.Db, id, ct) : [];
                return new TopicsDto(id, [.. counts.Where(c => c.CollectionId == id).Select(c => new TopicDto(c.Tag, c.Documents,
                    owner ? [.. rules.Where(r => r.Tags.Contains(c.Tag, StringComparer.Ordinal)).Select(r => new TopicRuleDto(r.SourceId, r.SourceName, r.On, r.Match))] : null))]);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetCollectionTopics")
            .WithSummary("The tags on a collection's live chunks with document counts (and, for the owner, the path rules that set them)");

        return api;
    }

    /// <summary>
    /// Collections of other departments shared with one of <paramref name="departments"/>: <c>municipality</c> (when one of
    /// them is active) or <c>selected</c> with a share row for one of them. Not deleted.
    /// </summary>
    public static IQueryable<Collection> SharedWith(RagDbContext db, IQueryable<Guid> departments)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Collections.Where(c => c.DeletedAt == null && !departments.Contains(c.DepartmentId) &&
            ((c.SharedWith == CollectionSharingLevels.Municipality && db.Departments.Any(d => d.IsActive && departments.Contains(d.Id))) ||
             (c.SharedWith == CollectionSharingLevels.Selected && db.CollectionShares.Any(s => s.CollectionId == c.Id && departments.Contains(s.DepartmentId)))));
    }

    /// <summary>The caller's own collections and those shared with one of the caller's departments.</summary>
    public static IQueryable<Collection> Visible(AdminContext admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var departments = admin.Departments().Select(d => d.Id);
        return CollectionEndpoints.Scoped(admin).Union(SharedWith(admin.Db, departments));
    }

    /// <summary>The departments other than the owner that may link a collection at <paramref name="level"/>.</summary>
    public static async Task<HashSet<Guid>> ReachAsync(RagDbContext db, Guid owner, string level, IEnumerable<Guid> shares, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return level switch
        {
            CollectionSharingLevels.Municipality => [.. await db.Departments.AsNoTracking().Where(d => d.IsActive && d.Id != owner).Select(d => d.Id).ToListAsync(ct)],
            CollectionSharingLevels.Selected => [.. shares.Where(d => d != owner)],
            _ => [],
        };
    }

    /// <summary>Whether a collection may be linked by an assistant of <paramref name="department"/> (V11): its own, or shared with it.</summary>
    public static async Task<HashSet<Guid>> LinkableAsync(RagDbContext db, Guid department, IReadOnlyCollection<Guid> collectionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var departments = db.Departments.Where(d => d.Id == department).Select(d => d.Id);
        var own = db.Collections.Where(c => c.DeletedAt == null && c.DepartmentId == department);
        return [.. await own.Union(SharedWith(db, departments)).Where(c => collectionIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)];
    }

    /// <summary>Published assistants of <paramref name="departments"/> whose published version links the collection.</summary>
    private static async Task<List<SharedLinker>> PublishedLinkersAsync(RagDbContext db, Guid collectionId, IReadOnlyCollection<Guid> departments, CancellationToken ct) =>
        await (from v in db.AssistantVersionsLinking([collectionId]).AsNoTracking()
               join a in db.Assistants.AsNoTracking() on v.Id equals a.PublishedVersionId
               join d in db.Departments.AsNoTracking() on a.DepartmentId equals d.Id
               where departments.Contains(a.DepartmentId)
               orderby d.Name, a.Name
               select new SharedLinker(a.Id, a.Name, d.Id, d.Name)).ToListAsync(ct);

    /// <summary>Tags on the live chunks of <paramref name="collectionIds"/>, each with the number of documents that carry it.</summary>
    public static async Task<List<TopicCount>> TopicCountsAsync(RagDbContext db, Guid[] collectionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (collectionIds.Length == 0)
        {
            return [];
        }

        return await db.Database.SqlQuery<TopicCount>($"""
            SELECT c."CollectionId", t."Tag", count(DISTINCT c."DocumentId")::int AS "Documents"
            FROM content."Chunks" c
            JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" AND d."DeletedAt" IS NULL
            CROSS JOIN LATERAL unnest(c."Tags") AS t("Tag")
            WHERE c."CollectionId" = ANY ({collectionIds}) AND c."DeletedAt" IS NULL
            GROUP BY c."CollectionId", t."Tag"
            ORDER BY t."Tag"
            """).ToListAsync(ct);
    }

    /// <summary>The path rules (processing design §7) of the collection's live sources' published pipelines that set tags.</summary>
    private static async Task<List<PathRuleTags>> PathRulesAsync(RagDbContext db, Guid collectionId, CancellationToken ct)
    {
        var rows = await (from s in db.Sources.AsNoTracking()
                          join p in db.Pipelines.AsNoTracking() on s.PipelineId equals p.Id
                          join v in db.PipelineVersions.AsNoTracking() on p.PublishedVersionId equals v.Id
                          where s.CollectionId == collectionId && s.DeletedAt == null
                          orderby s.Name
                          select new { s.Id, s.Name, v.Definition }).ToListAsync(ct);
        var rules = new List<PathRuleTags>();
        foreach (var row in rows)
        {
            if (JsonNode.Parse(row.Definition)?["params"]?["pathRules"] is not JsonArray pathRules)
            {
                continue;
            }

            foreach (var rule in pathRules.OfType<JsonObject>())
            {
                if (rule["tags"] is JsonArray tags && tags.Count > 0)
                {
                    rules.Add(new PathRuleTags(row.Id, row.Name, rule["on"]?.GetValue<string>() ?? "", rule["match"]?.GetValue<string>() ?? "",
                        [.. tags.Select(t => t?.GetValue<string>()).OfType<string>()]));
                }
            }
        }

        return rules;
    }

    private sealed record PathRuleTags(Guid SourceId, string SourceName, string On, string Match, IReadOnlyList<string> Tags);
}

/// <summary>One tag of a collection with its live document count.</summary>
public sealed class TopicCount
{
    public Guid CollectionId { get; init; }
    public string Tag { get; init; } = "";
    public int Documents { get; init; }
}

/// <summary>A published assistant that would lose a collection (V35).</summary>
public sealed record SharedLinker(Guid Id, string Name, Guid DepartmentId, string DepartmentName);

public sealed record TopicsDto(Guid CollectionId, IReadOnlyList<TopicDto> Topics);

/// <summary><see cref="Rules"/>: the path rules that set the tag; null for a department the collection is only shared with.</summary>
public sealed record TopicDto(string Tag, int Documents, IReadOnlyList<TopicRuleDto>? Rules);

public sealed record TopicRuleDto(Guid SourceId, string SourceName, string On, string Match);

public sealed record SharingAudit(string SharedWith, IReadOnlyList<Guid> DepartmentIds);

public sealed record SharingRequest : AdminRequest, IValidatableObject
{
    [Required]
    public string SharedWith { get; init; } = "";

    /// <summary>Only with <c>selected</c>: active departments other than the owner.</summary>
    [MaxLength(500)]
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];

    [Required]
    public string? Version { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!CollectionSharingLevels.All.Contains(SharedWith))
        {
            yield return new ValidationResult(
                string.Create(CultureInfo.InvariantCulture, $"The sharing must be one of {string.Join(", ", CollectionSharingLevels.All)}."), [nameof(SharedWith)]);
        }
    }
}
