using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;

namespace Ume.Rag.AdminApi;

/// <summary>The organisation mirror (admin design §4.10): departments and teams as synced from the gateway.</summary>
public static class OrganisationEndpoints
{
    public static RouteGroupBuilder MapOrganisation(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var organisation = api.MapGroup("/organisation").WithTags("Organisation");

        // Departments in the user's scope (every one for a platform admin), active first.
        organisation.MapGet("/departments", async (AdminContext admin, CancellationToken ct) =>
            await admin.Departments().AsNoTracking().OrderByDescending(d => d.IsActive).ThenBy(d => d.Name)
                .Select(d => new DepartmentDto(d.Id, d.Name, d.CostCenterCode, d.IsActive, d.SyncedAt,
                    d.Teams.OrderBy(t => t.Name).Select(t => new TeamDto(t.Id, t.Name, t.Code, t.IsActive)).ToList()))
                .ToListAsync(ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("ListDepartments").WithSummary("Departments and teams in scope");

        return api;
    }
}

public sealed record DepartmentDto(Guid Id, string Name, string CostCenterCode, bool IsActive, DateTimeOffset SyncedAt, IReadOnlyList<TeamDto> Teams);

public sealed record TeamDto(Guid Id, string Name, string? Code, bool IsActive);
