using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Granskningslogg (admin design §9.1, P1b item 9): the audit log by keyset, newest first, with the filters period, actor,
/// action, entity type, entity id, department and service; an entry's detail with the stored before/after snapshot as text.
/// Platform admins see every entry, department admins their departments' entries (<c>AuditLog.DepartmentId</c>). Every
/// successful read emits 6007 (<see cref="AdminActivityMiddleware"/>, identity §10).
/// </summary>
public static class AuditEndpoints
{
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapAudit(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var audit = api.MapGroup("/audit").WithTags("Audit");

        audit.MapGet("", async (DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, string? entityType, string? entityId,
                Guid? departmentId, string? service, long? after, int? limit, AdminContext admin, CancellationToken ct) =>
            {
                var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
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
                    var pattern = $"%{Like.Escape(actor.Trim())}%";
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
                    .Select(a => new AuditSummary(a.Id, a.Timestamp, a.Actor, a.ActorName, a.Action, a.EntityType, a.EntityId, a.Service, a.DepartmentId,
                        a.Classification, a.ConfigVersion, a.Reason))
                    .ToListAsync(ct);
                return new KeysetPage<AuditSummary, long?>([.. rows.Take(take)], rows.Count > take ? rows[take - 1].Id : null);
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

public sealed record AuditSummary(
    long Id, DateTimeOffset Timestamp, string Actor, string? ActorName, string Action, string EntityType, string? EntityId, string? Service,
    Guid? DepartmentId, string? Classification, long? ConfigVersion, string? Reason);

/// <summary>An entry as stored: <see cref="Details"/> is the before/after snapshot JSON as text (admin design §9.1; the diff viewer is P2).</summary>
public sealed record AuditDetail(
    long Id, DateTimeOffset Timestamp, DateTimeOffset RecordedAt, string Actor, string? ActorName, string? ActorRoles, string? ActorDepartments,
    string Action, string EntityType, string? EntityId, string? Service, Guid? DepartmentId, string? Classification, long? ConfigVersion, string? Reason,
    string? Details, string? SourceAddress, string? RequestId, string? UserAgent);
