using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The client registry (identity design §4.1), a platform object: readable for pickers, writable by platform admins
/// (admin design §3.2). The first resource on the write path; its generic form comes in P1 (§5.8).
/// </summary>
public static class ClientEndpoints
{
    public static RouteGroupBuilder MapClients(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var clients = api.MapGroup("/clients").WithTags("Clients");

        // Pickers show name, kind and level; the clients page also the number of active keys.
        clients.MapGet("", async (AdminContext admin, CancellationToken ct) =>
            {
                var now = admin.Now;
                var keys = admin.Db.Keys;
                // The Active branch of ApiKey.GetStatus: not revoked, enabled, not expired, not in a rotation's grace period.
                return await admin.Db.Clients.AsNoTracking().OrderBy(c => c.Name)
                    .Select(c => new ClientSummary(c.Id, c.Name, c.Kind, c.ClassificationCeiling, c.IsEnabled, c.PortalUrl,
                        keys.Count(k => k.ClientId == c.Id && (k.RevokedAt == null || k.RevokedAt > now) && k.IsEnabled && (k.ExpiresAt == null || k.ExpiresAt > now)
                            && k.GraceUntil == null)))
                    .ToListAsync(ct);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListClients").WithSummary("Registered clients (picker fields and active keys)");

        clients.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
            await admin.Db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } client
                ? await DtoAsync(admin, client, ct)
                : throw AdminFaults.Of(AdminErrorCodes.NotFound))
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetClient").WithSummary("One client and the assistants whose audience includes it");

        // The client and its first key in one transaction, each with its audit entry (admin design §5.9).
        clients.MapPost("", async (ClientCreateRequest request, AdminContext admin, AdminKeyHasher hasher, IDataProtectionProvider protection, CancellationToken ct) =>
            {
                var generated = request.FirstKey is null ? null : hasher.Get().Generate();
                var (client, key) = await admin.WriteAsync(async attempt =>
                {
                    var client = new Client { Name = "", Kind = "", ClassificationCeiling = "" };
                    request.ApplyTo(client, admin);
                    admin.Db.Clients.Add(client);
                    KeyDto? key = null;
                    List<AuditAlso> also = [];
                    if (request.FirstKey is { } first)
                    {
                        var (created, department) = await KeyEndpoints.AddSecretKeyAsync(admin, client, first.Name, first.TeamId, null, generated!, protection, attempt,
                            fieldPrefix: "firstKey.");
                        key = KeyDto.Of(created, client.Name, admin.Now);
                        also.Add(new AuditAlso("create", AuditEntityTypes.Key, created.Id, null, KeyAudit.Of(created)) { DepartmentId = department });
                    }

                    return new AdminChange<(Client, KeyDto?)>((client, key), "create", AuditEntityTypes.Client, client.Id, null, ClientAudit.Of(client))
                    {
                        Classification = client.ClassificationCeiling, Also = also,
                    };
                }, ct);
                return Results.Created($"/api/clients/{client.Id}",
                    new ClientCreatedDto(ClientDto.Of(client, []), key is null ? null : new KeyCreatedDto(key, generated!.PlainText)));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("CreateClient").WithSummary("Register a client, optionally with its first key (the secret shown this once)");

        clients.MapPut("/{id:guid}", async (Guid id, ClientUpdateRequest request, AdminContext admin, CancellationToken ct) =>
            await DtoAsync(admin, await admin.WriteAsync(async attempt =>
            {
                var client = await admin.Db.Clients.SingleOrDefaultAsync(c => c.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                admin.ExpectVersion(client, request.Version);
                var before = ClientAudit.Of(client);
                request.ApplyTo(client, admin);
                return new AdminChange<Client>(client, "update", AuditEntityTypes.Client, client.Id, before, ClientAudit.Of(client))
                {
                    Classification = client.ClassificationCeiling,
                };
            }, ct), ct))
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateClient").WithSummary("Change a client");

        return api;
    }

    /// <summary>
    /// Gives <see cref="ClientRequest"/>'s coded members their allowed values as <c>enum</c> in the OpenAPI document (admin design
    /// §5.9): the generator turns no DataAnnotation into <c>enum</c>, and the lists stay those <see cref="ClientRequest.Validate"/> checks.
    /// </summary>
    public static OpenApiOptions AddClientSchemas(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonPropertyInfo is { } property && property.DeclaringType.IsAssignableTo(typeof(ClientRequest))
                && ClientRequest.AllowedValues.TryGetValue(property.Name, out var values))
            {
                schema.Enum = [.. values.Select(v => (JsonNode)JsonValue.Create(v)!)];
            }

            return Task.CompletedTask;
        });
    }

    private static async Task<ClientDto> DtoAsync(AdminContext admin, Client client, CancellationToken ct)
    {
        var id = client.Id;
        var audience = admin.Db.AssistantAudiences.Where(a => a.Kind == AssistantAudienceKinds.Client && a.SubjectId == id).Select(a => a.AssistantId);
        var assistants = await admin.Db.Assistants.AsNoTracking().Where(a => audience.Contains(a.Id)).OrderBy(a => a.Name)
            .Select(a => new ClientAssistantDto(a.Id, a.Name, a.DepartmentId, a.PublishedVersionId != null)).ToListAsync(ct);
        return ClientDto.Of(client, assistants);
    }
}

/// <summary>
/// A picker row; <see cref="PortalUrl"/> is the client's own UI, linked from the assistant page (admin design §5.4 step 13).
/// <see cref="ActiveKeys"/> counts the client's keys whose status is <c>active</c> now (a rotation's grace period not included).
/// </summary>
public sealed record ClientSummary(Guid Id, string Name, string Kind, string ClassificationCeiling, bool IsEnabled, string? PortalUrl, int ActiveKeys);

/// <summary>A client; <see cref="Assistants"/> are those with a <c>client</c> audience row for it (the drawer's "Used by").</summary>
public sealed record ClientDto(
    Guid Id, string Name, string Kind, string? OAuthClientId, string ClassificationCeiling, bool StoresHistory, int? ClientRetentionDays,
    IReadOnlyList<string> AllowedOrigins, string ToolPolicy, string? PortalUrl, bool IsEnabled, DateTimeOffset UpdatedAt, string? UpdatedBy, string Version,
    IReadOnlyList<ClientAssistantDto> Assistants)
{
    public static ClientDto Of(Client c, IReadOnlyList<ClientAssistantDto> assistants) => new(c.Id, c.Name, c.Kind, c.OAuthClientId, c.ClassificationCeiling,
        c.StoresHistory, c.ClientRetentionDays, c.AllowedOrigins, c.ToolPolicy, c.PortalUrl, c.IsEnabled, c.UpdatedAt, c.UpdatedBy,
        c.Version.ToString(CultureInfo.InvariantCulture), assistants);
}

/// <summary>An assistant whose audience includes the client; <see cref="Published"/> as in the assistants list.</summary>
public sealed record ClientAssistantDto(Guid Id, string Name, Guid DepartmentId, bool Published);

/// <summary>A new client and, when <see cref="ClientCreateRequest.FirstKey"/> was given, its first key with the secret, shown this once.</summary>
public sealed record ClientCreatedDto(ClientDto Client, KeyCreatedDto? Key);

/// <summary>
/// A client's fields. Field rules, and no chat history at <c>sensitive</c> (a field error before the table's CHECK); the other
/// rule across columns (no <c>sensitive</c> MCP client) is the table's CHECK constraint and answers <c>platform_rule</c>.
/// </summary>
public record ClientRequest : AdminRequest, IValidatableObject
{
    /// <summary>The coded members' allowed values by JSON name: what <see cref="Validate"/> accepts and the OpenAPI <c>enum</c>s.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedValues { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["kind"] = ClientKinds.All,
        ["classificationCeiling"] = Classification.All,
        ["toolPolicy"] = ["none", "read"],
    };

    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    [Required]
    public string Kind { get; init; } = "";

    [StringLength(200)]
    public string? OAuthClientId { get; init; }

    [Required]
    public string ClassificationCeiling { get; init; } = "";

    public bool StoresHistory { get; init; }

    [Range(1, 3650)]
    public int? ClientRetentionDays { get; init; }

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public string ToolPolicy { get; init; } = "none";

    [StringLength(500), Url]
    public string? PortalUrl { get; init; }

    public bool IsEnabled { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!ClientKinds.All.Contains(Kind))
        {
            yield return new ValidationResult($"Kind must be one of {string.Join(", ", ClientKinds.All)}.", [nameof(Kind)]);
        }

        if (!Classification.IsValid(ClassificationCeiling))
        {
            yield return new ValidationResult($"The level must be one of {string.Join(", ", Classification.All)}.", [nameof(ClassificationCeiling)]);
        }

        if (!AllowedValues["toolPolicy"].Contains(ToolPolicy))
        {
            yield return new ValidationResult("The tool policy must be none or read.", [nameof(ToolPolicy)]);
        }

        // CK_Clients_History: a client that keeps chat history never reaches sensitive assistants (admin design §5.9).
        if (StoresHistory && ClassificationCeiling == Classification.Sensitive)
        {
            yield return new ValidationResult("A client that keeps chat history cannot be sensitive.", [nameof(StoresHistory)]);
        }

        if (AllowedOrigins.Any(o => !Uri.TryCreate(o, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.PathAndQuery != "/"))
        {
            yield return new ValidationResult("Each allowed origin is an https origin without a path.", [nameof(AllowedOrigins)]);
        }

        // Without a value the feedback importer never prunes Open WebUI's feedback copies (chat design §13.3).
        if (this is ClientUpdateRequest && Kind == ClientKinds.OpenWebUi && ClientRetentionDays is null)
        {
            yield return new ValidationResult("An Open WebUI client keeps its feedback for a number of days.", [nameof(ClientRetentionDays)]);
        }
    }

    public void ApplyTo(Client client, AdminContext admin)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(admin);
        client.Name = Name.Trim();
        client.Kind = Kind;
        client.OAuthClientId = string.IsNullOrWhiteSpace(OAuthClientId) ? null : OAuthClientId.Trim();
        client.ClassificationCeiling = ClassificationCeiling;
        client.StoresHistory = StoresHistory;
        client.ClientRetentionDays = ClientRetentionDays ?? (Kind == ClientKinds.OpenWebUi ? Client.OpenWebUiFeedbackRetentionDays : null);
        client.AllowedOrigins = [.. AllowedOrigins];
        client.ToolPolicy = ToolPolicy;
        client.PortalUrl = PortalUrl;
        client.IsEnabled = IsEnabled;
        client.UpdatedAt = admin.Now;
        client.UpdatedBy = admin.Actor;
    }
}

/// <summary>A new client's fields and, optionally, its first key (admin design §5.9 step 3).</summary>
public sealed record ClientCreateRequest : ClientRequest
{
    /// <summary>A secret key created with the client in the same transaction; null creates none.</summary>
    public FirstKeyRequest? FirstKey { get; init; }
}

/// <summary>The first key's name and, optionally, its team for attribution; scopes, limits and expiry are set on the key later.</summary>
public sealed record FirstKeyRequest : AdminRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    public Guid? TeamId { get; init; }
}

/// <summary>A client's fields and the <c>version</c> the caller read (admin design §4.3).</summary>
public sealed record ClientUpdateRequest : ClientRequest
{
    [Required]
    public string? Version { get; init; }
}
