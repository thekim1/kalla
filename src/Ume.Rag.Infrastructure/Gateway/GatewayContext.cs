namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>
/// <c>GET /v1/ume/context</c> as the gateway's contract describes it (<c>UmeContext</c>, <c>contracts/gateway/integrations.json</c>
/// at gateway <c>v0.10.0</c>; gateway-changes §5.5), with the fields Ume.Rag reads. Unknown fields are ignored: the gateway
/// adds fields, features and scopes over time.
/// </summary>
public sealed record GatewayContext(
    GatewayContextInfo Gateway,
    GatewayContextKey Key,
    IReadOnlyList<GatewayContextDepartment> Departments,
    IReadOnlyList<GatewayContextTeam> Teams,
    IReadOnlyList<GatewayAssignedKey> AssignedKeys)
{
    /// <summary>The gateway lists every department and team (feature <c>organisation</c>).</summary>
    public const string OrganisationFeature = "organisation";

    /// <summary>The departments carry <c>member_groups</c> under <see cref="OrganisationRead"/> (gateway v0.10.0).</summary>
    public const string MemberGroupsFeature = "member-groups";

    /// <summary>The integration scope that gives the whole organisation in the context.</summary>
    public const string OrganisationRead = "organisation.read";

    /// <summary>The gateway supports the feature (features are only added, gateway-changes §5.5).</summary>
    public bool Supports(string feature) => Gateway.Features.Contains(feature, StringComparer.Ordinal);
}

/// <summary>The gateway's version, admin UI address and features.</summary>
public sealed record GatewayContextInfo(string? Version, string? AdminUrl, IReadOnlyList<string> Features);

/// <summary>The presented key: here the service's credential key.</summary>
public sealed record GatewayContextKey(Guid Id, string Name, string IntegrationRole, IReadOnlyList<string> IntegrationScopes, IReadOnlyList<string> AllowedResidencies);

/// <summary>A department; <see cref="MemberGroups"/> only under <c>organisation.read</c> (feature <c>member-groups</c>).</summary>
public sealed record GatewayContextDepartment(Guid Id, string Name, string CostCenterCode, bool IsActive, IReadOnlyList<string>? MemberGroups);

public sealed record GatewayContextTeam(Guid Id, Guid DepartmentId, string Name, string? Code, bool IsActive);

/// <summary>An assigned key of the integration that is not revoked (the contract's <c>AssignedKey</c>); never a secret.</summary>
public sealed record GatewayAssignedKey(
    Guid Id,
    string Name,
    Guid TeamId,
    Guid? DepartmentId,
    string Status,
    bool OwnerActive,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<string> AllowedResidencies,
    bool Usable,
    string PiiPolicy,
    string ContentGuardPolicy,
    int? RequestsPerMinute,
    int? TokensPerMinute,
    int? MaxConcurrentRequests,
    int? MaxOutputTokens,
    bool HasSystemPrompt);

/// <summary>A context answer and its <c>ETag</c>; <see cref="Context"/> is null for <c>304 Not Modified</c>.</summary>
public sealed record GatewayContextAnswer(GatewayContext? Context, string? ETag);
