using System.ComponentModel.DataAnnotations;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>Bound from the <c>Admin</c> configuration section (adapted from the gateway's; identity design §1.4).</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Requests per minute per signed-in user (or client address before sign-in).</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// Hours a session may last from sign-in, however often it is extended. After that the user signs in at the identity
    /// provider again (which also picks up role changes made there).
    /// </summary>
    [Range(1, 24)]
    public int MaxSessionHours { get; set; } = 12;

    /// <summary>
    /// Runs the nightly retention steps (repo-and-deployment §3). Each batch is idempotent under an advisory lock, so
    /// several instances share or skip the work; <c>false</c> is an off switch.
    /// </summary>
    public bool Retention { get; set; } = true;

    /// <summary>Copies each pipeline version's publish report id from the worker's job result (<see cref="PublishReportLinker"/>); <c>false</c> is an off switch.</summary>
    public bool PublishReportLinker { get; set; } = true;

    /// <summary>How much of an admin's address the audit log and events keep (copied, identity design §1.4): <c>Full</c> by default.</summary>
    public SourceAddressMode AuditSourceAddress { get; set; } = SourceAddressMode.Full;
}

/// <summary>
/// Bound from the <c>Oidc</c> configuration section (the gateway's, adapted; identity design §1.4). Defaults match the
/// bundled Keycloak realm; other identity providers remap claims or map groups to roles.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The identity provider's issuer URL.</summary>
    [Required]
    public string? Authority { get; set; }

    /// <summary>Ume.Rag's own registration (identity design §1.6), never the gateway's <c>ume-admin</c>.</summary>
    [Required]
    public string ClientId { get; set; } = "ume-rag-admin";

    public string? ClientSecret { get; set; }

    public string RoleClaim { get; set; } = "roles";
    public string DepartmentClaim { get; set; } = "departmentCodes";
    public string TeamClaim { get; set; } = "teamCodes";

    /// <summary>The group claim: role groups (<see cref="RoleGroups"/>) and member groups of departments (identity design §2).</summary>
    public string GroupClaim { get; set; } = "groups";

    /// <summary>Role name (rag-platform-admin, rag-department-admin, …) to the group names that grant it, separated by ';'.</summary>
    public Dictionary<string, string> RoleGroups { get; set; } = [];

    /// <summary>Extra scopes to request, for example "groups".</summary>
    public string[] ExtraScopes { get; set; } = [];

    /// <summary>When set, the <c>tid</c> claim must equal it; a missing <c>tid</c> is refused too (identity design §1.7).</summary>
    public string? TenantId { get; set; }

    /// <summary>Refuse guests: <c>acct</c> = 1, or an <c>idp</c> from another tenant or IdP (identity design §1.7).</summary>
    public bool RefuseGuests { get; set; } = true;
}
