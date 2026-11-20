namespace Kalla.Domain.Entities;

/// <summary>
/// A change or privileged read in <c>audit."AuditLog"</c>: the gateway's <c>AuditLogEntry</c> columns plus
/// <see cref="Service"/>, <see cref="Reason"/>, <see cref="ConfigVersion"/>, <see cref="Classification"/> and
/// <see cref="DepartmentId"/> (data-model §2.4). Every service appends; only the admin reads.
/// </summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The admin's <c>oid</c> (fallback <c>sub</c>), or <c>system:&lt;job&gt;</c> for jobs.</summary>
    public required string Actor { get; set; }

    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }

    /// <summary>JSON details; secrets are always masked before being written.</summary>
    public string? Details { get; set; }

    /// <summary>The actor's display name or e-mail when the entry was written; the subject alone may be gone from the IdP later.</summary>
    public string? ActorName { get; set; }

    /// <summary>The actor's admin roles at the time, comma-separated.</summary>
    public string? ActorRoles { get; set; }

    /// <summary>The actor's department codes at the time, comma-separated.</summary>
    public string? ActorDepartments { get; set; }

    /// <summary>The actor's client address behind trusted proxies.</summary>
    public string? SourceAddress { get; set; }

    /// <summary>The sign-in session (a random id from sign-in, never the cookie): ties entries of one session together.</summary>
    public string? SessionId { get; set; }

    /// <summary>The request's trace id, to find it in logs and traces.</summary>
    public string? RequestId { get; set; }

    /// <summary>The browser or client, at most 200 characters.</summary>
    public string? UserAgent { get; set; }

    /// <summary>When the row was written (database transaction start, set by Postgres).</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The writing service: <c>kalla-admin</c>, <c>kalla-api</c>, <c>kalla-worker</c>, <c>kalla-migrate</c>.</summary>
    public string? Service { get; set; }

    /// <summary>The reason an admin gave (required for some actions, e.g. clearing a personal-data flag).</summary>
    public string? Reason { get; set; }

    /// <summary>The config version the change produced or ran under (data-model §5).</summary>
    public long? ConfigVersion { get; set; }

    public string? Classification { get; set; }

    /// <summary>Lets department admins see their departments' entries (admin design §9.1).</summary>
    public Guid? DepartmentId { get; set; }
}
