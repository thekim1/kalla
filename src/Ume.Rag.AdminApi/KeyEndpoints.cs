using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;

namespace Ume.Rag.AdminApi;

/// <summary>
/// API keys (identity design §6.4, admin design §4.5 "Clients, keys…"; P1a item 14c, J7), adapted from the gateway's
/// <c>KeyEndpoints.cs</c> at e25b491: create (the secret shown once), update, rotate (immediately or with 24 h grace),
/// reveal/copy (platform admin, audited), revoke. A key belongs to a client (RAG's change from the gateway's team keys) and
/// optionally to a team for attribution. Platform admins manage every key; department admins the keys of teams in their
/// departments (the gateway's <c>KeyTeams</c> pattern). V23: scopes name only assistants and collections of the key team's
/// department. Only secret keys in P1 (<c>pk</c> widget keys are P2).
/// </summary>
public static class KeyEndpoints
{
    public static RouteGroupBuilder MapKeys(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var keys = api.MapGroup("/keys").WithTags("Keys");

        keys.MapGet("", async (Guid? clientId, AdminContext admin, CancellationToken ct) =>
            {
                var query = Managed(admin);
                if (clientId is { } client)
                {
                    query = query.Where(k => k.ClientId == client);
                }

                var items = await query.OrderBy(k => k.Name).ToListAsync(ct);
                return items.Select(k => KeyDto.Of(k, admin.Now)).ToList();
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("ListKeys").WithSummary("Keys the caller manages; never their secrets");

        keys.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) => KeyDto.Of(await FindAsync(admin, id, ct), admin.Now))
            .RequireAuthorization(AdminPolicies.Manage).WithName("GetKey").WithSummary("One key");

        keys.MapPost("", async (KeyCreateRequest request, AdminContext admin, KeyHasher hasher, IDataProtectionProvider protection, CancellationToken ct) =>
            {
                var generated = hasher.Generate();
                var key = await admin.WriteAsync(async attempt =>
                {
                    var client = await admin.Db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.ClientId, attempt)
                        ?? throw AdminFaults.Field("clientId", AdminErrorCodes.InvalidValue);
                    var department = await CheckTeamAsync(admin, request.TeamId, attempt);
                    await CheckScopesAsync(admin, department, request.Scopes, attempt);
                    var key = new ApiKey
                    {
                        ClientId = client.Id, TeamId = request.TeamId, Kind = KeyKinds.Secret, Name = request.Name.Trim(), Prefix = generated.Prefix,
                        KeyHash = generated.Hash, EncryptedSecret = Protector(protection).Protect(generated.PlainText), CreatedAt = admin.Now, CreatedBy = admin.Actor,
                    };
                    Apply(key, request);
                    admin.Db.Keys.Add(key);
                    return new AdminChange<ApiKey>(key, "create", AuditEntityTypes.Key, key.Id, null, KeyAudit.Of(key))
                    {
                        DepartmentId = department, Classification = client.ClassificationCeiling,
                    };
                }, ct);
                return Results.Created($"/api/keys/{key.Id}", new KeyCreatedDto(KeyDto.Of(key, admin.Now), generated.PlainText));
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("CreateKey").WithSummary("Create a key; the response holds its secret, shown this once");

        keys.MapPut("/{id:guid}", async (Guid id, KeyUpdateRequest request, AdminContext admin, CancellationToken ct) =>
                KeyDto.Of(await admin.WriteAsync(async attempt =>
                {
                    var key = await FindAsync(admin, id, attempt, tracked: true);
                    var department = await CheckTeamAsync(admin, key.TeamId, attempt);
                    await CheckScopesAsync(admin, department, request.Scopes, attempt);
                    var before = KeyAudit.Of(key);
                    key.Name = request.Name.Trim();
                    Apply(key, request);
                    return new AdminChange<ApiKey>(key, "update", AuditEntityTypes.Key, key.Id, before, KeyAudit.Of(key)) { DepartmentId = department };
                }, ct), admin.Now))
            .RequireAuthorization(AdminPolicies.Manage).WithName("UpdateKey").WithSummary("Change a key's name, scopes, limits or expiry");

        keys.MapPost("/{id:guid}/rotate", async (Guid id, RotateRequest request, AdminContext admin, KeyHasher hasher, IDataProtectionProvider protection, CancellationToken ct) =>
            {
                var generated = hasher.Generate();
                var (replacement, old) = await admin.WriteAsync(async attempt =>
                {
                    var old = await FindAsync(admin, id, attempt, tracked: true);
                    if (!old.IsUsable(admin.Now) || old.RotatedToKeyId is not null)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.KeyNotRotatable);
                    }

                    var department = await CheckTeamAsync(admin, old.TeamId, attempt);
                    var before = KeyAudit.Of(old);
                    var replacement = KeyRotation.Rotate(old, generated, request.Mode, admin.Now, admin.Actor);
                    replacement.EncryptedSecret = Protector(protection).Protect(generated.PlainText);
                    admin.Db.Keys.Add(replacement);
                    return new AdminChange<(ApiKey, ApiKey)>((replacement, old), "rotate", AuditEntityTypes.Key, old.Id, before,
                        new { old = KeyAudit.Of(old), replacement = KeyAudit.Of(replacement) }) { DepartmentId = department };
                }, ct);
                return new KeyRotatedDto(KeyDto.Of(replacement, admin.Now), generated.PlainText, KeyDto.Of(old, admin.Now));
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("RotateKey").WithSummary("Replace a key; the response holds the new secret, shown this once");

        // Platform admins only; every reveal or copy is audited (identity §6.4).
        keys.MapPost("/{id:guid}/reveal", async (Guid id, RevealRequest request, AdminContext admin, IDataProtectionProvider protection, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var key = await FindAsync(admin, id, attempt);
                    if (key.RevokedAt is not null)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.KeyRevoked);
                    }

                    var secret = key.EncryptedSecret is { } stored ? Protector(protection).Unprotect(stored) : throw AdminFaults.Of(AdminErrorCodes.KeySecretUnavailable);
                    return new AdminChange<KeySecretDto>(new KeySecretDto(secret), request.Purpose == RevealPurpose.Copy ? "copy" : "reveal", AuditEntityTypes.Key, key.Id,
                        null, new { key.Name, key.Prefix });
                }, ct))
            .RequireAuthorization(AdminPolicies.Admin).WithName("RevealKey").WithSummary("A key's secret (platform admin; audited)");

        keys.MapPost("/{id:guid}/revoke", async (Guid id, AdminContext admin, CancellationToken ct) =>
                KeyDto.Of(await admin.WriteAsync(async attempt =>
                {
                    var key = await FindAsync(admin, id, attempt, tracked: true);
                    var before = KeyAudit.Of(key);
                    KeyRotation.Revoke(key, admin.Now);
                    return new AdminChange<ApiKey>(key, "revoke", AuditEntityTypes.Key, key.Id, before, KeyAudit.Of(key))
                    {
                        DepartmentId = await CheckTeamAsync(admin, key.TeamId, attempt),
                    };
                }, ct), admin.Now))
            .RequireAuthorization(AdminPolicies.Manage).WithName("RevokeKey").WithSummary("Revoke a key now");

        return api;
    }

    /// <summary>Every key for a platform admin; else the keys of teams in the caller's departments (gateway <c>KeyTeams</c>).</summary>
    private static IQueryable<ApiKey> Managed(AdminContext admin)
    {
        if (admin.IsPlatformAdmin)
        {
            return admin.Db.Keys;
        }

        var departments = admin.Departments().Select(d => d.Id);
        var teams = admin.Db.Teams.Where(t => departments.Contains(t.DepartmentId)).Select(t => t.Id);
        return admin.Db.Keys.Where(k => k.TeamId != null && teams.Contains(k.TeamId.Value));
    }

    private static async Task<ApiKey> FindAsync(AdminContext admin, Guid id, CancellationToken ct, bool tracked = false)
    {
        var query = tracked ? Managed(admin) : Managed(admin).AsNoTracking();
        return await query.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }

    /// <summary>The team's department; a department admin must name a team of theirs, a platform admin may give none.</summary>
    private static async Task<Guid?> CheckTeamAsync(AdminContext admin, Guid? teamId, CancellationToken ct)
    {
        if (teamId is not { } team)
        {
            return admin.IsPlatformAdmin ? null : throw AdminFaults.Field("teamId", AdminErrorCodes.NotInScope);
        }

        var departments = admin.Departments().Select(d => d.Id);
        return await admin.Db.Teams.AsNoTracking().Where(t => t.Id == team && departments.Contains(t.DepartmentId)).Select(t => (Guid?)t.DepartmentId).SingleOrDefaultAsync(ct)
            ?? throw AdminFaults.Field("teamId", AdminErrorCodes.NotInScope);
    }

    /// <summary>V23: scoped assistants and collections belong to the key team's department (any, for a team-less platform key).</summary>
    private static async Task CheckScopesAsync(AdminContext admin, Guid? department, KeyScopesRequest? scopes, CancellationToken ct)
    {
        if (scopes is null)
        {
            return;
        }

        var assistants = scopes.AssistantIds.Distinct().ToList();
        var collections = scopes.CollectionIds.Distinct().ToList();
        var foundAssistants = await admin.Db.Assistants.AsNoTracking().CountAsync(a => assistants.Contains(a.Id) && (department == null || a.DepartmentId == department), ct);
        var foundCollections = await admin.Db.Collections.AsNoTracking()
            .CountAsync(c => collections.Contains(c.Id) && c.DeletedAt == null && (department == null || c.DepartmentId == department), ct);
        if (foundAssistants != assistants.Count || foundCollections != collections.Count)
        {
            throw AdminFaults.Field("scopes", AdminErrorCodes.KeyScopeInvalid);
        }
    }

    private static void Apply(ApiKey key, KeyFields request)
    {
        key.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        key.ExpiresAt = request.ExpiresAt?.ToUniversalTime();
        if (request.Scopes is { } scopes)
        {
            key.Scopes = new KeyScopes([.. scopes.AssistantIds.Distinct()], [.. scopes.CollectionIds.Distinct()]);
        }

        if (request.AllowedIpRanges is not null)
        {
            NetworkRanges.Normalize(request.AllowedIpRanges, out var ranges);
            key.AllowedIpRanges = [.. ranges];
        }

        key.RequestsPerMinute = request.RequestsPerMinute;
        key.IsEnabled = request.IsEnabled;
    }

    private static IDataProtector Protector(IDataProtectionProvider protection) => protection.CreateProtector("Ume.Rag.AdminApi.KeySecret.v1");
}

public record KeyFields : AdminRequest, IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    [StringLength(1000)]
    public string? Description { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Null keeps the scopes (none on create: the client's audiences only).</summary>
    public KeyScopesRequest? Scopes { get; init; }

    /// <summary>Null keeps the ranges.</summary>
    [MaxLength(NetworkRanges.MaxEntries)]
    public IReadOnlyList<string>? AllowedIpRanges { get; init; }

    [Range(1, 100_000)]
    public int? RequestsPerMinute { get; init; }

    public bool IsEnabled { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedIpRanges is not null && NetworkRanges.Normalize(AllowedIpRanges, out _) is { } error)
        {
            yield return new ValidationResult(error, [nameof(AllowedIpRanges)]);
        }
    }
}

public sealed record KeyCreateRequest : KeyFields
{
    [Required]
    public Guid ClientId { get; init; }

    /// <summary>The team for attribution and management; required for department admins.</summary>
    public Guid? TeamId { get; init; }
}

public sealed record KeyUpdateRequest : KeyFields;

public sealed record KeyScopesRequest
{
    [MaxLength(100)]
    public IReadOnlyList<Guid> AssistantIds { get; init; } = [];

    [MaxLength(100)]
    public IReadOnlyList<Guid> CollectionIds { get; init; } = [];
}

public sealed record RotateRequest : AdminRequest
{
    [EnumDataType(typeof(KeyRotationMode))]
    public KeyRotationMode Mode { get; init; } = KeyRotationMode.RevokeImmediately;
}

public enum RevealPurpose
{
    Reveal,
    Copy,
}

public sealed record RevealRequest : AdminRequest
{
    [EnumDataType(typeof(RevealPurpose))]
    public RevealPurpose Purpose { get; init; } = RevealPurpose.Reveal;
}

public sealed record KeyDto(
    Guid Id, Guid ClientId, Guid? TeamId, string Kind, string Name, string? Description, string Prefix, string Status, IReadOnlyList<Guid> AssistantIds,
    IReadOnlyList<Guid> CollectionIds, IReadOnlyList<string> AllowedIpRanges, int? RequestsPerMinute, bool IsEnabled, DateTimeOffset CreatedAt, string? CreatedBy,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? GraceUntil, Guid? RotatedToKeyId)
{
    public static KeyDto Of(ApiKey k, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(k);
        return new(k.Id, k.ClientId, k.TeamId, k.Kind, k.Name, k.Description, k.Prefix,
            System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(k.GetStatus(now).ToString()), k.Scopes.AssistantIds, k.Scopes.CollectionIds,
            k.AllowedIpRanges, k.RequestsPerMinute, k.IsEnabled, k.CreatedAt, k.CreatedBy, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.RotatedToKeyId);
    }
}

public sealed record KeyCreatedDto(KeyDto Key, string Secret);

public sealed record KeyRotatedDto(KeyDto Key, string Secret, KeyDto Previous);

public sealed record KeySecretDto(string Secret);

/// <summary>A key's audit snapshot: never the hash or the secret.</summary>
public sealed record KeyAudit(
    Guid Id, Guid ClientId, Guid? TeamId, string Name, string? Description, string Prefix, IReadOnlyList<Guid> AssistantIds, IReadOnlyList<Guid> CollectionIds,
    IReadOnlyList<string> AllowedIpRanges, int? RequestsPerMinute, bool IsEnabled, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? GraceUntil,
    Guid? RotatedToKeyId)
{
    public static KeyAudit Of(ApiKey k) => new(k.Id, k.ClientId, k.TeamId, k.Name, k.Description, k.Prefix, k.Scopes.AssistantIds, k.Scopes.CollectionIds,
        k.AllowedIpRanges, k.RequestsPerMinute, k.IsEnabled, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.RotatedToKeyId);
}
