using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.ServiceDefaults;

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

        // What a platform admin tells the app's owner (admin design §5.9.1): rag-api's public address, when the deployment names it.
        clients.MapGet("/connection", (IOptions<AdminOptions> options) =>
                new ClientConnectionDto(options.Value.ApiPublicUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                        ? url.TrimEnd('/')
                        : null))
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetClientConnection").WithSummary("How an app reaches rag-api: its public address, or null when unset");

        // Pickers show name, kind and level; the clients page also the number of active keys and their last use.
        clients.MapGet("", async (AdminContext admin, CancellationToken ct) =>
            {
                var now = admin.Now;
                var keys = admin.Db.Keys;
                var uses = admin.Db.KeyLastUse;
                // The Active branch of ApiKey.GetStatus: not revoked, enabled, not expired, not in a rotation's grace period.
                return await admin.Db.Clients.AsNoTracking().OrderBy(c => c.Name)
                    .Select(c => new ClientSummary(c.Id, c.Name, c.Kind, c.ClassificationCeiling, c.IsEnabled, c.PortalUrl, c.IsManagedByDeployment,
                        keys.Count(k => k.ClientId == c.Id && (k.RevokedAt == null || k.RevokedAt > now) && k.IsEnabled && (k.ExpiresAt == null || k.ExpiresAt > now)
                            && k.GraceUntil == null),
                        uses.Where(u => keys.Any(k => k.Id == u.KeyId && k.ClientId == c.Id)).Max(u => (DateTimeOffset?)u.LastUsedAt)))
                    .ToListAsync(ct);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListClients").WithSummary("Registered clients (picker fields and active keys)");

        clients.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
            await admin.Db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } client
                ? await DtoAsync(admin, client, ct)
                : throw AdminFaults.Of(AdminErrorCodes.NotFound))
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetClient").WithSummary("One client and the assistants whose audience includes it");

        // The client, the assistants it offers and its first key in one transaction, each with its audit entry (admin design §5.9, §5.9.1).
        clients.MapPost("", async (ClientCreateRequest request, AdminContext admin, AdminKeyHasher hasher, IDataProtectionProvider protection, GatewayModelCheck models,
                CancellationToken ct) =>
            {
                var generated = request.FirstKey is null ? null : hasher.Get().Generate();
                var offered = new Client { Name = "", Kind = "", ClassificationCeiling = "" };
                request.ApplyTo(offered, admin);
                await WarmAsync(admin, models, offered, request.AssistantIds.Distinct().ToList(), _ => true, ct);
                var (client, key) = await admin.WriteAsync(async attempt =>
                {
                    var client = new Client { Name = "", Kind = "", ClassificationCeiling = "" };
                    request.ApplyTo(client, admin);
                    admin.Db.Clients.Add(client);
                    KeyDto? key = null;
                    var also = await SetAssistantsAsync(admin, models, client, request.AssistantIds, [], attempt);
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
                    new ClientCreatedDto(await DtoAsync(admin, client, ct), key is null ? null : new KeyCreatedDto(key, generated!.PlainText)));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("CreateClient").WithSummary("Register a client, optionally with its first key (the secret shown this once)");

        // A changed ceiling or history re-checks every assistant the client offers (admin design §4.5, #700); the gateway lists those
        // checks need are fetched before the write, outside the config version lock (§4.4, #696).
        clients.MapPut("/{id:guid}", async (Guid id, ClientUpdateRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                if (await admin.Db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } current && Revalidates(current, request))
                {
                    request.ApplyTo(current, admin);
                    await WarmAsync(admin, models, current, await Offering(admin, id).ToListAsync(ct), _ => true, ct);
                }

                return await DtoAsync(admin, await admin.WriteAsync(async attempt =>
                {
                    var client = await admin.Db.Clients.SingleOrDefaultAsync(c => c.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    admin.ExpectVersion(client, request.Version);
                    // The deployment's own client stays what the deployment made it (identity design §4.1, §5.2): its kind and its name,
                    // by which rag-migrate finds it (#712), are fixed.
                    if (client.IsManagedByDeployment && request.Kind != client.Kind)
                    {
                        throw AdminFaults.Field("kind", AdminErrorCodes.ClientManagedByDeployment);
                    }

                    if (client.IsManagedByDeployment && request.Name.Trim() != client.Name)
                    {
                        throw AdminFaults.Field("name", AdminErrorCodes.ClientManagedByDeployment);
                    }

                    var before = ClientAudit.Of(client);
                    var revalidate = Revalidates(client, request);
                    var ceilingChanged = request.ClassificationCeiling != client.ClassificationCeiling;
                    request.ApplyTo(client, admin);
                    if (revalidate)
                    {
                        await RecheckAssistantsAsync(admin, models, client, ceilingChanged, attempt);
                    }

                    return new AdminChange<Client>(client, "update", AuditEntityTypes.Client, client.Id, before, ClientAudit.Of(client))
                    {
                        Classification = client.ClassificationCeiling,
                    };
                }, ct), ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateClient").WithSummary("Change a client (a changed ceiling or history re-checks V7, V8, V9 per offered assistant)");

        // The assistants that offer the client, set from the client's side (admin design §5.9.1): each changed assistant's audience
        // is checked and audited as its own audience save.
        clients.MapPut("/{id:guid}/assistants", async (Guid id, ClientAssistantsRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                if (await admin.Db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } offered)
                {
                    var wanted = request.AssistantIds.Distinct().ToList();
                    var before = await Offering(admin, id).ToListAsync(ct);
                    await WarmAsync(admin, models, offered, [.. wanted.Except(before).Concat(before.Except(wanted))], wanted.Contains, ct);
                }

                return await DtoAsync(admin, await admin.WriteAsync(async attempt =>
                {
                    var client = await admin.Db.Clients.SingleOrDefaultAsync(c => c.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    admin.ExpectVersion(client, request.Version);
                    var current = await Offering(admin, id).ToListAsync(attempt);
                    var also = await SetAssistantsAsync(admin, models, client, request.AssistantIds, current, attempt);
                    // The stamp moves the client's xmin, so a concurrent save of the same list conflicts (admin design §4.3).
                    client.UpdatedAt = admin.Now;
                    client.UpdatedBy = admin.Actor;
                    return new AdminChange<Client>(client, "assistants", AuditEntityTypes.Client, client.Id, new { assistantIds = current },
                        new { assistantIds = request.AssistantIds.Distinct().ToList() })
                    {
                        Classification = client.ClassificationCeiling, Also = also,
                    };
                }, ct), ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("SetClientAssistants").WithSummary("The assistants that offer the client (V7, V8, V9 per assistant)");

        return api;
    }

    /// <summary>
    /// Gives the coded string members of the generic forms' requests their allowed values as <c>enum</c> in the OpenAPI document,
    /// so the form shows a select (admin design §5.8, §5.9): <see cref="ClientRequest"/>'s and
    /// <see cref="SiteProfileCreateRequest"/>'s level. The generator turns no DataAnnotation into <c>enum</c>, and the lists stay
    /// those the requests' <c>Validate</c> checks.
    /// </summary>
    public static OpenApiOptions AddCodedValueSchemas(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonPropertyInfo is { } property && AllowedValuesOf(property.DeclaringType) is { } allowed
                && allowed.TryGetValue(property.Name, out var values))
            {
                schema.Enum = [.. values.Select(v => (JsonNode)JsonValue.Create(v)!)];
            }

            return Task.CompletedTask;
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? AllowedValuesOf(Type type) =>
        type.IsAssignableTo(typeof(ClientRequest)) ? ClientRequest.AllowedValues
        : type == typeof(SiteProfileCreateRequest) ? SiteProfileCreateRequest.AllowedValues
        : null;

    /// <summary>
    /// Makes <paramref name="assistantIds"/> the assistants with a <c>client</c> audience row for <paramref name="client"/>
    /// (<paramref name="current"/> are those that have one now; none for a new client). Each assistant that gains or loses the client
    /// gets its audience written by <see cref="AssistantEndpoints.ApplyAudienceAsync"/> and an <c>audiences</c> audit entry; a rule
    /// refusal is a field error on <c>assistantIds</c> that names the assistant.
    /// </summary>
    private static async Task<List<AuditAlso>> SetAssistantsAsync(AdminContext admin, GatewayModelCheck models, Client client, IReadOnlyList<Guid> assistantIds,
        IReadOnlyList<Guid> current, CancellationToken ct)
    {
        var wanted = assistantIds.Distinct().ToList();
        var changed = wanted.Except(current).Concat(current.Except(wanted)).ToList();
        List<AuditAlso> also = [];
        if (changed.Count == 0)
        {
            return also;
        }

        var assistants = await admin.Db.Assistants.Where(a => changed.Contains(a.Id)).OrderBy(a => a.Name).ToListAsync(ct);
        if (assistants.Count != changed.Count)
        {
            throw AdminFaults.Field("assistantIds", AdminErrorCodes.InvalidValue);
        }

        foreach (var assistant in assistants)
        {
            var assistantId = assistant.Id;
            var audience = await OtherClientsAsync(admin, assistantId, client.Id, ct);
            if (wanted.Contains(assistantId))
            {
                audience.Add(client);
            }

            try
            {
                var before = await AssistantEndpoints.ApplyAudienceAsync(admin, models, assistant, audience, ct);
                also.Add(new AuditAlso("audiences", AuditEntityTypes.Assistant, assistantId, new { clientIds = before }, new { clientIds = audience.Select(c => c.Id).ToList() })
                {
                    DepartmentId = assistant.DepartmentId,
                });
            }
            catch (ApiFaultException refusal) when (refusal.Status == StatusCodes.Status409Conflict)
            {
                throw AdminFaults.Field("assistantIds", assistant.Name, refusal);
            }
        }

        return also;
    }

    /// <summary>The client's ceiling or history changes, so the assistants it offers are checked again (admin design §4.5).</summary>
    private static bool Revalidates(Client client, ClientRequest request) =>
        request.ClassificationCeiling != client.ClassificationCeiling || request.StoresHistory != client.StoresHistory;

    /// <summary>
    /// Checks every assistant that offers <paramref name="client"/> again with the client's new ceiling and history (admin design §4.5;
    /// V7, V9, and for a published assistant its rule set and V8 at the gateway; #700), as an audience save of it would; nothing of
    /// the assistant changes. A refusal is a field error that names the assistant: on <c>storesHistory</c> for V9 and when only the
    /// history changed, else on <c>classificationCeiling</c>.
    /// </summary>
    private static async Task RecheckAssistantsAsync(AdminContext admin, GatewayModelCheck models, Client client, bool ceilingChanged, CancellationToken ct)
    {
        var offering = Offering(admin, client.Id);
        var assistants = await admin.Db.Assistants.AsNoTracking().Where(a => offering.Contains(a.Id)).OrderBy(a => a.Name).ToListAsync(ct);
        foreach (var assistant in assistants)
        {
            var audience = await OtherClientsAsync(admin, assistant.Id, client.Id, ct);
            audience.Add(client);
            try
            {
                await AssistantEndpoints.CheckAudienceAsync(admin, models, assistant, audience, ct);
            }
            catch (ApiFaultException refusal) when (refusal.Status == StatusCodes.Status409Conflict)
            {
                var code = refusal.Extensions?.TryGetValue("code", out var c) == true ? c as string : null;
                var field = code == AdminErrorCodes.ClientStoresHistory.Code || (!ceilingChanged && code != AdminErrorCodes.AudienceAboveCeiling.Code)
                    ? "storesHistory"
                    : "classificationCeiling";
                throw AdminFaults.Field(field, assistant.Name, refusal);
            }
        }
    }

    /// <summary>
    /// Fetches, before the write and outside the config version lock (admin design §4.4, #696), the gateway lists V8 and V39 will
    /// check for the published ones of <paramref name="assistantIds"/>: each with its other clients and, where
    /// <paramref name="offers"/> says so, <paramref name="client"/> as the request makes it.
    /// </summary>
    private static async Task WarmAsync(AdminContext admin, GatewayModelCheck models, Client client, List<Guid> assistantIds, Func<Guid, bool> offers,
        CancellationToken ct)
    {
        if (assistantIds.Count == 0)
        {
            return;
        }

        var clientId = client.Id;
        var assistants = await admin.Db.Assistants.AsNoTracking().Where(a => assistantIds.Contains(a.Id) && a.PublishedVersionId != null).ToListAsync(ct);
        var rows = await admin.Db.AssistantAudiences.AsNoTracking()
            .Where(a => assistantIds.Contains(a.AssistantId) && a.Kind == AssistantAudienceKinds.Client && a.SubjectId != clientId)
            .Select(a => new { a.AssistantId, a.SubjectId }).ToListAsync(ct);
        var otherIds = rows.Select(r => r.SubjectId).OfType<Guid>().Distinct().ToList();
        var others = await admin.Db.Clients.AsNoTracking().Where(c => otherIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        await models.WarmAsync(assistants.Select(a =>
        {
            List<Client> audience = [.. rows.Where(r => r.AssistantId == a.Id && r.SubjectId is { } s && others.ContainsKey(s)).Select(r => others[r.SubjectId!.Value])];
            if (offers(a.Id))
            {
                audience.Add(client);
            }

            return (AssistantRules.Ceiling(a, audience), a.GatewayKeyId, a.DepartmentId);
        }), ct);
    }

    /// <summary>The assistants with a <c>client</c> audience row for client <paramref name="clientId"/>.</summary>
    private static IQueryable<Guid> Offering(AdminContext admin, Guid clientId) =>
        admin.Db.AssistantAudiences.Where(a => a.Kind == AssistantAudienceKinds.Client && a.SubjectId == clientId).Select(a => a.AssistantId);

    /// <summary>The clients of the assistant's audience other than <paramref name="clientId"/>, by name.</summary>
    private static Task<List<Client>> OtherClientsAsync(AdminContext admin, Guid assistantId, Guid clientId, CancellationToken ct)
    {
        var others = admin.Db.AssistantAudiences.Where(a => a.AssistantId == assistantId && a.Kind == AssistantAudienceKinds.Client && a.SubjectId != clientId)
            .Select(a => a.SubjectId);
        return admin.Db.Clients.AsNoTracking().Where(c => others.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync(ct);
    }

    private static async Task<ClientDto> DtoAsync(AdminContext admin, Client client, CancellationToken ct)
    {
        var id = client.Id;
        var audience = admin.Db.AssistantAudiences.Where(a => a.Kind == AssistantAudienceKinds.Client && a.SubjectId == id).Select(a => a.AssistantId);
        var assistants = await admin.Db.Assistants.AsNoTracking().Where(a => audience.Contains(a.Id)).OrderBy(a => a.Name)
            .Select(a => new ClientAssistantDto(a.Id, a.Name, a.DepartmentId, a.PublishedVersionId != null, a.Slug, a.Classification)).ToListAsync(ct);
        return ClientDto.Of(client, assistants);
    }
}

/// <summary>
/// A picker row; <see cref="PortalUrl"/> is the client's own UI, linked from the assistant page (admin design §5.4 step 13).
/// <see cref="ActiveKeys"/> counts the client's keys whose status is <c>active</c> now (a rotation's grace period not included);
/// <see cref="LastUsedAt"/> is the latest use of any of its keys, null before the first call.
/// </summary>
public sealed record ClientSummary(Guid Id, string Name, string Kind, string ClassificationCeiling, bool IsEnabled, string? PortalUrl, bool IsManagedByDeployment,
    int ActiveKeys, DateTimeOffset? LastUsedAt);

/// <summary>rag-api's public address for the "How to connect" block (<c>Admin:ApiPublicUrl</c>); null when the deployment names none.</summary>
public sealed record ClientConnectionDto(string? ApiBaseUrl);

/// <summary>A client; <see cref="Assistants"/> are those with a <c>client</c> audience row for it (the drawer's "Used by").</summary>
public sealed record ClientDto(
    Guid Id, string Name, string Kind, string? OAuthClientId, string ClassificationCeiling, bool StoresHistory, int? ClientRetentionDays,
    IReadOnlyList<string> AllowedOrigins, string ToolPolicy, string? PortalUrl, bool IsEnabled, bool IsManagedByDeployment, DateTimeOffset UpdatedAt,
    string? UpdatedBy, string Version, IReadOnlyList<ClientAssistantDto> Assistants)
{
    public static ClientDto Of(Client c, IReadOnlyList<ClientAssistantDto> assistants) => new(c.Id, c.Name, c.Kind, c.OAuthClientId, c.ClassificationCeiling,
        c.StoresHistory, c.ClientRetentionDays, c.AllowedOrigins, c.ToolPolicy, c.PortalUrl, c.IsEnabled, c.IsManagedByDeployment, c.UpdatedAt, c.UpdatedBy,
        c.Version.ToString(CultureInfo.InvariantCulture), assistants);
}

/// <summary>
/// An assistant whose audience includes the client; <see cref="Published"/> as in the assistants list, <see cref="Slug"/> its model
/// name in <c>GET /v1/models</c>.
/// </summary>
public sealed record ClientAssistantDto(Guid Id, string Name, Guid DepartmentId, bool Published, string Slug, string Classification);

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

    /// <summary>The assistants that offer the new client, each given a <c>client</c> audience row in the same transaction (admin design §5.9.1).</summary>
    public IReadOnlyList<Guid> AssistantIds { get; init; } = [];
}

/// <summary>The assistants that offer a client, and the client's <c>version</c> the caller read (admin design §5.9.1).</summary>
public sealed record ClientAssistantsRequest : AdminRequest
{
    public IReadOnlyList<Guid> AssistantIds { get; init; } = [];

    [Required]
    public string? Version { get; init; }
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
