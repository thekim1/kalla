using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// V8 (admin design §4.6, identity §4.7, gateway-changes §13.4): a model is servable at a ceiling when the mirror knows it
/// with a residency within the ceiling, and the gateway's <c>/v1/models</c> called with that level's key and ceiling lists
/// it. Called on assistant publish and on a level or audience save (item 14); P1 sends no <c>x-ume-on-behalf-of</c>.
/// The ceiling is lowered to rag-admin's credential key's residencies first, as chat lowers it (#237, M-17 C9).
/// </summary>
/// <remarks>
/// Scoped, so one request asks the gateway once per ceiling and key (#696): every list it gets, or the failure, is kept for the
/// request. An endpoint calls <see cref="WarmAsync"/> before its write, outside the config version lock (admin design §4.4), so
/// the checks under the lock read the lists it fetched; a list it did not warm is still asked for under the lock.
/// </remarks>
public sealed class GatewayModelCheck(RagDbContext db, GatewayApi gateway, GatewayCeiling serviceCeiling, GatewayFeatures features)
{
    private readonly Dictionary<(Residency Ceiling, Guid? AsKey), Task<IReadOnlySet<string>>> _listed = [];

    /// <summary>The gateway acts as keys (feature <c>act-as-key</c> in this service's context, gateway phase 2; B-43).</summary>
    public bool ActsAsKeys => features.ActAsKey;

    /// <summary>
    /// Fetches, in parallel, the lists <see cref="EnsureServableAsync"/> will need for each model check at a ceiling with the gateway
    /// key of an object of a department (and <see cref="KeyModelsAsync"/> for the same pair). A key is only used when it is a usable
    /// key of that department, as the checks under the lock refuse any other before asking the gateway (V39); such a check is not
    /// warmed. A failure is kept for the check that needs the list and not thrown here, so a pair warmed but not checked costs one
    /// call and nothing else.
    /// </summary>
    public async Task WarmAsync(IEnumerable<(Residency Ceiling, Guid? GatewayKeyId, Guid DepartmentId)> checks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var wanted = checks.ToList();
        var keyIds = ActsAsKeys ? wanted.Select(c => c.GatewayKeyId).OfType<Guid>().Distinct().ToList() : [];
        var keys = keyIds.Count == 0
            ? new Dictionary<Guid, GatewayKey>()
            : await db.GatewayKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, cancellationToken);
        List<Task> pending = [];
        foreach (var (ceiling, keyId, departmentId) in wanted)
        {
            var asKey = ActsAsKeys ? keyId : null;
            if (asKey is { } id && !(keys.TryGetValue(id, out var key) && key.DepartmentId == departmentId && GatewayKeyRules.UnusableReason(key) is null))
            {
                continue;
            }

            pending.Add(ListedAsync(await serviceCeiling.CapAsync(ceiling, cancellationToken), asKey, cancellationToken));
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Kept in the memo: the check under the lock maps it to its own refusal when it needs that list.
        }
    }

    /// <summary>
    /// <see cref="WarmAsync"/> for a V39 check alone (<see cref="KeyModelsAsync"/>), which asks the gateway only while it acts as
    /// keys and the object has a key.
    /// </summary>
    public Task WarmKeyAsync(Residency ceiling, Guid? gatewayKeyId, Guid departmentId, CancellationToken cancellationToken) =>
        ActsAsKeys && gatewayKeyId is not null ? WarmAsync([(ceiling, gatewayKeyId, departmentId)], cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Throws the V8 fault (<c>model_residency</c>, <c>model_not_listed</c>, <c>gateway_key_missing</c>,
    /// <c>gateway_unavailable</c>) unless servable. With <paramref name="gatewayKeyId"/> and while the gateway acts as keys,
    /// the list is the object's key's (admin §4.6 V8, B-43): a refusal of the key is <c>gateway_key_invalid</c>, a missing
    /// echo <c>gateway_unavailable</c> (event 5011 is logged by the handler).
    /// </summary>
    public async Task EnsureServableAsync(string model, Residency ceiling, CancellationToken cancellationToken, Guid? gatewayKeyId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ceiling = await serviceCeiling.CapAsync(ceiling, cancellationToken);
        var parameters = new Dictionary<string, string> { ["model"] = model, ["ceiling"] = ResidencyCeiling.ToHeader(ceiling) };
        var row = await db.GatewayModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == model, cancellationToken);
        if (row is null || !row.Residencies.Any(r => ResidencyCeiling.TryParse(r, out var residency) && residency <= ceiling))
        {
            throw AdminFaults.Of(AdminErrorCodes.ModelResidency, parameters);
        }

        var listed = await ListAsync(ceiling, ActsAsKeys ? gatewayKeyId : null, parameters, cancellationToken);
        if (!listed.Contains(model))
        {
            throw AdminFaults.Of(AdminErrorCodes.ModelNotListed, parameters);
        }
    }

    /// <summary>
    /// V39's model part as the key (admin §4.6, B-43): while the gateway acts as keys, <c>GET /v1/models</c> as
    /// <paramref name="key"/> at <paramref name="ceiling"/>, and each model the object uses that it does not list is named in
    /// <c>gateway_key_model</c>; a refusal of the key is <c>gateway_key_invalid</c>. Empty when the gateway does not act as keys.
    /// </summary>
    public async Task<IReadOnlyList<RuleProblem>> KeyModelsAsync(GatewayKey key, Residency ceiling, IEnumerable<string?> models, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(models);
        if (!ActsAsKeys)
        {
            return [];
        }

        ceiling = await serviceCeiling.CapAsync(ceiling, cancellationToken);
        IReadOnlySet<string> listed;
        try
        {
            listed = await ListedAsync(ceiling, key.Id, cancellationToken);
        }
        catch (GatewayKeyRefusedException refused)
        {
            return [new(AdminErrorCodes.GatewayKeyInvalid, new Dictionary<string, string> { ["key"] = key.Name, ["reason"] = refused.Code }, [key.Id.ToString()])];
        }
        catch (GatewayKeyMissingException)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayKeyMissing, new Dictionary<string, string> { ["model"] = "", ["ceiling"] = ResidencyCeiling.ToHeader(ceiling) });
        }
        catch (Exception ex) when (ex is GatewayUnavailableException or GatewayRefusedException or GatewayResidencyException or GatewayCallRefusedException
                                       or GatewayDelegationEchoException)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayUnavailable, new Dictionary<string, string> { ["model"] = "", ["ceiling"] = ResidencyCeiling.ToHeader(ceiling) });
        }

        var missing = models.OfType<string>().Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).Where(m => !listed.Contains(m))
            .Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0 ? [] : [new(AdminErrorCodes.GatewayKeyModel, new Dictionary<string, string> { ["key"] = key.Name }, missing)];
    }

    private async Task<IReadOnlySet<string>> ListAsync(Residency ceiling, Guid? asKey, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await ListedAsync(ceiling, asKey, cancellationToken);
        }
        catch (GatewayKeyMissingException)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayKeyMissing, parameters);
        }
        catch (GatewayKeyRefusedException refused)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayKeyInvalid,
                new Dictionary<string, string>(parameters) { ["key"] = parameters.GetValueOrDefault("key") ?? refused.GatewayKeyId.ToString(), ["reason"] = refused.Code });
        }
        catch (Exception ex) when (ex is GatewayUnavailableException or GatewayRefusedException or GatewayResidencyException or GatewayCallRefusedException
                                       or GatewayDelegationEchoException)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayUnavailable, parameters);
        }
    }

    /// <summary>The gateway's list at the capped <paramref name="ceiling"/> as <paramref name="asKey"/>, asked once per request.</summary>
    private Task<IReadOnlySet<string>> ListedAsync(Residency ceiling, Guid? asKey, CancellationToken cancellationToken)
    {
        if (!_listed.TryGetValue((ceiling, asKey), out var listed))
        {
            listed = gateway.ListModelsAsync(ceiling, cancellationToken, asKey);
            _listed[(ceiling, asKey)] = listed;
        }

        return listed;
    }
}

/// <summary>
/// Which mirror models the platform's key may use (#447, admin design §4.5): the gateway's <c>/v1/models</c> at a ceiling
/// (lowered to rag-admin's credential key's residencies), cached 5 minutes per ceiling. Null when the gateway could not be asked (no key
/// for the level, unreachable, refused); a failure is not cached, so the next picker load asks again.
/// </summary>
public sealed class GatewayModelAvailability(GatewayApi gateway, IOptions<GatewayOptions> options, GatewayCeiling serviceCeiling, TimeProvider time,
    GatewayFeatures features)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Residency, Guid?), (DateTimeOffset At, IReadOnlySet<string> Models)> _cache = new();

    /// <summary>
    /// The models listed at <paramref name="ceiling"/>; with <paramref name="gatewayKeyId"/>, while the gateway acts as keys,
    /// as that key (admin §4.5, B-43: the picker shows what the object's key may use), cached per ceiling and key.
    /// </summary>
    public async Task<IReadOnlySet<string>?> ListedAsync(Residency ceiling, CancellationToken cancellationToken, Guid? gatewayKeyId = null)
    {
        var asKey = features.ActAsKey ? gatewayKeyId : null;
        if (string.IsNullOrWhiteSpace(options.Value.BaseUrl))
        {
            return null; // no gateway configured here: availability unknown
        }

        ceiling = await serviceCeiling.CapAsync(ceiling, cancellationToken);
        var now = time.GetUtcNow();
        if (_cache.TryGetValue((ceiling, asKey), out var cached) && now - cached.At < CacheFor)
        {
            return cached.Models;
        }

        try
        {
            var models = await gateway.ListModelsAsync(ceiling, cancellationToken, asKey);
            _cache[(ceiling, asKey)] = (now, models);
            return models;
        }
        catch (Exception ex) when (ex is GatewayKeyMissingException or GatewayUnavailableException or GatewayRefusedException or GatewayResidencyException
                                       or GatewayCallRefusedException or GatewayKeyRefusedException or GatewayDelegationEchoException)
        {
            return null;
        }
    }
}

/// <summary>
/// One level's key check: one embedding call at that ceiling (admin design §4.5 <c>GET /api/gateway/status</c>).
/// <c>Outcome</c>: <c>ok</c>, <c>not_configured</c> (no key here), <c>refused</c> (4xx), <c>unavailable</c>, <c>residency_refused</c>;
/// <c>Residency</c> the residency that answered (or the refused one), <c>Code</c> the gateway's error code.
/// </summary>
public sealed record GatewayLevelStatus(string Level, string Outcome, string? Residency, int? HttpStatus, string? Code, double? LatencyMs);

/// <summary>A profile's canary: recorded, and the last on-demand check (null = never checked).</summary>
public sealed record EmbeddingProfileCanaryStatus(Guid Id, string Name, string GatewayModel, int Dimensions, string MaxResidency, bool CanaryRecorded,
    DateTimeOffset? CanaryCheckedAt, double? CanarySimilarity);

/// <summary>
/// The gateway as this admin instance sees it. <see cref="Gateway"/> is the last context's version, admin URL and features
/// (admin design §4.10: the Gateway page shows them), <see cref="Organisation"/> the organisation sync with its <c>degraded</c> reason.
/// </summary>
public sealed record GatewayStatus(bool Configured, string? Model, IReadOnlyList<GatewayLevelStatus> Levels, GatewayModelSyncStatus Sync,
    IReadOnlyList<EmbeddingProfileCanaryStatus> Profiles, GatewayContextInfo? Gateway, OrgSyncStatus Organisation, GatewayActedStatus? Acted = null)
{
    /// <summary>The Integration API's read-back (B-45): whether the gateway's context names its address and its last refusal (<c>401</c>, <c>403</c>, <c>429</c>).</summary>
    public IntegrationReadback? Integration { get; init; }
}

/// <summary>The Integration API on the <i>Gateway</i> page (admin §4.5): configured or not, and why the last read-back gave nothing.</summary>
public sealed record IntegrationReadback(bool Configured, IntegrationApiError? LastError);

/// <summary>A key's budget as the admin shows it (admin §4.5): <c>scope</c> <c>key</c>, <c>team</c> or <c>department</c>; <c>unit</c> <c>usd</c> or <c>tokens</c>.</summary>
public sealed record KeyBudgetDto(string Scope, string Unit, decimal Limit, decimal Spent, decimal PercentUsed, bool Exhausted, string? Residency, string Period,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd)
{
    public static KeyBudgetDto Of(IntegrationBudget b) => new(b.Scope switch { "VirtualKey" => "key", "Team" => "team", "Department" => "department", _ => b.Scope.ToLowerInvariant() },
        b.Unit.ToLowerInvariant(), b.Limit, b.Spent, b.PercentUsed, b.Exhausted, b.Residency, b.Period, b.PeriodStart, b.PeriodEnd);
}

/// <summary>A key's gateway usage per period and step (admin §8.6): what the gateway counted for the key, the record for charging.</summary>
public sealed record KeyUsageRowDto(DateOnly Period, string? Step, long Requests, long SuccessfulRequests, long InputTokens, long CachedInputTokens, long OutputTokens,
    decimal CostUsd);

/// <summary><c>GET /api/gateway/keys/{id}/usage</c>: the period and the gateway's rows.</summary>
public sealed record KeyUsageDto(DateOnly From, DateOnly To, string Granularity, IReadOnlyList<KeyUsageRowDto> Rows);

/// <summary>
/// One <c>GET /v1/models</c> made as the first usable mirrored key while the gateway acts as keys (admin §4.5, B-43).
/// <c>Outcome</c>: <c>ok</c> (echoed), <c>echo_missing</c> (event 5011), <c>refused</c> (the gateway refused the key, <c>Code</c>),
/// <c>unavailable</c>, <c>no_key</c> (no usable key in the mirror); null when the gateway does not act as keys.
/// </summary>
public sealed record GatewayActedStatus(Guid? GatewayKeyId, string? KeyName, string Outcome, string? Code);

/// <summary>The key picker's list and the gateway's admin UI address for links to a key (null when unknown).</summary>
public sealed record GatewayKeyListDto(string? AdminUrl, IReadOnlyList<GatewayKeyDto> Keys);

/// <summary>
/// An assigned gateway key of the mirror with its read-only settings (admin design §4.5). <see cref="Selectable"/> is V39's
/// status part (<c>Active</c>, owner active, the gateway's <c>usable</c>), and <see cref="Reason"/> says why not.
/// </summary>
public sealed record GatewayKeyDto(
    Guid Id, string Name, Guid TeamId, string? TeamName, Guid? DepartmentId, string Status, bool OwnerActive, DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> AllowedModels, IReadOnlyList<string> AllowedResidencies, string PiiPolicy, string ContentGuardPolicy, int? RequestsPerMinute,
    int? TokensPerMinute, int? MaxConcurrentRequests, int? MaxOutputTokens, bool HasSystemPrompt, bool Usable, bool Selectable, string? Reason, DateTimeOffset SyncedAt,
    int UsedBy = 0)
{
    /// <summary><paramref name="usedBy"/>: the assistants and knowledge bases that hold the key (the shared-key note, admin §8.6, #642).</summary>
    public static GatewayKeyDto Of(GatewayKey k, string? teamName, int usedBy = 0)
    {
        ArgumentNullException.ThrowIfNull(k);
        var reason = GatewayKeyRules.UnusableReason(k);
        return new(k.Id, k.Name, k.TeamId, teamName, k.DepartmentId, k.Status, k.OwnerActive, k.ExpiresAt, k.AllowedModels, k.AllowedResidencies, k.PiiPolicy,
            k.ContentGuardPolicy, k.RequestsPerMinute, k.TokensPerMinute, k.MaxConcurrentRequests, k.MaxOutputTokens, k.HasSystemPrompt, k.Usable, reason is null,
            reason, k.SyncedAt, usedBy);
    }
}

/// <summary>
/// A row of the model mirror. <see cref="Available"/>: listed by the gateway's <c>/v1/models</c> with the platform's key at
/// the requested ceiling, null when the gateway could not be asked (#447).
/// </summary>
public sealed record GatewayModelDto(string Name, string Kind, string ModelKind, IReadOnlyList<string> Residencies, string Source, bool SupportsTools, int? ContextWindow,
    int? MaxOutputTokens, decimal? InputPerMillionUsd, decimal? CachedInputPerMillionUsd, decimal? OutputPerMillionUsd, DateTimeOffset? SyncedAt, bool? Available,
    string LimitsSource)
{
    public static GatewayModelDto Of(GatewayModel m, bool? available) => new(m.Name, m.Kind, m.ModelKind, m.Residencies, m.Source, m.SupportsTools, m.ContextWindow,
        m.MaxOutputTokens, m.InputPerMillionUsd, m.CachedInputPerMillionUsd, m.OutputPerMillionUsd, m.SyncedAt, available, m.LimitsSource);
}

/// <summary>
/// A mirror row's limits (admin design §4.5, §4.10; Decisions "Answer length from the model"): null clears one; both are
/// integers 1–10,000,000 and the output limit is at most the window when both are set. Manual until the gateway's catalogue
/// carries them; the sync keeps them.
/// </summary>
public sealed record GatewayModelLimitsRequest : AdminRequest, IValidatableObject
{
    public const int Max = 10_000_000;

    [Range(1, Max)]
    public int? ContextWindow { get; init; }

    [Range(1, Max)]
    public int? MaxOutputTokens { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ContextWindow is { } window && MaxOutputTokens is { } output && output > window
            ? [new ValidationResult("At most the context window.", [nameof(MaxOutputTokens)])]
            : [];
}

/// <summary>The gateway area (admin design §4.5): the model mirror, its sync, the key status per level and the embedding canary check.</summary>
public static class GatewayEndpoints
{
    /// <summary>The <c>platform_rule</c> constraint of a manual limit on a row whose limits come from the gateway.</summary>
    public const string LimitsFromGateway = "limits_from_gateway";

    /// <summary>The input of a status call: fixed, carries no content.</summary>
    public const string StatusProbeText = "status";

    /// <summary>The longest period of a key's gateway usage (admin §8.6; the Integration API's own limit, gateway-changes §5.6).</summary>
    public const int MaxIntegrationDays = 366;

    /// <summary>A JSON <c>null</c> body (a minimal API writes nothing for a null value): "no read-back" (admin §4.5, §8.6).</summary>
    private static IResult JsonNull() => Results.Content("null", "application/json");

    /// <summary>404 unless <paramref name="keyId"/> is a mirrored key of a department in the caller's scope (admin §4.5, as <c>/keys</c>).</summary>
    private static async Task InScopeKeyAsync(AdminContext admin, Guid keyId, CancellationToken ct)
    {
        var departments = admin.Departments().Select(d => d.Id);
        if (!await admin.Db.GatewayKeys.AsNoTracking().AnyAsync(k => k.Id == keyId && k.DepartmentId != null && departments.Contains(k.DepartmentId.Value), ct))
        {
            throw AdminFaults.Of(AdminErrorCodes.NotFound);
        }
    }

    public static RouteGroupBuilder MapGateway(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var gateway = api.MapGroup("/gateway").WithTags("Gateway");

        gateway.MapGet("/models", async (string? level, Guid? gatewayKeyId, AdminContext admin, RagDbContext db, GatewayModelAvailability availability,
                CancellationToken ct) =>
            {
                // A key of a department outside the caller's scope is 404 before any gateway call, as /keys (admin §4.5; #618 review).
                if (gatewayKeyId is { } keyId)
                {
                    var departments = admin.Departments().Select(d => d.Id);
                    if (!await db.GatewayKeys.AsNoTracking().AnyAsync(k => k.Id == keyId && k.DepartmentId != null && departments.Contains(k.DepartmentId.Value), ct))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    }
                }

                // level = an assistant's level (its ceiling); absent: the platform ceiling of the Settings pickers (V32).
                if (level is not null && !Classification.IsValid(level))
                {
                    throw AdminFaults.Field("level", AdminErrorCodes.InvalidValue);
                }

                var ceiling = level is null ? PlatformModelRules.Ceiling : ResidencyCeiling.ForLevel(level);
                var listed = await availability.ListedAsync(ceiling, ct, gatewayKeyId);
                return (await db.GatewayModels.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct))
                    .Select(m => GatewayModelDto.Of(m, listed?.Contains(m.Name))).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListGatewayModels")
            .WithSummary("The gateway model mirror with residencies, prices and whether the platform's key, or the object's (?gatewayKeyId=), may use each model (?level=)");

        // The limits of a row the gateway sends none for (Decisions "Answer length from the model", admin §4.10): once
        // /v1/models carries them (LimitsSource catalog) the manual fields go, and a write is refused (platform_rule).
        // Model names hold slashes (ume/chat-eu): a catch-all segment, unescaped, so a client may send the slash or %2F.
        gateway.MapPut("/models/{**name}", async (string name, GatewayModelLimitsRequest request, AdminContext admin, CancellationToken ct) =>
                GatewayModelDto.Of(await admin.WriteAsync(async attempt =>
                {
                    name = Uri.UnescapeDataString(name);
                    var row = await admin.Db.GatewayModels.SingleOrDefaultAsync(m => m.Name == name, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    if (row.LimitsSource == GatewayModelSources.Catalog)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.PlatformRule, new Dictionary<string, string> { ["constraint"] = LimitsFromGateway });
                    }

                    var before = new { row.ContextWindow, row.MaxOutputTokens };
                    row.ContextWindow = request.ContextWindow;
                    row.MaxOutputTokens = request.MaxOutputTokens;
                    return new AdminChange<GatewayModel>(row, "update", AuditEntityTypes.GatewayModel, row.Name, before, new { row.ContextWindow, row.MaxOutputTokens, row.Name });
                }, ct), null))
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateGatewayModel")
            .WithSummary("Set a mirror row's context window and output limit while the gateway sends none (null clears); the sync keeps them");

        gateway.MapPost("/sync", async (HttpContext http, GatewayModelSync sync, CancellationToken ct) =>
            !sync.Enabled
                ? throw AdminFaults.Of(AdminErrorCodes.SyncOff)
                : await sync.RunAsync(AdminActor.Of(http), ct) is { } result
                    ? Results.Ok(result)
                    : throw AdminFaults.Of(AdminErrorCodes.SyncFailed, new Dictionary<string, string> { ["error"] = sync.Status.LastError ?? "" }))
            .RequireAuthorization(AdminPolicies.Admin).WithName("SyncGatewayModels").WithSummary("Sync the model catalogue from the gateway now");

        // The key picker (admin design §4.5, §5.5.4; B-36): the department's assigned keys from the mirror with their read-only
        // settings; an unusable one is listed with its reason and is not selectable. Never a secret.
        gateway.MapGet("/keys", async (Guid departmentId, AdminContext admin, OrganisationSync organisation, CancellationToken ct) =>
            {
                if (!await admin.Departments().AnyAsync(d => d.Id == departmentId, ct))
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }

                var keys = await admin.Db.GatewayKeys.AsNoTracking().Where(k => k.DepartmentId == departmentId).OrderBy(k => k.Name).ToListAsync(ct);
                var teams = await admin.Db.Teams.AsNoTracking().Where(t => t.DepartmentId == departmentId).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
                var ids = keys.Select(k => k.Id).ToList();
                var assistants = await admin.Db.Assistants.AsNoTracking().Where(a => a.GatewayKeyId != null && ids.Contains(a.GatewayKeyId.Value))
                    .GroupBy(a => a.GatewayKeyId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
                var collections = await admin.Db.Collections.AsNoTracking()
                    .Where(c => c.DeletedAt == null && c.GatewayKeyId != null && ids.Contains(c.GatewayKeyId.Value))
                    .GroupBy(c => c.GatewayKeyId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
                return new GatewayKeyListDto(organisation.AdminUrl, [.. keys.Select(k => GatewayKeyDto.Of(k, teams.GetValueOrDefault(k.TeamId),
                    assistants.GetValueOrDefault(k.Id) + collections.GetValueOrDefault(k.Id)))]);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListGatewayKeys")
            .WithSummary("The department's assigned gateway keys for the key picker, with their settings and whether each can be chosen");

        // B-45 (admin §4.5, §8.6; gateway-changes §5.6): a key's budgets and its usage as the gateway counted them. The key's
        // department must be in scope (404 before any gateway call); null when the gateway names no Integration API address or
        // gives nothing (the Gateway page shows why).
        gateway.MapGet("/keys/{id:guid}/budget", async (Guid id, AdminContext admin, IntegrationApiClient integration, CancellationToken ct) =>
            {
                await InScopeKeyAsync(admin, id, ct);
                var budgets = await integration.BudgetsAsync(id, ct);
                return budgets is null ? JsonNull() : Results.Json(budgets.Items.Select(KeyBudgetDto.Of).ToList());
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetGatewayKeyBudget")
            .WithSummary("The budgets that apply to a gateway key, from the gateway's Integration API (null without the read-back)");

        gateway.MapGet("/keys/{id:guid}/usage", async (Guid id, DateOnly? from, DateOnly? to, string? granularity, AdminContext admin, IntegrationApiClient integration,
                CancellationToken ct) =>
            {
                await InScopeKeyAsync(admin, id, ct);
                var period = CostPeriod.Of(from, to, granularity, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(admin.Now,
                    TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime), maxDays: MaxIntegrationDays);
                var usage = await integration.UsageAsync(id, period.From, period.To, period.Granularity, ct);
                return usage is null ? JsonNull() : Results.Json(new KeyUsageDto(usage.From, usage.To, usage.Granularity, [.. usage.Items.Select(r =>
                    new KeyUsageRowDto(r.Period, r.Step, r.Requests, r.SuccessfulRequests, r.InputTokens, r.CachedInputTokens, r.OutputTokens, r.CostUsd))]));
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetGatewayKeyUsage")
            .WithSummary("A gateway key's requests, tokens and cost per period and step as the gateway counted them (null without the read-back)");

        gateway.MapGet("/status", StatusAsync)
            .RequireAuthorization(AdminPolicies.Admin).WithName("GatewayStatus").WithSummary("Per level key: one embedding call at that ceiling; sync and canary state");

        gateway.MapGet("/canary-check", (RagDbContext db, CancellationToken ct) => EmbeddingCanaryCheck.LastAsync(db, ct))
            .RequireAuthorization(AdminPolicies.Admin).WithName("LastEmbeddingCanaryCheck")
            .WithSummary("The stored last canary check per active profile; no gateway call");

        gateway.MapPost("/canary-check", (AdminContext admin, EmbeddingCanaryCheck check, CancellationToken ct) => check.RunAsync(admin, ct))
            .RequireAuthorization(AdminPolicies.Admin).WithName("CheckEmbeddingCanary")
            .WithSummary("The embedding canary on demand: one embedding per active profile with a recorded canary; the result is stored on the profile");

        return api;
    }

    /// <summary>
    /// Per level key held by this instance, one embedding of a fixed word with the default profile's model at that ceiling
    /// (the delegation echo is added with gateway release 2, P2). A level whose ceiling is below the model's residency is
    /// refused by the gateway (403 <c>max_residency_not_met</c>), which the result shows as such; any other failure of one
    /// level is reported as <c>error</c> with <see cref="GatewayModelSync.ErrorText"/>, so the page always answers.
    /// </summary>
    private static async Task<GatewayStatus> StatusAsync(RagDbContext db, GatewayApi gateway, GatewayModelSync sync, OrganisationSync organisation,
        IOptions<GatewayOptions> options, GatewayFeatures features, GatewayCeiling serviceCeiling, IntegrationApiClient integration, CancellationToken ct)
    {
        var profiles = await db.EmbeddingProfiles.AsNoTracking().Where(p => p.State == EmbeddingProfileStates.Active)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
        var recorded = (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM config."EmbeddingProfiles" WHERE "CanaryEmbedding" IS NOT NULL
            """).ToListAsync(ct)).ToHashSet();
        var canaries = profiles.Select(p => new EmbeddingProfileCanaryStatus(p.Id, p.Name, p.GatewayModel, p.Dimensions,
            ResidencyCeiling.ToHeader(p.MaxResidency), recorded.Contains(p.Id), p.CanaryCheckedAt, p.CanarySimilarity)).ToList();
        var configured = !string.IsNullOrEmpty(options.Value.BaseUrl);
        var model = profiles.FirstOrDefault()?.GatewayModel;
        var dimensions = profiles.FirstOrDefault() is { RequestDimensions: true } first ? first.Dimensions : (int?)null;
        var levels = new List<GatewayLevelStatus>();
        var maximum = configured ? await serviceCeiling.MaximumAsync(ct) : Residency.OnPrem;
        foreach (var level in Enum.GetValues<Residency>())
        {
            var name = ResidencyCeiling.ToHeader(level);
            if (!configured || model is null || options.Value.KeyFor(level, maximum) is null)
            {
                levels.Add(new GatewayLevelStatus(name, "not_configured", null, null, null, null));
                continue;
            }

            try
            {
                var answer = await gateway.EmbedAsync(model, [StatusProbeText], level, ct, dimensions);
                levels.Add(new GatewayLevelStatus(name, "ok", ResidencyCeiling.ToHeader(answer.Residency), 200, null, answer.Duration.TotalMilliseconds));
            }
            catch (GatewayRefusedException refused)
            {
                levels.Add(new GatewayLevelStatus(name, "refused", null, refused.Status, refused.Code, null));
            }
            catch (GatewayUnavailableException unavailable)
            {
                levels.Add(new GatewayLevelStatus(name, "unavailable", null, unavailable.Status, unavailable.Code, null));
            }
            catch (GatewayResidencyException residency)
            {
                levels.Add(new GatewayLevelStatus(name, "residency_refused", residency.Observed, null, null, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Anything else from one level (a malformed answer, a count mismatch) is that level's error, not a failed page.
                levels.Add(new GatewayLevelStatus(name, "error", null, null, GatewayModelSync.ErrorText(ex), null));
            }
        }

        var context = organisation.Status;
        var acted = configured && features.ActAsKey ? await ActedAsync(db, gateway, await serviceCeiling.MaximumAsync(ct), ct) : null;
        return new GatewayStatus(configured, model, levels, sync.Status, canaries, context.Gateway, context, acted)
        {
            Integration = new IntegrationReadback(integration.Configured, integration.LastError),
        };
    }

    /// <summary>The acted check of the status (B-43): <c>/v1/models</c> as the first usable mirrored key, at the service's maximum.</summary>
    private static async Task<GatewayActedStatus> ActedAsync(RagDbContext db, GatewayApi gateway, Residency maximum, CancellationToken ct)
    {
        var key = await db.GatewayKeys.AsNoTracking().Where(k => k.Status == "Active" && k.OwnerActive && k.Usable).OrderBy(k => k.Name).FirstOrDefaultAsync(ct);
        if (key is null)
        {
            return new GatewayActedStatus(null, null, "no_key", null);
        }

        try
        {
            await gateway.ListModelsAsync(maximum, ct, key.Id);
            return new GatewayActedStatus(key.Id, key.Name, "ok", null);
        }
        catch (GatewayDelegationEchoException)
        {
            return new GatewayActedStatus(key.Id, key.Name, "echo_missing", null);
        }
        catch (GatewayKeyRefusedException refused)
        {
            return new GatewayActedStatus(key.Id, key.Name, "refused", refused.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new GatewayActedStatus(key.Id, key.Name, "unavailable", GatewayModelSync.ErrorText(ex));
        }
    }
}
