using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;

namespace Kalla.AdminApi;

/// <summary>
/// Granskningslogg (admin design §9.1, P1b item 9): the audit log by keyset, newest first, with the filters period, actor,
/// action, entity type, entity id, department and service; an entry's detail with the stored before/after snapshot as text.
/// Platform admins see every entry, department admins their departments' entries (<c>AuditLog.DepartmentId</c>). Every
/// successful read emits 6007 (<see cref="AdminActivityMiddleware"/>, identity §10).
/// </summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAudit(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var audit = api.MapGroup("/audit").WithTags("Audit");

        audit.MapGet("", async (DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, string? entityType, string? entityId,
                Guid? departmentId, string? service, long? after, int? limit, AdminContext admin, CancellationToken ct) =>
            {
                var take = Keyset.Take(limit);
                var query = Scoped(admin);
                if (from is { } start)
                {
                    query = query.Where(a => a.Timestamp >= start);
                }

                if (to is { } end)
                {
                    query = query.Where(a => a.Timestamp < end);
                }

                if (!string.IsNullOrWhiteSpace(actor))
                {
                    var pattern = Like.Contains(actor.Trim());
                    query = query.Where(a => a.Actor == actor.Trim() || EF.Functions.ILike(a.ActorName ?? "", pattern, Like.EscapeCharacter));
                }

                if (!string.IsNullOrWhiteSpace(action))
                {
                    query = query.Where(a => a.Action == action.Trim());
                }

                if (!string.IsNullOrWhiteSpace(entityType))
                {
                    query = query.Where(a => a.EntityType == entityType.Trim());
                }

                if (!string.IsNullOrWhiteSpace(entityId))
                {
                    query = query.Where(a => a.EntityId == entityId.Trim());
                }

                if (departmentId is { } department)
                {
                    query = query.Where(a => a.DepartmentId == department);
                }

                if (!string.IsNullOrWhiteSpace(service))
                {
                    query = query.Where(a => a.Service == service.Trim());
                }

                if (after is { } last)
                {
                    query = query.Where(a => a.Id < last);
                }

                var rows = await query.OrderByDescending(a => a.Id).Take(take + 1)
                    .Select(a => new AuditSummary(a.Id, a.Timestamp, a.Actor, a.ActorName, a.Action, a.EntityType, a.EntityId, null, null, a.Service,
                        a.DepartmentId, a.Classification, a.ConfigVersion, a.Reason))
                    .ToListAsync(ct);
                var (page, next) = KeysetPage.From(rows, take, a => a.Id);
                return new KeysetPage<AuditSummary, long?>(await NamedAsync(admin.Db, [.. page], ct), next);
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("ListAudit")
            .WithSummary("The audit log, newest first (keyset; period, actor, action, entity, department, service)");

        // No route constraint: a malformed id is 404 not_found after authorization, like an unknown one (as templates).
        audit.MapGet("/{id}", async (string id, AdminContext admin, CancellationToken ct) =>
                !long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var entry)
                    ? throw AdminFaults.Of(AdminErrorCodes.NotFound)
                    : await Scoped(admin).Where(a => a.Id == entry)
                    .Select(a => new AuditDetail(a.Id, a.Timestamp, a.RecordedAt, a.Actor, a.ActorName, a.ActorRoles, a.ActorDepartments, a.Action,
                        a.EntityType, a.EntityId, a.Service, a.DepartmentId, a.Classification, a.ConfigVersion, a.Reason, a.Details, a.SourceAddress,
                        a.RequestId, a.UserAgent))
                    .SingleOrDefaultAsync(ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound))
            .RequireAuthorization(AdminPolicies.Manage).WithName("GetAuditEntry").WithSummary("An audit entry with its before/after snapshot (JSON text)");

        return api;
    }

    /// <summary>
    /// The entries with their object's name (admin design §9.1 "Readable entries"): the current name for the types that have
    /// one, read in one query per type for the whole page, and whether the object still exists (also for runs, which have no
    /// name); else the <c>name</c> in the entry's snapshot (after, then before), read in one query for the rows still without.
    /// </summary>
    private static async Task<List<AuditSummary>> NamedAsync(RagDbContext db, List<AuditSummary> rows, CancellationToken ct)
    {
        var current = new Dictionary<(string Type, string Id), string?>();
        var looked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rows.Where(r => r.EntityId is not null).GroupBy(r => r.EntityType, StringComparer.Ordinal))
        {
            var ids = group.Select(r => r.EntityId!).Distinct(StringComparer.Ordinal).ToList();
            if (group.Key == AuditEntityTypes.GatewayModel)
            {
                looked.Add(group.Key);
                foreach (var name in await db.GatewayModels.AsNoTracking().Where(m => ids.Contains(m.Name)).Select(m => m.Name).ToListAsync(ct))
                {
                    current[(group.Key, name)] = name;
                }

                continue;
            }

            var guids = ids.Select(i => Guid.TryParse(i, out var g) ? g : (Guid?)null).OfType<Guid>().Distinct().ToList();
            if (CurrentQuery(db, group.Key, guids) is not { } found)
            {
                continue;
            }

            looked.Add(group.Key);
            if (guids.Count == 0)
            {
                continue;
            }

            foreach (var row in await found.ToListAsync(ct))
            {
                current[(group.Key, row.Id.ToString("D"))] = row.Name;
            }
        }

        var named = rows.Select(r =>
        {
            var exists = current.TryGetValue((r.EntityType, Key(r.EntityType, r.EntityId)), out var name);
            return r with { EntityName = name, EntityExists = looked.Contains(r.EntityType) && r.EntityId is not null ? exists : null };
        }).ToList();

        var missing = named.Where(r => r.EntityName is null).Select(r => r.Id).ToArray();
        if (missing.Length == 0)
        {
            return named;
        }

        var snapshots = (await SnapshotNamesAsync(db, missing, ct)).Where(s => s.Name is not null).ToDictionary(s => s.Id, s => s.Name);
        return [.. named.Select(r => r.EntityName is null && snapshots.TryGetValue(r.Id, out var name) ? r with { EntityName = name } : r)];
    }

    /// <summary>An entity id as the lookups key it: a gateway model by its name, the others by their id in one Guid format.</summary>
    private static string Key(string entityType, string? entityId) =>
        entityType != AuditEntityTypes.GatewayModel && Guid.TryParse(entityId, out var id) ? id.ToString("D") : entityId ?? "";

    /// <summary>The current objects of a type among <paramref name="ids"/> with their names, or null for a type with no lookup.</summary>
    private static IQueryable<CurrentObject>? CurrentQuery(RagDbContext db, string entityType, List<Guid> ids) => entityType switch
    {
        AuditEntityTypes.Collection => db.Collections.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new CurrentObject(c.Id, c.Name)),
        AuditEntityTypes.Source => db.Sources.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new CurrentObject(s.Id, s.Name)),
        AuditEntityTypes.Assistant => db.Assistants.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new CurrentObject(a.Id, a.Name)),
        AuditEntityTypes.Client => db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new CurrentObject(c.Id, c.Name)),
        AuditEntityTypes.Key => db.Keys.AsNoTracking().Where(k => ids.Contains(k.Id)).Select(k => new CurrentObject(k.Id, k.Name)),
        AuditEntityTypes.EmbeddingProfile or AuditEntityTypes.EmbeddingProfileIndex =>
            db.EmbeddingProfiles.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new CurrentObject(p.Id, p.Name)),
        AuditEntityTypes.SiteProfile => db.SiteProfiles.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new CurrentObject(p.Id, p.Host)),
        AuditEntityTypes.Run => db.Runs.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new CurrentObject(r.Id, null)),
        _ => null,
    };

    /// <summary>The <c>name</c> in the snapshot of each entry in <paramref name="ids"/>: the after state's, else the before state's.</summary>
    private static Task<List<AuditSnapshotName>> SnapshotNamesAsync(RagDbContext db, long[] ids, CancellationToken ct) =>
        db.Database.SqlQuery<AuditSnapshotName>($"""
            SELECT "Id", COALESCE("Details" -> 'after' ->> 'name', "Details" -> 'before' ->> 'name') AS "Name"
            FROM audit."AuditLog" WHERE "Id" = ANY({ids})
            """).ToListAsync(ct);

    private sealed record CurrentObject(Guid Id, string? Name);

    /// <summary>Every entry for a platform admin; a department admin's departments' entries only (entries without a department are platform-wide).</summary>
    private static IQueryable<AuditLogEntry> Scoped(AdminContext admin)
    {
        var entries = admin.Db.AuditLog.AsNoTracking();
        if (admin.IsPlatformAdmin)
        {
            return entries;
        }

        var departments = admin.Departments().Select(d => (Guid?)d.Id);
        return entries.Where(a => departments.Contains(a.DepartmentId));
    }
}

/// <summary>
/// An entry in the list. <see cref="EntityName"/> is the object's current name, else the name in the entry's snapshot, else null;
/// <see cref="EntityExists"/> whether the object still exists, for the types the list looks up (null for the others).
/// </summary>
public sealed record AuditSummary(
    long Id, DateTimeOffset Timestamp, string Actor, string? ActorName, string Action, string EntityType, string? EntityId, string? EntityName,
    bool? EntityExists, string? Service, Guid? DepartmentId, string? Classification, long? ConfigVersion, string? Reason);

/// <summary>The name in an audit entry's snapshot (the SQL's columns).</summary>
public sealed class AuditSnapshotName
{
    public long Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>An entry as stored: <see cref="Details"/> is the before/after snapshot JSON as text (admin design §9.1; the diff viewer is P2).</summary>
public sealed record AuditDetail(
    long Id, DateTimeOffset Timestamp, DateTimeOffset RecordedAt, string Actor, string? ActorName, string? ActorRoles, string? ActorDepartments,
    string Action, string EntityType, string? EntityId, string? Service, Guid? DepartmentId, string? Classification, long? ConfigVersion, string? Reason,
    string? Details, string? SourceAddress, string? RequestId, string? UserAgent);
