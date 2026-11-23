using System.Text.Json;
using System.Text.Json.Serialization;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>Audit entity types (admin design §9.2).</summary>
public static class AuditEntityTypes
{
    public const string Client = "client";
    public const string Settings = "settings";
    public const string DataProtection = "dataprotection";
    public const string Collection = "collection";
    public const string Department = "department";
    public const string EvaluationQuestion = "evaluationquestion";
    public const string SiteProfile = "siteprofile";
    public const string Assistant = "assistant";
    public const string AssistantTemplate = "assistanttemplate";
    public const string AssistantTest = "assistanttest";
    public const string Source = "source";
    public const string Run = "run";
    public const string Document = "document";
    public const string Key = "key";
    public const string Pipeline = "pipeline";
    public const string Feedback = "feedback";
    public const string EmbeddingProfile = "embedding-profile";
    public const string GatewayModel = "gateway-model";

    /// <summary>The worker's HNSW reindex entries (<c>CheckHnswIndexHandler</c>) name the embedding profile this way.</summary>
    public const string EmbeddingProfileIndex = "embeddingprofile";
}

/// <summary>The <c>Details</c> of an audit entry. Copied.</summary>
public sealed record AuditDetails(object? Before, object? After);

internal static class AuditJson
{
    /// <summary>camelCase and enum names, like the API's own responses.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A DTO as an audit snapshot: its fields without <c>version</c>, which before the save still holds the old token.</summary>
    public static System.Text.Json.Nodes.JsonObject? WithoutVersion(object? dto)
    {
        if (dto is null || JsonSerializer.SerializeToNode(dto, Options) is not System.Text.Json.Nodes.JsonObject json)
        {
            return null;
        }

        json.Remove("version");
        return json;
    }
}

/// <summary>
/// Audit snapshots (adapted from the gateway's <c>AuditSnapshots.cs</c>): what an entity's stored settings were before and
/// after a change. They hold only the entity's own columns, so an entry does not depend on which navigations were loaded
/// or on the clock, and never a secret.
/// </summary>
public static class AuditSnapshots
{
    /// <summary>The snapshot record types; the test that no secret column reaches the audit log reflects over them.</summary>
    public static IReadOnlyList<Type> All { get; } =
    [
        typeof(ClientAudit), typeof(NetworkSettings), typeof(RetentionSettings), typeof(DeletionGuardSettings), typeof(EvaluationSettings),
        typeof(JsonSectionSettings), typeof(PiiSettings), typeof(WorkerSettings), typeof(DataProtectionDto),
        typeof(CollectionAudit), typeof(SourceAudit), typeof(KeyAudit), typeof(AssistantAudit), typeof(DraftAudit), typeof(AssistantTemplateAudit),
        typeof(SignInAudit), typeof(SignInCredentialAudit),
    ];
}

public sealed record ClientAudit(
    Guid Id, string Name, string Kind, string? OAuthClientId, string ClassificationCeiling, bool StoresHistory, int? ClientRetentionDays,
    IReadOnlyList<string> AllowedOrigins, string ToolPolicy, string? PortalUrl, bool IsEnabled)
{
    public static ClientAudit Of(Client c) => new(c.Id, c.Name, c.Kind, c.OAuthClientId, c.ClassificationCeiling, c.StoresHistory,
        c.ClientRetentionDays, c.AllowedOrigins, c.ToolPolicy, c.PortalUrl, c.IsEnabled);
}
