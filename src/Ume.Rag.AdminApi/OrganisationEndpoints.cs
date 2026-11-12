using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>The organisation mirror (admin design §4.10): departments and teams as synced from the gateway.</summary>
public static class OrganisationEndpoints
{
    public static RouteGroupBuilder MapOrganisation(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var organisation = api.MapGroup("/organisation").WithTags("Organisation");

        // Departments in the user's scope (every one for a platform admin), active first, each with its member groups (from the
        // gateway, read-only) and how its knowledge bases are shared (admin design §3.2).
        organisation.MapGet("/departments", async (AdminContext admin, CancellationToken ct) =>
        {
            var collections = admin.Db.Collections.Where(c => c.DeletedAt == null);
            return await admin.Departments().AsNoTracking().OrderByDescending(d => d.IsActive).ThenBy(d => d.Name)
                .Select(d => new DepartmentDto(d.Id, d.Name, d.CostCenterCode, d.IsActive, d.SyncedAt, d.MemberGroups,
                    d.Teams.OrderBy(t => t.Name).Select(t => new TeamDto(t.Id, t.Name, t.Code, t.IsActive)).ToList(),
                    new SharingSummaryDto(
                        collections.Count(c => c.DepartmentId == d.Id && c.SharedWith == CollectionSharingLevels.Department),
                        collections.Count(c => c.DepartmentId == d.Id && c.SharedWith == CollectionSharingLevels.Selected),
                        collections.Count(c => c.DepartmentId == d.Id && c.SharedWith == CollectionSharingLevels.Municipality),
                        admin.Db.CollectionShares
                            .Where(s => s.DepartmentId != d.Id && collections.Any(c =>
                                c.Id == s.CollectionId && c.DepartmentId == d.Id && c.SharedWith == CollectionSharingLevels.Selected))
                            .Select(s => s.DepartmentId).Distinct().Count())))
                .ToListAsync(ct);
        })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListDepartments").WithSummary("Departments and teams in scope");

        // Every active department by name, for the sharing picker (admin design §5.5.2: "Selected departments"): a department
        // admin's scope holds only their own departments, but they share with any other. Names only.
        organisation.MapGet("/departments/names", async (AdminContext admin, CancellationToken ct) =>
                await admin.Db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).Select(d => new DepartmentNameDto(d.Id, d.Name)).ToListAsync(ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("ListDepartmentNames").WithSummary("Every active department's id and name (pickers)");

        // The Map as a table (admin design §5.5.6, P1b item 35): department → knowledge base → assistant → audience.
        organisation.MapGet("/departments/{id:guid}/map", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var department = await admin.Departments().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                return await DepartmentMap.BuildAsync(admin.Db, department, ct);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetDepartmentMap")
            .WithSummary("The department's knowledge bases (own and shared in), the assistants that link them and their audiences, as table rows");

        // Sync now, and the last runs for the overview (admin design §3.4: platform admins).
        organisation.MapPost("/sync", async (HttpContext http, OrganisationSync sync, CancellationToken ct) =>
            !sync.Enabled
                ? throw AdminFaults.Of(AdminErrorCodes.SyncOff)
                : await sync.RunAsync(AdminActor.Of(http), ct) is { } result
                    ? Results.Ok(result)
                    : throw AdminFaults.Of(AdminErrorCodes.SyncFailed, new Dictionary<string, string> { ["error"] = sync.Status.LastError ?? "" }))
            .RequireAuthorization(AdminPolicies.Admin).WithName("SyncOrganisation").WithSummary("Sync departments, teams and gateway keys from the gateway's context now");
        organisation.MapGet("/sync", (OrganisationSync sync) => sync.Status)
            .RequireAuthorization(AdminPolicies.Admin).WithName("OrganisationSyncStatus").WithSummary("The organisation sync's last runs");

        return api;
    }
}

/// <summary>
/// The Map's rows (admin design §5.5.6): one per department → knowledge base → assistant → audience path. The
/// department's own knowledge bases (also when none of its assistants links them) and the shared ones its assistants
/// link, with their owning department; its assistants with their published version, a link only the draft has marked
/// <c>draftOnly</c>, and the link's topics; each audience client. No content. Archived assistants are left out: the Map
/// shows who uses what now (#374), so a knowledge base only an archived assistant links ends its path there.
/// </summary>
public static class DepartmentMap
{
    public static async Task<MapDto> BuildAsync(Ume.Rag.Infrastructure.Persistence.RagDbContext db, Department department, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(department);
        var assistants = await db.Assistants.AsNoTracking().Where(a => a.DepartmentId == department.Id && a.State != AssistantStates.Archived).OrderBy(a => a.Name).ToListAsync(ct);
        var versionIds = assistants.SelectMany(a => new[] { a.PublishedVersionId, a.DraftVersionId }).OfType<Guid>().ToList();
        var versions = await db.AssistantVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var assistantIds = assistants.Select(a => a.Id).ToList();
        var audiences = (await (from x in db.AssistantAudiences.AsNoTracking()
                                join c in db.Clients.AsNoTracking() on x.SubjectId equals c.Id
                                where assistantIds.Contains(x.AssistantId) && x.Kind == AssistantAudienceKinds.Client
                                orderby c.Name
                                select new { x.AssistantId, Audience = new MapAudienceDto(c.Id, c.Name, c.Kind) }).ToListAsync(ct))
            .ToLookup(x => x.AssistantId, x => x.Audience);

        // Per assistant: its links, the published version's first and then those only the draft has.
        var links = assistants.Select(a =>
        {
            var published = a.PublishedVersionId is { } p && versions.TryGetValue(p, out var pv) ? pv.CollectionLinks : [];
            var draft = a.DraftVersionId is { } d && versions.TryGetValue(d, out var dv) ? dv.CollectionLinks : [];
            var number = a.PublishedVersionId is { } n && versions.TryGetValue(n, out var nv) ? nv.Number : (int?)null;
            var all = published.Select(l => (Link: l, DraftOnly: false))
                .Concat(draft.Where(l => published.All(x => x.CollectionId != l.CollectionId)).Select(l => (Link: l, DraftOnly: true)));
            return (Assistant: a, Number: number, Links: all.ToList());
        }).ToList();

        var linkedIds = links.SelectMany(l => l.Links.Select(x => x.Link.CollectionId)).Distinct().ToList();
        var collections = await db.Collections.AsNoTracking()
            .Where(c => c.DeletedAt == null && (c.DepartmentId == department.Id || linkedIds.Contains(c.Id))).ToDictionaryAsync(c => c.Id, ct);
        var owners = collections.Values.Select(c => c.DepartmentId).Distinct().ToList();
        var names = await db.Departments.AsNoTracking().Where(d => owners.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        MapCollectionDto Collection(Collection c) => new(c.Id, c.Name, c.DepartmentId, names.GetValueOrDefault(c.DepartmentId) ?? "", c.DepartmentId != department.Id,
            c.SharedWith, c.Classification, c.PublishedAt is not null, c.ContactName, c.ContactEmail);

        var rows = new List<MapRowDto>();
        foreach (var (assistant, number, assistantLinks) in links)
        {
            foreach (var (link, draftOnly) in assistantLinks)
            {
                if (!collections.TryGetValue(link.CollectionId, out var collection))
                {
                    continue; // deleted since: the publish check (V34) reports it
                }

                var entry = new MapAssistantDto(assistant.Id, assistant.Name, assistant.State, number, draftOnly, link.Tags, assistant.ContactName, assistant.ContactEmail);
                var clients = audiences[assistant.Id].ToList();
                rows.AddRange(clients.Count == 0
                    ? [new MapRowDto(Collection(collection), entry, null)]
                    : clients.Select(client => new MapRowDto(Collection(collection), entry, client)));
            }
        }

        // The department's own knowledge bases that none of its assistants links.
        var linkedHere = rows.Select(r => r.Collection.Id).ToHashSet();
        rows.AddRange(collections.Values.Where(c => c.DepartmentId == department.Id && !linkedHere.Contains(c.Id)).Select(c => new MapRowDto(Collection(c), null, null)));
        return new MapDto(department.Id, department.Name,
            [.. rows.OrderBy(r => r.Collection.Shared).ThenBy(r => r.Collection.Name, StringComparer.Ordinal).ThenBy(r => r.Assistant?.Name, StringComparer.Ordinal)
                .ThenBy(r => r.Audience?.Name, StringComparer.Ordinal)]);
    }
}

public sealed record DepartmentNameDto(Guid Id, string Name);

public sealed record MapDto(Guid DepartmentId, string DepartmentName, IReadOnlyList<MapRowDto> Rows);

/// <summary>A path; <see cref="Assistant"/> and <see cref="Audience"/> are null where the path ends earlier.</summary>
public sealed record MapRowDto(MapCollectionDto Collection, MapAssistantDto? Assistant, MapAudienceDto? Audience);

/// <summary><see cref="Shared"/>: owned by another department and shared in.</summary>
public sealed record MapCollectionDto(
    Guid Id, string Name, Guid DepartmentId, string DepartmentName, bool Shared, string SharedWith, string Classification, bool Published, string? ContactName,
    string? ContactEmail);

/// <summary><see cref="PublishedVersion"/>: null before the first publish; <see cref="DraftOnly"/>: the link is only in the draft.</summary>
public sealed record MapAssistantDto(
    Guid Id, string Name, string State, int? PublishedVersion, bool DraftOnly, IReadOnlyList<string> Topics, string? ContactName, string? ContactEmail);

public sealed record MapAudienceDto(Guid ClientId, string Name, string Kind);

public sealed record DepartmentDto(Guid Id, string Name, string CostCenterCode, bool IsActive, DateTimeOffset SyncedAt,
    IReadOnlyList<string> MemberGroups, IReadOnlyList<TeamDto> Teams, SharingSummaryDto Sharing);

/// <summary>
/// A department's live knowledge bases per sharing level, and how many other departments its <c>selected</c> ones are
/// shared with.
/// </summary>
public sealed record SharingSummaryDto(int Department, int Selected, int Municipality, int SharedWithDepartments);

public sealed record TeamDto(Guid Id, string Name, string? Code, bool IsActive);
