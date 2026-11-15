using System.Net;
using Ume.Rag.Domain.Services;

namespace Ume.Rag.Domain.Entities;

/// <summary>Client kinds of the registry (data-model §2.1 <c>Clients</c>, R5).</summary>
public static class ClientKinds
{
    public const string OpenWebUi = "open-webui";
    public const string Widget = "widget";
    public const string Integration = "integration";
    public const string EntraApp = "entra-app";
    public const string McpClient = "mcp-client";
    public const string OwnClient = "own-client";
    public const string Teams = "teams";
    public const string Internal = "internal";

    public static IReadOnlyList<string> All { get; } = [OpenWebUi, Widget, Integration, EntraApp, McpClient, OwnClient, Teams, Internal];
}

/// <summary>A registered client (data-model §2.1 <c>Clients</c>): its ceiling caps every assistant it may use.</summary>
public sealed class Client
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Kind { get; set; }

    /// <summary><c>azp</c>/<c>appid</c>/<c>client_id</c> of the client's tokens; strings for Keycloak.</summary>
    public string? OAuthClientId { get; set; }

    public required string ClassificationCeiling { get; set; }
    public bool StoresHistory { get; set; }
    public int? ClientRetentionDays { get; set; }

    /// <summary>An <c>open-webui</c> client's default (chat design §13.3).</summary>
    public const int OpenWebUiFeedbackRetentionDays = 90;

    /// <summary>The admin test panel's client, the deployment's managed <c>internal</c> one (identity design §4.1, admin design §4.9).</summary>
    public const string AdminConsoleName = "admin-console";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary><c>none</c> or <c>read</c> (P3 <c>confirm</c>); tools design §5.1.</summary>
    public string ToolPolicy { get; set; } = "none";

    public string? PortalUrl { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The deployment's own client (identity design §4.1, §5.2; M-17 C4, #712): <c>rag-migrate</c> creates it and keeps its keys:
    /// Open WebUI's one key, the one the deployment generated, and none on <see cref="AdminConsoleName"/>; the admin edits its
    /// settings but not its name, kind or keys. Only the managed <c>open-webui</c> client's users are trusted from their JWT. At most
    /// one per kind and name (<c>IX_Clients_Managed</c>, #712: <c>admin-console</c> and P2's <c>rag-internal</c> are both
    /// <c>internal</c>); rag-migrate keeps one managed <c>open-webui</c> client.
    /// </summary>
    public bool IsManagedByDeployment { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Postgres <c>xmin</c>, the concurrency token of an editable aggregate root (data-model §2.1).</summary>
    public uint Version { get; set; }

    public List<ApiKey> Keys { get; set; } = [];
}

/// <summary>Key kinds; the stored kind must match the key's prefix (identity design §6.1).</summary>
public static class KeyKinds
{
    /// <summary>Secret key, <c>ume-rag-sk-</c>.</summary>
    public const string Secret = "sk";

    /// <summary>Public widget key, <c>ume-rag-pk-</c> (P2).</summary>
    public const string Public = "pk";
}

/// <summary>What a key may reach beyond its client's audiences (data-model §2.1 <c>Keys.Scopes</c>); empty = the client's audiences only.</summary>
public sealed record KeyScopes(Guid[] AssistantIds, Guid[] CollectionIds)
{
    public static KeyScopes None { get; } = new([], []);

    public bool IsEmpty => AssistantIds.Length == 0 && CollectionIds.Length == 0;

    // Value equality over the ids (a record compares arrays by reference).
    public bool Equals(KeyScopes? other) =>
        other is not null && AssistantIds.SequenceEqual(other.AssistantIds) && CollectionIds.SequenceEqual(other.CollectionIds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in AssistantIds)
        {
            hash.Add(id);
        }

        hash.Add('|');
        foreach (var id in CollectionIds)
        {
            hash.Add(id);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// A gateway-style key in <c>config."Keys"</c> (data-model §2.1, identity design §6.1): the gateway's
/// <c>VirtualKey</c> with a client instead of a team's policies. The plain key is shown once; only its HMAC is stored.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Team for attribution (gateway mirror, item 11); no foreign key until <c>Teams</c> exists.</summary>
    public Guid? TeamId { get; set; }

    public string Kind { get; set; } = KeyKinds.Secret;
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Non-secret identifying prefix, e.g. <c>ume-rag-sk-AbC1</c>, shown in UI and logs.</summary>
    public required string Prefix { get; set; }

    /// <summary>Hex HMAC-SHA256 of the full key with the server-side pepper.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Data-Protection-encrypted full key for the audited reveal (platform admin only).</summary>
    public string? EncryptedSecret { get; set; }

    public KeyScopes Scopes { get; set; } = KeyScopes.None;
    public List<string> AllowedOrigins { get; set; } = [];
    public List<string> AllowedIpRanges { get; set; } = [];
    public int? RequestsPerMinute { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when the key was rotated with a grace period; the key works until this instant.</summary>
    public DateTimeOffset? GraceUntil { get; set; }

    public Guid? RotatedToKeyId { get; set; }

    // Copied from the gateway's VirtualKey.GetStatus.
    public KeyStatus GetStatus(DateTimeOffset now)
    {
        if (RevokedAt is { } revoked && revoked <= now)
        {
            return KeyStatus.Revoked;
        }

        if (!IsEnabled)
        {
            return KeyStatus.Disabled;
        }

        if (ExpiresAt is { } expires && expires <= now)
        {
            return KeyStatus.Expired;
        }

        if (GraceUntil is { } grace)
        {
            return grace > now ? KeyStatus.InGracePeriod : KeyStatus.Revoked;
        }

        return KeyStatus.Active;
    }

    public bool IsUsable(DateTimeOffset now) => GetStatus(now) is KeyStatus.Active or KeyStatus.InGracePeriod;

    // Copied from the gateway's VirtualKey (address part): parsed AllowedIpRanges, built once by IndexAllowLists for keys
    // held in a read-only cache.
    private IPNetwork[]? _allowedNetworks;
    private List<string>? _indexedRanges;
    private int _indexedRangeCount;

    /// <summary>
    /// Builds the lookup <see cref="IsAddressAllowed"/> uses, so a cached key answers without parsing. Call once the key is
    /// loaded for read-only use; if the list is replaced or resized afterwards the index is ignored.
    /// </summary>
    public void IndexAllowLists()
    {
        _allowedNetworks = NetworkRanges.ParseValid(AllowedIpRanges);
        _indexedRanges = AllowedIpRanges;
        _indexedRangeCount = AllowedIpRanges.Count;
    }

    /// <summary>
    /// The key's address allow-list. Empty = every address. An unknown address (no connection address) is refused when a
    /// list is set, and so is every address if no entry parses: a broken list must not open the key to everyone.
    /// </summary>
    public bool IsAddressAllowed(IPAddress? address)
    {
        if (AllowedIpRanges.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        var networks = _allowedNetworks is { } index && ReferenceEquals(_indexedRanges, AllowedIpRanges) && _indexedRangeCount == AllowedIpRanges.Count
            ? index
            : NetworkRanges.ParseValid(AllowedIpRanges);
        return NetworkRanges.Contains(networks, address);
    }
}

/// <summary>When a key was last used; split out so key use never bumps the config version (data-model §2.1).</summary>
public sealed class KeyLastUse
{
    public Guid KeyId { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

public enum KeyStatus
{
    Active = 0,
    InGracePeriod = 1,
    Expired = 2,
    Revoked = 3,
    Disabled = 4,
}

public enum KeyRotationMode
{
    /// <summary>Old key stops working immediately (default).</summary>
    RevokeImmediately = 0,

    /// <summary>Old key keeps working for 24 hours so clients can switch.</summary>
    Grace24Hours = 1,
}
