using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ume.Rag.ServiceDefaults;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Bound from the <c>OrgSync</c> configuration section (repo-and-deployment §3): the interval of the organisation and model
/// syncs. <see cref="BaseUrl"/> and <see cref="DataApiKey"/> are the Data API's, read only by the model sync until it reads
/// <c>/v1/models</c> (B-35 part 3); the organisation comes from the gateway's context.
/// </summary>
public sealed class OrgSyncOptions
{
    public const string SectionName = "OrgSync";

    /// <summary>The gateway Data API (locally the FakeGateway) of the model catalogue (<c>/v1/catalog/*</c>).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>A Data API key with <c>catalog.read</c> only (<c>OrgSync__DataApiKey</c>, a secret).</summary>
    public string? DataApiKey { get; set; }

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 5;
}

/// <summary>What one run changed: rows written per table (unchanged rows are not written; a removed key counts).</summary>
public sealed record OrgSyncResult(int Departments, int Teams, int Keys)
{
    public bool Changed => Departments + Teams + Keys > 0;
}

/// <summary>
/// The last runs, for the platform admin's overview and the <i>Gateway</i> page (admin design §4.10).
/// <see cref="Gateway"/> is the last context's version, admin URL and features; <see cref="Degraded"/> says why departments
/// and teams were left as they are (null when they were taken).
/// </summary>
public sealed record OrgSyncStatus(bool Enabled, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures, string? LastError,
    OrgSyncResult? LastResult, GatewayContextInfo? Gateway, string? Degraded);

/// <summary>
/// Mirrors the gateway's <c>GET /v1/ume/context</c> (admin design §4.10, gateway-changes §5.5) with the admin's credential
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
    IServiceScopeFactory scopes, GatewayApi gateway, IOptions<OrgSyncOptions> options, IOptions<GatewayOptions> gatewayOptions, TimeProvider time,
    IMeterFactory meters, ConfigLockMetrics lockMetrics, ILogger<OrganisationSync> logger) : BackgroundService, IHealthCheck
{
    /// <summary>Failed runs in a row after which health is <c>Degraded</c> (identity §2.1).</summary>
    public const int FailuresBeforeDegraded = 3;

    public const string HttpClientName = "org-sync";

    /// <summary>The audit actor of a scheduled run.</summary>
    public const string SystemActor = "system:org-sync";

    /// <summary>Advisory lock key of the sync ("RAG0" + 0x10), one writer across instances.</summary>
    public const long LockKey = 0x5241_4730_0010;

    private readonly Counter<long> _runs = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.admin.sync", description: "Organisation and model catalogue sync runs by kind and outcome");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRunAt;
    private DateTimeOffset? _lastSuccessAt;
    private int _failures;
    private string? _lastError;
    private OrgSyncResult? _lastResult;
    private GatewayContextInfo? _gateway;
    private string? _degraded;
    private string? _etag;

    /// <summary>On when the gateway and this service's credential key are configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(gatewayOptions.Value.BaseUrl) && !string.IsNullOrEmpty(gatewayOptions.Value.Key);

    public OrgSyncStatus Status => new(Enabled, _lastRunAt, _lastSuccessAt, Volatile.Read(ref _failures), _lastError, _lastResult, _gateway, _degraded);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            LogDisabled(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
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
            _lastRunAt = time.GetUtcNow();
            var answer = await gateway.GetContextAsync(_etag, cancellationToken);
            if (answer.Context is not { } context)
            {
                // 304: the context is as at the last run, which was written.
                MarkSucceeded();
                _runs.Add(1, new("kind", "organisation"), new("outcome", "unchanged"));
                return new OrgSyncResult(0, 0, 0);
            }

            var degraded = DegradedReason(context);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var result = await db.ExecuteConfigWriteAsync(async (_, ct) =>
            {
                if (!await db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock({LockKey}) AS "Value" """).SingleAsync(ct))
                {
                    return new RunOutcome(null, null); // another instance is syncing right now
                }

                var changed = await ApplyAsync(db, context, time.GetUtcNow(), ct);
                if (!changed.Changed)
                {
                    return new RunOutcome(changed, null);
                }

                await db.SaveChangesAsync(ct);
                var audit = new AuditLogEntry
                {
                    Timestamp = time.GetUtcNow(), Actor = SystemActor, Action = "sync", EntityType = "organisation", Service = AdminContext.ServiceName,
                    ConfigVersion = await db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(ct),
                    Details = JsonSerializer.Serialize(new AuditDetails(null, changed), AuditJson.Options),
                };
                actor?.Stamp(audit);
                db.AuditLog.Add(audit);
                await db.SaveChangesAsync(ct);
                return new RunOutcome(changed, audit);
            }, cancellationToken, lockMetrics);

            if (result.Audit is { } entry)
            {
                SecurityEvents.AdminChange(SecurityEvents.CreateLogger(scope.ServiceProvider.GetRequiredService<ILoggerFactory>()), entry.Actor,
                    entry.Action, entry.EntityType, entry.EntityId);
            }

            _gateway = context.Gateway;
            _degraded = degraded;
            if (degraded is not null)
            {
                LogDegraded(logger, degraded);
            }

            // Another instance's run (lock held) wrote this context or will: the next run asks again without an ETag.
            _etag = result.Result is null ? null : answer.ETag;
            MarkSucceeded();
            _lastResult = result.Result ?? _lastResult;
            _runs.Add(1, new("kind", "organisation"), new("outcome", result.Result is null ? "skipped" : result.Result.Changed ? "changed" : "unchanged"));
            LogRun(logger, result.Result?.Departments ?? 0, result.Result?.Teams ?? 0, result.Result?.Keys ?? 0);
            return result.Result ?? new OrgSyncResult(0, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Shown to platform admins: the type and SQL state, HTTP status or gateway code only, never connection details.
            _lastError = GatewayModelSync.ErrorText(ex);
            var failures = Interlocked.Increment(ref _failures);
            _runs.Add(1, new("kind", "organisation"), new("outcome", "failed"));
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
            : Volatile.Read(ref _failures) >= FailuresBeforeDegraded ? HealthCheckResult.Degraded($"The organisation sync has failed {Volatile.Read(ref _failures)} times in a row.")
            : _degraded is { } reason ? HealthCheckResult.Degraded(reason)
            : HealthCheckResult.Healthy());

    private void MarkSucceeded()
    {
        _lastSuccessAt = time.GetUtcNow();
        _lastError = null;
        Volatile.Write(ref _failures, 0);
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    /// <summary>A run's result (null: another instance held the lock) and its audit entry (null: nothing changed).</summary>
    private sealed record RunOutcome(OrgSyncResult? Result, AuditLogEntry? Audit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organisation sync off: Gateway:BaseUrl or Gateway:Key is not set")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organisation sync wrote {Departments} departments, {Teams} teams and {Keys} gateway keys")]
    private static partial void LogRun(ILogger logger, int departments, int teams, int keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Organisation sync degraded: {Reason}")]
    private static partial void LogDegraded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Organisation sync failed: {Error} ({Types}; {Failures} in a row); the mirror is kept")]
    private static partial void LogRunFailed(ILogger logger, string error, string types, int failures);
}
