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
