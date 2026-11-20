using System.Security.Claims;

namespace Kalla.AdminApi;

/// <summary>A department's member groups, from the gateway mirror (<c>config."Departments"</c>).</summary>
public sealed record DepartmentGroups(string CostCenterCode, bool IsActive, string[] MemberGroups);

/// <summary>
/// Department scope from member groups, claims only (identity design §2.3, the same code shape as the gateway's feature C).
/// After claim mapping: every active department whose member groups include a value of the group claim
/// (case-insensitive) adds its cost-centre code as a <c>departmentCodes</c> claim, unless the claim already carries it.
/// Membership never grants a role, and a token without a group claim (absent or overage) adds nothing.
/// </summary>
public static class DepartmentMembershipResolver
{
    /// <summary>Adds the departments from member groups to <paramref name="principal"/>; returns the codes it added.</summary>
    public static IReadOnlyList<string> Apply(ClaimsPrincipal principal, IEnumerable<DepartmentGroups> departments, OidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(departments);
        ArgumentNullException.ThrowIfNull(options);
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return [];
        }

        var fromClaim = identity.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var groups = identity.FindAll(options.GroupClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0)
        {
            return [];
        }

        var added = new List<string>();
        foreach (var department in departments)
        {
            if (department.IsActive && !fromClaim.Contains(department.CostCenterCode) && !added.Contains(department.CostCenterCode) &&
                department.MemberGroups.Any(groups.Contains))
            {
                identity.AddClaim(new Claim(AdminClaims.DepartmentCodes, department.CostCenterCode));
                identity.AddClaim(new Claim(AdminClaims.GroupDepartmentCodes, department.CostCenterCode));
                added.Add(department.CostCenterCode);
            }
        }

        return added;
    }
}
