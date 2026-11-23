using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Kalla.ServiceDefaults;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Gateway;
using Kalla.Infrastructure.Persistence;

namespace Kalla.AdminApi;

/// <summary>
/// Bound from the <c>OrgSync</c> configuration section (repo-and-deployment §3): the interval of the organisation and model
/// syncs. Both call the gateway with the admin's credential key (<c>Gateway:Key</c>); there is no Data API key.
/// </summary>
public sealed class OrgSyncOptions
{
    public const string SectionName = "OrgSync";

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 5;
}

/// <summary>What one run changed: rows written per table (unchanged rows are not written; a removed key counts).</summary>
public sealed record OrgSyncResult(int Departments, int Teams, int Keys) : IMirrorSyncResult
{
    public bool Changed => Departments + Teams + Keys > 0;
}

/// <summary>
/// The last runs, for the platform admin's overview and the <i>Gateway</i> page (admin design §4.10).
/// <see cref="Gateway"/> is the last context's version, admin URL and features; <see cref="Degraded"/> says why departments
/// and teams were left as they are (null when they were taken). <see cref="AdminUrl"/> is the address for the gateway links
/// (admin design §2.3): the context's <c>admin_url</c>, else <c>Admin:GatewayAdminUrl</c> (#619). <see cref="Integration"/> is the
/// Integration API read-back's state (B-45), filled by <c>GET /api/organisation/sync</c> (#642).
/// </summary>
public sealed record OrgSyncStatus(bool Enabled, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures, string? LastError,
    OrgSyncResult? LastResult, GatewayContextInfo? Gateway, string? Degraded, string? AdminUrl = null, IntegrationReadback? Integration = null);

/// <summary>
/// Mirrors the gateway's <c>GET /v1/fors/context</c> (admin design §4.10, gateway-changes §5.5) with the admin's credential
/// key, every <c>OrgSync:IntervalMinutes</c> and on demand: <c>assigned_keys</c> into <c>config."GatewayKeys"</c>, and
/// departments and teams into <c>config."Departments"</c>/<c>"Teams"</c> when the gateway has the feature
/// <c>organisation</c> and the key the scope <c>organisation.read</c> (otherwise they are left as they are and the status
/// says <c>degraded</c>, because without the scope the context lists only the keys' departments). Member groups replace the
/// stored ones when the gateway has the feature <c>member-groups</c> (identity §2.1). <c>If-None-Match</c> with the last
/// <c>ETag</c>: a <c>304</c> writes nothing. One run is one config write: the version lock, then
/// <c>pg_try_advisory_xact_lock</c> so only one instance writes, then only the changed rows and one audit entry. A
/// department or team missing from the context becomes inactive, never deleted; a key no longer listed (revoked) is
/// deleted. A failed run keeps the last mirror.
/// </summary>
public sealed partial class OrganisationSync(
    IServiceScopeFactory scopes, GatewayApi gateway, IOptions<OrgSyncOptions> options, IOptionsMonitor<GatewayOptions> gatewayOptions, TimeProvider time,
    IMeterFactory meters, ConfigLockMetrics lockMetrics, ILogger<OrganisationSync> logger, IOptions<AdminOptions>? adminOptions = null) : BackgroundService, IHealthCheck
{
    /// <summary>Failed runs in a row after which health is <c>Degraded</c> (identity §2.1).</summary>
    public const int FailuresBeforeDegraded = 3;

    /// <summary>The audit actor of a scheduled run.</summary>
    public const string SystemActor = "system:org-sync";

    /// <summary>Advisory lock key of the sync ("RAG0" + 0x10), one writer across instances.</summary>
    public const long LockKey = 0x5241_4730_0010;

    private readonly MirrorSyncState _state = new("organisation", meters, gatewayOptions, time);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OrgSyncResult? _lastResult;
    private GatewayContextInfo? _gateway;
    private string? _degraded;
    private string? _etag;

    /// <summary>On when the gateway and this service's credential key are configured.</summary>
    public bool Enabled => _state.Enabled;

    public OrgSyncStatus Status =>
        new(Enabled, _state.LastRunAt, _state.LastSuccessAt, _state.ConsecutiveFailures, _state.LastError, _lastResult, _gateway, _degraded, AdminUrl);

    /// <summary>The gateway links' address (admin design §2.3): the last context's <c>admin_url</c>, else <c>Admin:GatewayAdminUrl</c>.</summary>
    public string? AdminUrl => _gateway?.AdminUrl is { Length: > 0 } url ? url : adminOptions?.Value.GatewayAdminUrl is { Length: > 0 } configured ? configured : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(gatewayOptions.CurrentValue.BaseUrl))
        {
            LogDisabled(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Without its credential key yet (a stack update writes it later): check again soon instead of stopping.
            if (!Enabled)
            {
                await Task.Delay(GatewayContextReader.KeyWait, time, stoppingToken);
                continue;
            }

            await RunAsync(null, stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(options.Value.IntervalMinutes), time, stoppingToken);
        }
    }

    /// <summary>
    /// One run, by <paramref name="actor"/> (an admin's <c>Sync now</c>) or the schedule (null). Runs one at a time per
    /// instance; never throws except on shutdown. Returns null when the run failed or the sync is off.
    /// </summary>
    public async Task<OrgSyncResult?> RunAsync(AdminActor? actor, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state.MarkStarted();
            var answer = await gateway.GetContextAsync(_etag, cancellationToken);
            if (answer.Context is not { } context)
            {
                // 304: the context is as at the last run, which was written.
                _state.MarkSucceeded("unchanged");
                return new OrgSyncResult(0, 0, 0);
            }

            var degraded = DegradedReason(context);
            var result = await MirrorSync.WriteAsync(scopes, lockMetrics, time, LockKey, SystemActor, "organisation", actor,
                (db, now, ct) => ApplyAsync(db, context, now, ct), cancellationToken);

            _gateway = context.Gateway;
            _degraded = degraded;
            if (degraded is not null)
            {
                LogDegraded(logger, degraded);
            }

            // Another instance's run (lock held) wrote this context or will: the next run asks again without an ETag.
            _etag = result is null ? null : answer.ETag;
            _state.MarkSucceeded(MirrorSyncState.Outcome(result));
            _lastResult = result ?? _lastResult;
            LogRun(logger, result?.Departments ?? 0, result?.Teams ?? 0, result?.Keys ?? 0);
            return result ?? new OrgSyncResult(0, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failures = _state.MarkFailed(ex);
            LogRunFailed(logger, GatewayModelSync.ErrorText(ex), ExceptionText.Types(ex), failures); // never the messages (#232)
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Why departments and teams are not taken from <paramref name="context"/>, or null when they are: the gateway lacks the
    /// feature <c>organisation</c>, or the credential key the scope <c>organisation.read</c>.
    /// </summary>
    public static string? DegradedReason(GatewayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.Supports(GatewayContext.OrganisationFeature)
            ? "The gateway does not list the organisation (feature organisation); departments and teams are kept as they are."
            : !context.Key.IntegrationScopes.Contains(GatewayContext.OrganisationRead, StringComparer.Ordinal)
                ? "The admin's gateway key lacks the scope organisation.read; departments and teams are kept as they are."
                : null;
    }

    /// <summary>
    /// Brings the tracked mirror in line with <paramref name="context"/> without saving: the assigned keys always, departments
    /// and teams only when <see cref="DegradedReason"/> is null. Inserts new rows, updates changed ones (with
    /// <c>SyncedAt</c>), deactivates missing departments and teams and deletes keys no longer listed. Teams of a department
    /// the context does not list are skipped.
    /// </summary>
    public static async Task<OrgSyncResult> ApplyAsync(RagDbContext db, GatewayContext context, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(context);
        var keys = await ApplyKeysAsync(db, context.AssignedKeys, now, ct);
        if (DegradedReason(context) is not null)
        {
            return new OrgSyncResult(0, 0, keys);
        }

        var memberGroups = context.Supports(GatewayContext.MemberGroupsFeature);
        var storedDepartments = await db.Departments.ToDictionaryAsync(d => d.Id, ct);
        var storedTeams = await db.Teams.ToDictionaryAsync(t => t.Id, ct);
        int changedDepartments = 0, changedTeams = 0;

        foreach (var item in context.Departments)
        {
            var groups = memberGroups ? item.MemberGroups ?? [] : null;
            if (!storedDepartments.TryGetValue(item.Id, out var department))
            {
                department = new Department { Id = item.Id, Name = item.Name, CostCenterCode = item.CostCenterCode };
                db.Departments.Add(department);
                storedDepartments[item.Id] = department;
            }
            else if (department.Name == item.Name && department.CostCenterCode == item.CostCenterCode && department.IsActive == item.IsActive &&
                     (groups is null || department.MemberGroups.SequenceEqual(groups, StringComparer.Ordinal)))
            {
                continue;
            }

            department.Name = item.Name;
            department.CostCenterCode = item.CostCenterCode;
            department.IsActive = item.IsActive;
            department.MemberGroups = groups is null ? department.MemberGroups : [.. groups];
            department.SyncedAt = now;
            changedDepartments++;
        }

        var listedDepartments = context.Departments.Select(d => d.Id).ToHashSet();
        foreach (var department in storedDepartments.Values.Where(d => d.IsActive && !listedDepartments.Contains(d.Id)))
        {
            department.IsActive = false;
            department.SyncedAt = now;
            changedDepartments++;
        }

        foreach (var item in context.Teams.Where(t => storedDepartments.ContainsKey(t.DepartmentId)))
        {
            if (!storedTeams.TryGetValue(item.Id, out var team))
            {
                team = new Team { Id = item.Id, DepartmentId = item.DepartmentId, Name = item.Name };
                db.Teams.Add(team);
                storedTeams[item.Id] = team;
            }
            else if (team.DepartmentId == item.DepartmentId && team.Name == item.Name && team.IsActive == item.IsActive && team.Code == item.Code)
            {
                continue;
            }

            team.DepartmentId = item.DepartmentId;
            team.Name = item.Name;
            team.IsActive = item.IsActive;
            team.Code = item.Code;
            team.SyncedAt = now;
            changedTeams++;
        }

        var listedTeams = context.Teams.Select(t => t.Id).ToHashSet();
        foreach (var team in storedTeams.Values.Where(t => t.IsActive && !listedTeams.Contains(t.Id)))
        {
            team.IsActive = false;
            team.SyncedAt = now;
            changedTeams++;
        }

        return new OrgSyncResult(changedDepartments, changedTeams, keys);
    }

    /// <summary>The <c>GatewayKeys</c> mirror (data-model §2.1): upserted by id, only changed rows written; a key no longer listed (revoked) deleted.</summary>
    private static async Task<int> ApplyKeysAsync(RagDbContext db, IReadOnlyList<GatewayAssignedKey> assigned, DateTimeOffset now, CancellationToken ct)
    {
        var stored = await db.GatewayKeys.ToDictionaryAsync(k => k.Id, ct);
        var changed = 0;
        foreach (var item in assigned)
        {
            if (!stored.TryGetValue(item.Id, out var key))
            {
                key = new GatewayKey { Id = item.Id, Name = item.Name, Status = item.Status, PiiPolicy = item.PiiPolicy, ContentGuardPolicy = item.ContentGuardPolicy };
                db.GatewayKeys.Add(key);
                stored[item.Id] = key;
            }
            else if (Same(key, item))
            {
                continue;
            }

            key.Name = item.Name;
            key.TeamId = item.TeamId;
            key.DepartmentId = item.DepartmentId;
            key.Status = item.Status;
            key.OwnerActive = item.OwnerActive;
            key.ExpiresAt = item.ExpiresAt;
            key.AllowedModels = [.. item.AllowedModels];
            key.AllowedResidencies = [.. item.AllowedResidencies];
            key.PiiPolicy = item.PiiPolicy;
            key.ContentGuardPolicy = item.ContentGuardPolicy;
            key.RequestsPerMinute = item.RequestsPerMinute;
            key.TokensPerMinute = item.TokensPerMinute;
            key.MaxConcurrentRequests = item.MaxConcurrentRequests;
            key.MaxOutputTokens = item.MaxOutputTokens;
            key.HasSystemPrompt = item.HasSystemPrompt;
            key.Usable = item.Usable;
            key.SyncedAt = now;
            changed++;
        }

        var listed = assigned.Select(k => k.Id).ToHashSet();
        foreach (var key in stored.Values.Where(k => !listed.Contains(k.Id)))
        {
            db.GatewayKeys.Remove(key);
            changed++;
        }

        return changed;
    }

    private static bool Same(GatewayKey key, GatewayAssignedKey item) =>
        key.Name == item.Name && key.TeamId == item.TeamId && key.DepartmentId == item.DepartmentId && key.Status == item.Status &&
        key.OwnerActive == item.OwnerActive && key.ExpiresAt == item.ExpiresAt && key.AllowedModels.SequenceEqual(item.AllowedModels, StringComparer.Ordinal) &&
        key.AllowedResidencies.SequenceEqual(item.AllowedResidencies, StringComparer.Ordinal) && key.PiiPolicy == item.PiiPolicy &&
        key.ContentGuardPolicy == item.ContentGuardPolicy && key.RequestsPerMinute == item.RequestsPerMinute && key.TokensPerMinute == item.TokensPerMinute &&
        key.MaxConcurrentRequests == item.MaxConcurrentRequests && key.MaxOutputTokens == item.MaxOutputTokens && key.HasSystemPrompt == item.HasSystemPrompt &&
        key.Usable == item.Usable;

    /// <summary>
    /// Health (identity §2.1, admin design §4.10): <c>Degraded</c> with the reason when departments and teams are not taken
    /// from the context, or after <see cref="FailuresBeforeDegraded"/> failed runs; the mirror stays in use either way.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            !Enabled ? HealthCheckResult.Healthy("The organisation sync is off.")
            : _state.ConsecutiveFailures >= FailuresBeforeDegraded ? HealthCheckResult.Degraded($"The organisation sync has failed {_state.ConsecutiveFailures} times in a row.")
            : _degraded is { } reason ? HealthCheckResult.Degraded(reason)
            : HealthCheckResult.Healthy());

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Organisation sync off: Gateway:BaseUrl or Gateway:Key is not set")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organisation sync wrote {Departments} departments, {Teams} teams and {Keys} gateway keys")]
    private static partial void LogRun(ILogger logger, int departments, int teams, int keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Organisation sync degraded: {Reason}")]
    private static partial void LogDegraded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Organisation sync failed: {Error} ({Types}; {Failures} in a row); the mirror is kept")]
    private static partial void LogRunFailed(ILogger logger, string error, string types, int failures);
}
