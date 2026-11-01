using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.ServiceDefaults;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>Bound from the <c>OrgSync</c> configuration section (repo-and-deployment §3). Without a base URL the sync is off.</summary>
public sealed class OrgSyncOptions
{
    public const string SectionName = "OrgSync";

    /// <summary>The gateway Data API (locally the FakeGateway), which serves <c>/v1/catalog/departments</c> and <c>/teams</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>A Data API key with <c>catalog.read</c> only (<c>OrgSync__DataApiKey</c>, a secret).</summary>
    public string? DataApiKey { get; set; }

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 5;
}

/// <summary>A department of the gateway catalogue. <see cref="MemberGroups"/> is null until gateway release 2 sends it.</summary>
public sealed record CatalogDepartmentItem(Guid Id, string Name, string CostCenterCode, bool IsActive, string[]? MemberGroups);

/// <summary>A team of the gateway catalogue; <see cref="Code"/> comes with gateway release 2.</summary>
public sealed record CatalogTeamItem(Guid Id, Guid DepartmentId, string Name, bool IsActive, string? Code);

/// <summary>What one run changed: rows written per table (unchanged rows are not written).</summary>
public sealed record OrgSyncResult(int Departments, int Teams)
{
    public bool Changed => Departments + Teams > 0;
}

/// <summary>The last runs, for the platform admin's overview (admin design §4.10).</summary>
public sealed record OrgSyncStatus(bool Enabled, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures, string? LastError, OrgSyncResult? LastResult);

/// <summary>
/// Mirrors the gateway's departments and teams into <c>config."Departments"</c>/<c>"Teams"</c> (admin design §4.10) every
/// <c>OrgSync:IntervalMinutes</c> and on demand. One run is one config write: the version lock, then
/// <c>pg_try_advisory_xact_lock</c> so only one instance writes (as the gateway's <c>ModelListSync</c>), then only the
/// changed rows and one audit entry. A department or team missing from the gateway becomes inactive, never deleted.
/// Member groups replace the stored ones only when the catalogue carries them (gateway release 2); before that they are
/// entered by hand (identity plan §3). A failed run keeps the last mirror.
/// </summary>
public sealed partial class OrganisationSync(
    IServiceScopeFactory scopes, IHttpClientFactory httpClients, IOptions<OrgSyncOptions> options, TimeProvider time, IMeterFactory meters,
    ILogger<OrganisationSync> logger) : BackgroundService
{
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

    public bool Enabled => options.Value.BaseUrl is not null;

    public OrgSyncStatus Status => new(Enabled, _lastRunAt, _lastSuccessAt, Volatile.Read(ref _failures), _lastError, _lastResult);

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
            var departments = await FetchAsync<CatalogDepartmentItem>("v1/catalog/departments", cancellationToken);
            var teams = await FetchAsync<CatalogTeamItem>("v1/catalog/teams", cancellationToken);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var result = await db.ExecuteConfigWriteAsync(async (_, ct) =>
            {
                if (!await db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock({LockKey}) AS "Value" """).SingleAsync(ct))
                {
                    return new RunOutcome(null, null); // another instance is syncing right now
                }

                var changed = await ApplyAsync(db, departments, teams, time.GetUtcNow(), ct);
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
            }, cancellationToken);

            if (result.Audit is { } entry)
            {
                SecurityEvents.AdminChange(SecurityEvents.CreateLogger(scope.ServiceProvider.GetRequiredService<ILoggerFactory>()), entry.Actor,
                    entry.Action, entry.EntityType, entry.EntityId);
            }

            _lastSuccessAt = time.GetUtcNow();
            _lastError = null;
            Volatile.Write(ref _failures, 0);
            _lastResult = result.Result ?? _lastResult;
            _runs.Add(1, new("kind", "organisation"), new("outcome", result.Result is null ? "skipped" : result.Result.Changed ? "changed" : "unchanged"));
            LogRun(logger, result.Result?.Departments ?? 0, result.Result?.Teams ?? 0);
            return result.Result ?? new OrgSyncResult(0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Shown to platform admins: the type and SQL state or HTTP status only, never a message with connection details.
            _lastError = ex switch
            {
                DbException { SqlState: { } state } => $"{ex.GetType().Name} (SQL state {state})",
                HttpRequestException { StatusCode: { } status } => $"{ex.GetType().Name} (HTTP {(int)status})",
                _ => ex.GetType().Name,
            };
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
    /// Brings the tracked mirror in line with the catalogue without saving: inserts new rows, updates changed ones (with
    /// <c>SyncedAt</c>), deactivates missing ones. Teams of a department the catalogue does not list are skipped.
    /// </summary>
    public static async Task<OrgSyncResult> ApplyAsync(
        RagDbContext db, IReadOnlyList<CatalogDepartmentItem> departments, IReadOnlyList<CatalogTeamItem> teams, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(departments);
        ArgumentNullException.ThrowIfNull(teams);
        var storedDepartments = await db.Departments.ToDictionaryAsync(d => d.Id, ct);
        var storedTeams = await db.Teams.ToDictionaryAsync(t => t.Id, ct);
        int changedDepartments = 0, changedTeams = 0;

        foreach (var item in departments)
        {
            if (!storedDepartments.TryGetValue(item.Id, out var department))
            {
                department = new Department { Id = item.Id, Name = item.Name, CostCenterCode = item.CostCenterCode };
                db.Departments.Add(department);
                storedDepartments[item.Id] = department;
            }
            else if (department.Name == item.Name && department.CostCenterCode == item.CostCenterCode && department.IsActive == item.IsActive &&
                     (item.MemberGroups is null || department.MemberGroups.SequenceEqual(item.MemberGroups, StringComparer.Ordinal)))
            {
                continue;
            }

            department.Name = item.Name;
            department.CostCenterCode = item.CostCenterCode;
            department.IsActive = item.IsActive;
            department.MemberGroups = item.MemberGroups is null ? department.MemberGroups : [.. item.MemberGroups];
            department.SyncedAt = now;
            changedDepartments++;
        }

        var listedDepartments = departments.Select(d => d.Id).ToHashSet();
        foreach (var department in storedDepartments.Values.Where(d => d.IsActive && !listedDepartments.Contains(d.Id)))
        {
            department.IsActive = false;
            department.SyncedAt = now;
            changedDepartments++;
        }

        foreach (var item in teams.Where(t => storedDepartments.ContainsKey(t.DepartmentId)))
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

        var listedTeams = teams.Select(t => t.Id).ToHashSet();
        foreach (var team in storedTeams.Values.Where(t => t.IsActive && !listedTeams.Contains(t.Id)))
        {
            team.IsActive = false;
            team.SyncedAt = now;
            changedTeams++;
        }

        return new OrgSyncResult(changedDepartments, changedTeams);
    }

    private async Task<IReadOnlyList<T>> FetchAsync<T>(string path, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Value.BaseUrl!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.DataApiKey);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<CatalogPage<T>>(AuditJson.Options, ct);
        return page?.Items ?? throw new InvalidDataException($"{path} returned no items.");
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private sealed record CatalogPage<T>(IReadOnlyList<T>? Items);

    /// <summary>A run's result (null: another instance held the lock) and its audit entry (null: nothing changed).</summary>
    private sealed record RunOutcome(OrgSyncResult? Result, AuditLogEntry? Audit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organisation sync off: OrgSync:BaseUrl is not set")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organisation sync wrote {Departments} departments and {Teams} teams")]
    private static partial void LogRun(ILogger logger, int departments, int teams);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Organisation sync failed: {Error} ({Types}; {Failures} in a row); the mirror is kept")]
    private static partial void LogRunFailed(ILogger logger, string error, string types, int failures);
}
