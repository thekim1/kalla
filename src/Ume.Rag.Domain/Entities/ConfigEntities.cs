namespace Ume.Rag.Domain.Entities;

/// <summary>
/// The single row of <c>config."ConfigVersion"</c> (data-model §5). Bumped once per config transaction by the trigger
/// function <c>config.rag_config_changed()</c>; admin transactions lock it <c>FOR UPDATE</c> first, so versions are
/// monotonic in commit order. The <c>LastTransactionId xid8</c> column is written only by the trigger and not mapped.
/// </summary>
public sealed class ConfigVersion
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One changed config row (R7, data-model §5). Written only by the trigger; readers reload by entity type.</summary>
public sealed class ConfigOutboxEntry
{
    public long Id { get; set; }
    public long Version { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }

    /// <summary><c>I</c>, <c>U</c> or <c>D</c>.</summary>
    public required string Operation { get; set; }

    public bool SecurityRelevant { get; set; }
}

/// <summary>
/// An admin BFF session (data-model §2.1 <c>AdminSessions</c>): the cookie's random id and the Data-Protection-encrypted
/// ticket. A read ignores expired rows; sign-out deletes the row; the retention step deletes expired ones.
/// </summary>
public sealed class AdminSession
{
    public required string SessionId { get; set; }
    public required byte[] Value { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Mirror of a gateway department (data-model §2.1), synced from the gateway's <c>/v1/ume/context</c> (admin design §4.10).
/// The id is the gateway's. <see cref="MemberGroups"/> are group-claim values that place a signed-in admin in this
/// department (identity design §2.1): the context's <c>member_groups</c>, never edited in Ume.Rag.
/// </summary>
public sealed class Department
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string CostCenterCode { get; set; }
    public string[] MemberGroups { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; }
    public List<Team> Teams { get; set; } = [];
}

/// <summary>Mirror of a gateway team (data-model §2.1), from <c>/v1/ume/context</c>; <see cref="Code"/> is for display.</summary>
public sealed class Team
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }
    public required string Name { get; set; }
    public string? Code { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>
/// Mirror of an assigned gateway key of the integration (data-model §2.1 <c>GatewayKeys</c>, admin design §4.10), from
/// <c>/v1/ume/context</c> <c>assigned_keys</c>: the key picker's list and the read-only settings on an assistant or knowledge
/// base. The id is the gateway's key id; never a secret. A revoked key's row is deleted.
/// </summary>
public sealed class GatewayKey
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>The key's team's department; null when the gateway names none.</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>The gateway's status: <c>Active</c>, <c>InGracePeriod</c>, <c>Disabled</c>, <c>Expired</c>, …</summary>
    public required string Status { get; set; }

    /// <summary>The key's team and department are active.</summary>
    public bool OwnerActive { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Empty = every model.</summary>
    public string[] AllowedModels { get; set; } = [];

    /// <summary>The key's own residencies (<c>OnPrem</c>, <c>Eu</c>, <c>External</c>); empty = all.</summary>
    public string[] AllowedResidencies { get; set; } = [];

    public required string PiiPolicy { get; set; }
    public required string ContentGuardPolicy { get; set; }
    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>The key's and its team's request policy combined, as the gateway applies it.</summary>
    public int? MaxOutputTokens { get; set; }

    public bool HasSystemPrompt { get; set; }

    /// <summary>The gateway's: false when rag-admin's credential key and this key allow no residency in common.</summary>
    public bool Usable { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}
