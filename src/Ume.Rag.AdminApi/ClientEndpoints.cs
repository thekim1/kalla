using System.ComponentModel.DataAnnotations;
using System.Globalization;
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

        // Pickers show name, kind and level only.
        clients.MapGet("", async (AdminContext admin, CancellationToken ct) =>
            await admin.Db.Clients.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new ClientSummary(c.Id, c.Name, c.Kind, c.ClassificationCeiling, c.IsEnabled)).ToListAsync(ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("ListClients").WithSummary("Registered clients (picker fields)");

        clients.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
            await admin.Db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } client
                ? ClientDto.Of(client)
                : throw AdminFaults.Of(AdminErrorCodes.NotFound))
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetClient").WithSummary("One client");

        clients.MapPost("", async (ClientRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var created = await admin.WriteAsync(_ =>
                {
                    var client = new Client { Name = "", Kind = "", ClassificationCeiling = "" };
                    request.ApplyTo(client, admin);
                    admin.Db.Clients.Add(client);
                    return Task.FromResult(new AdminChange<Client>(client, "create", AuditEntityTypes.Client, client.Id, null, ClientAudit.Of(client))
                    {
                        Classification = client.ClassificationCeiling,
                    });
                }, ct);
                return Results.Created($"/api/clients/{created.Id}", ClientDto.Of(created));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("CreateClient").WithSummary("Register a client");

        clients.MapPut("/{id:guid}", async (Guid id, ClientUpdateRequest request, AdminContext admin, CancellationToken ct) =>
            ClientDto.Of(await admin.WriteAsync(async attempt =>
            {
                var client = await admin.Db.Clients.SingleOrDefaultAsync(c => c.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                admin.ExpectVersion(client, request.Version);
                var before = ClientAudit.Of(client);
                request.ApplyTo(client, admin);
                return new AdminChange<Client>(client, "update", AuditEntityTypes.Client, client.Id, before, ClientAudit.Of(client))
                {
                    Classification = client.ClassificationCeiling,
                };
            }, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateClient").WithSummary("Change a client");

        return api;
    }
}

public sealed record ClientSummary(Guid Id, string Name, string Kind, string ClassificationCeiling, bool IsEnabled);

public sealed record ClientDto(
    Guid Id, string Name, string Kind, string? OAuthClientId, string ClassificationCeiling, bool StoresHistory, int? ClientRetentionDays,
    IReadOnlyList<string> AllowedOrigins, string ToolPolicy, string? PortalUrl, bool IsEnabled, DateTimeOffset UpdatedAt, string? UpdatedBy, string Version)
{
    public static ClientDto Of(Client c) => new(c.Id, c.Name, c.Kind, c.OAuthClientId, c.ClassificationCeiling, c.StoresHistory, c.ClientRetentionDays,
        c.AllowedOrigins, c.ToolPolicy, c.PortalUrl, c.IsEnabled, c.UpdatedAt, c.UpdatedBy, c.Version.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// A client's fields. Field rules only: rules across columns (no history at <c>sensitive</c>, no <c>sensitive</c> MCP
/// client) are the table's CHECK constraints and answer <c>platform_rule</c>.
/// </summary>
public record ClientRequest : AdminRequest, IValidatableObject
{
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

        if (ToolPolicy is not ("none" or "read"))
        {
            yield return new ValidationResult("The tool policy must be none or read.", [nameof(ToolPolicy)]);
        }

        if (AllowedOrigins.Any(o => !Uri.TryCreate(o, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.PathAndQuery != "/"))
        {
            yield return new ValidationResult("Each allowed origin is an https origin without a path.", [nameof(AllowedOrigins)]);
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
        client.ClientRetentionDays = ClientRetentionDays;
        client.AllowedOrigins = [.. AllowedOrigins];
        client.ToolPolicy = ToolPolicy;
        client.PortalUrl = PortalUrl;
        client.IsEnabled = IsEnabled;
        client.UpdatedAt = admin.Now;
        client.UpdatedBy = admin.Actor;
    }
}

/// <summary>A client's fields and the <c>version</c> the caller read (admin design §4.3).</summary>
public sealed record ClientUpdateRequest : ClientRequest
{
    [Required]
    public string? Version { get; init; }
}
