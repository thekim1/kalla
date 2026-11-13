using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Pipelines.Crawl;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The deployment's ceilings of a host's politeness profile (connectors design §6.6), checked on save (V37): bound from the
/// <c>Crawl</c> section the worker reads too (<c>Crawl:MaxHostRate</c>, <c>Crawl:MaxHostConnections</c>; set them alike on
/// both), and <see cref="MaxRenderPerHost"/> from <c>Render:MaxConcurrent</c>.
/// </summary>
public sealed class CrawlCeilingOptions
{
    public const string SectionName = "Crawl";

    /// <summary>Requests per second; <c>CrawlHosts.RequestsPerSecond</c> is <c>numeric(5,2)</c>.</summary>
    [Range(HostPolitenessRules.MinRequestsPerSecond, 999)]
    public double MaxHostRate { get; set; } = PolitenessCeilings.DefaultMaxHostRate;

    [Range(1, 256)]
    public int MaxHostConnections { get; set; } = PolitenessCeilings.DefaultMaxHostConnections;

    /// <summary><c>Render:MaxConcurrent</c>: the most pages a host's <c>renderPerHost</c> may render at once.</summary>
    [Range(1, 64)]
    public int MaxRenderPerHost { get; set; } = PolitenessCeilings.DefaultMaxRenderPerHost;

    public PolitenessCeilings Ceilings() => new(MaxHostRate, MaxHostConnections, MaxRenderPerHost);
}

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

    /// <summary>
    /// The organisation's display name in the admin UI (header and sign-in page); <c>Umeå kommun</c> by default. Set to an
    /// empty string, the UI words its texts without a name. Shown before sign-in, so it is not a secret.
    /// </summary>
    [StringLength(100)]
    public string? OrganisationName { get; set; } = "Umeå kommun";

    /// <summary>
    /// The gateway admin UI's address for the links (admin design §2.3) when the gateway's context carries no
    /// <c>admin_url</c>; unset by default (#619). Only an absolute http(s) URL is used.
    /// </summary>
    [Url]
    public string? GatewayAdminUrl { get; set; }
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

    /// <summary>
    /// Role name (rag-platform-admin, rag-department-admin, …) to the group names that grant it, separated by ';'. Each
    /// group is a <c>rag-</c> name or an Entra object id (<see cref="OidcOptionsValidator"/>, #413).
    /// </summary>
    public Dictionary<string, string> RoleGroups { get; set; } = [];

    /// <summary>Extra scopes to request, for example "groups".</summary>
    public string[] ExtraScopes { get; set; } = [];

    /// <summary>When set, the <c>tid</c> claim must equal it; a missing <c>tid</c> is refused too (identity design §1.7).</summary>
    public string? TenantId { get; set; }

    /// <summary>Refuse guests: <c>acct</c> = 1, or an <c>idp</c> from another tenant or IdP (identity design §1.7).</summary>
    public bool RefuseGuests { get; set; } = true;
}

/// <summary>
/// The checks data annotations cannot express (#413). In the shared Keycloak realm the group claim carries every group of
/// the account by its short name, the gateway's included (identity design §2.5), so a role group named like a gateway
/// group would grant an Ume.Rag role by accident. A role group must therefore be a <c>rag-</c> name (case-insensitive,
/// as the matching) or an Entra object id (a GUID, unique by nature). Member groups are not checked: they give department
/// scope only, never a role, and are shared with the gateway on purpose (identity design §2.1).
/// </summary>
public sealed class OidcOptionsValidator : IValidateOptions<OidcOptions>
{
    public const string RoleGroupPrefix = "rag-";

    public static bool IsRoleGroupName(string group) =>
        group.StartsWith(RoleGroupPrefix, StringComparison.OrdinalIgnoreCase) && group.Length > RoleGroupPrefix.Length ||
        Guid.TryParse(group, out _);

    public ValidateOptionsResult Validate(string? name, OidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var (role, mapped) in options.RoleGroups)
        {
            foreach (var group in mapped.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IsRoleGroupName(group))
                {
                    failures.Add($"Oidc:RoleGroups:{role} names the group '{group}'. A role group must start with '{RoleGroupPrefix}' " +
                        "or be an Entra object id, so that a gateway group of the same name in the shared realm grants no role (#413).");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
