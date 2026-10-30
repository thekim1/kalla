using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>Audit entity types (admin design §9.2).</summary>
public static class AuditEntityTypes
{
    public const string Client = "client";
}

/// <summary>The <c>Details</c> of an audit entry. Copied.</summary>
public sealed record AuditDetails(object? Before, object? After);

internal static class AuditJson
{
    /// <summary>camelCase and enum names, like the API's own responses.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// Audit snapshots (adapted from the gateway's <c>AuditSnapshots.cs</c>): what an entity's stored settings were before and
/// after a change. They hold only the entity's own columns, so an entry does not depend on which navigations were loaded
/// or on the clock, and never a secret.
/// </summary>
public static class AuditSnapshots
{
    /// <summary>The snapshot record types; the test that no secret column reaches the audit log reflects over them.</summary>
    public static IReadOnlyList<Type> All { get; } = [typeof(ClientAudit)];
}

public sealed record ClientAudit(
    Guid Id, string Name, string Kind, string? OAuthClientId, string ClassificationCeiling, bool StoresHistory, int? ClientRetentionDays,
    IReadOnlyList<string> AllowedOrigins, string ToolPolicy, string? PortalUrl, bool IsEnabled)
{
    public static ClientAudit Of(Client c) => new(c.Id, c.Name, c.Kind, c.OAuthClientId, c.ClassificationCeiling, c.StoresHistory,
        c.ClientRetentionDays, c.AllowedOrigins, c.ToolPolicy, c.PortalUrl, c.IsEnabled);
}
