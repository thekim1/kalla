namespace Kalla.Domain.Access;

/// <summary>Who the subject is (identity design §3); stored as <c>entra-user</c>, <c>owui-user</c>, <c>application</c>, <c>anonymous</c>.</summary>
public enum SubjectKind { EntraUser, OwuiUser, Application, Anonymous }

/// <summary>How the caller proved it (identity design §3).</summary>
public enum CredentialKind { SecretKey, PublicKey, AccessToken, ServiceKey, DownloadGrant }

/// <summary>What the caller wants to do (identity design §4.2).</summary>
public enum RagAction { ListModels, Chat, Search, ReadDocument, McpCall, ToolCall, Feedback }

/// <summary>The kind of resource a decision is about.</summary>
public enum ResourceKind { Assistant, Collection }

/// <summary>A resource of a decision: an assistant or a collection, by id.</summary>
public sealed record ResourceRef(ResourceKind Kind, Guid Id)
{
    public static ResourceRef Assistant(Guid id) => new(ResourceKind.Assistant, id);
    public static ResourceRef Collection(Guid id) => new(ResourceKind.Collection, id);
}

/// <summary>
/// A client of the registry (<c>config."Clients"</c>, identity design §4.1) as the snapshot holds it;
/// <see cref="IsManagedByDeployment"/>: the deployment's own client (§5.2), the only <c>open-webui</c> client whose users are trusted.
/// </summary>
public sealed record ClientEntry(Guid Id, string Name, string Kind, string Ceiling, bool StoresHistory, bool IsEnabled, bool IsManagedByDeployment = false);

/// <summary>
/// The key a principal authenticated with: id, client, kind (<c>sk</c>/<c>pk</c>), team, scopes and
/// <c>Keys.RequestsPerMinute</c> (the chat message limit when the key itself is the subject; chat design §4.1 step 4).
/// </summary>
public sealed record KeyEntry(Guid Id, Guid ClientId, string Kind, Guid? TeamId, IReadOnlyList<Guid> AssistantScopes, IReadOnlyList<Guid> CollectionScopes,
    int? RequestsPerMinute);

/// <summary>Departments and groups from the token's claims; <see cref="Known"/> is false for an overage token (identity §4.2).</summary>
public sealed record MembershipFacts(IReadOnlySet<Guid> Departments, IReadOnlySet<string> Groups, bool Known)
{
    /// <summary>No membership claims at all (keys, Open WebUI users in P1): only <c>client</c> audiences can match.</summary>
    public static MembershipFacts None { get; } = new(new HashSet<Guid>(), new HashSet<string>(StringComparer.Ordinal), true);
}

/// <summary>
/// One data-plane caller (identity design §3), built by the api's authentication. <see cref="DisplayName"/> and
/// <see cref="Email"/> are for display only: never logged or stored.
/// </summary>
public sealed record RagPrincipal(
    SubjectKind Kind,
    string SubjectId,
    string? DisplayName,
    string? Email,
    ClientEntry Client,
    CredentialKind Credential,
    KeyEntry? Key,
    IReadOnlySet<string> Permissions,
    MembershipFacts Membership,
    long ConfigVersion);

/// <summary>Stable deny codes of identity design §4.3 (also the problem's <c>code</c> and the 5002 reason).</summary>
public static class DenyReasons
{
    public const string None = "";
    public const string ConfigUnconfirmed = "config_unconfirmed";
    public const string ClientUnknown = "client_unknown";
    public const string ClientDisabled = "client_disabled";
    public const string ClientNotAllowed = "client_not_allowed";
    public const string CredentialNotAllowed = "credential_not_allowed";
    public const string SubjectNotAllowed = "subject_not_allowed";
    public const string PermissionMissing = "permission_missing";
    public const string NotFound = "not_found";
    public const string KeyScope = "key_scope";
    public const string CeilingExceeded = "ceiling_exceeded";
    public const string AnonymousNotAllowed = "anonymous_not_allowed";
    public const string ClientStoresHistory = "client_stores_history";
    public const string AudienceMismatch = "audience_mismatch";
    public const string MembershipUnavailable = "membership_unavailable";
}

/// <summary>
/// The outcome of <see cref="IAccessPolicy.Decide"/> (identity design §4.2): the first failing step's reason and status,
/// or allowed with the resource's level and the residency ceiling model calls for it carry. <c>Step</c> is the stable id
/// of the step that decided (<c>0</c>, <c>1</c>, <c>1a</c>, … <c>11</c>; <c>ok</c> when allowed).
/// </summary>
public sealed record AccessDecision(bool Allowed, string Reason, int HttpStatus, string Step, string? EffectiveLevel, Residency? MaxResidency, long ConfigVersion)
{
    public static AccessDecision Deny(string step, string reason, int status, long version) => new(false, reason, status, step, null, null, version);
}

/// <summary>Data levels in order: <c>public</c> &lt; <c>internal</c> &lt; <c>sensitive</c>.</summary>
public static class Levels
{
    /// <summary>The level's rank (0, 1, 2), or null for a value that is not a level (treated as disabled, identity §4.5 rule 3).</summary>
    public static int? Rank(string? level) => level switch
    {
        Classification.Public => 0,
        Classification.Internal => 1,
        Classification.Sensitive => 2,
        _ => null,
    };

    /// <summary>Whether <paramref name="level"/> is at most <paramref name="ceiling"/>; false when either is not a level.</summary>
    public static bool AtMost(string? level, string? ceiling) => Rank(level) is { } l && Rank(ceiling) is { } c && l <= c;

    /// <summary>The higher of two levels (both must be levels).</summary>
    public static string Max(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>Every level up to and including <paramref name="ceiling"/>.</summary>
    public static IReadOnlyList<string> UpTo(string ceiling) => [.. Classification.All.Where(l => AtMost(l, ceiling))];
}
