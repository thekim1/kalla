using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Gateway;
using Kalla.Infrastructure.Persistence;
using Kalla.Infrastructure.Security;

namespace Kalla.AdminApi;

/// <summary>What one run of a mirror sync changed; a run that changed nothing writes no rows and no audit entry.</summary>
public interface IMirrorSyncResult
{
    bool Changed { get; }
}

/// <summary>
/// The run bookkeeping of a mirror sync (<see cref="OrganisationSync"/>, <see cref="GatewayModelSync"/>), held in memory per
/// instance for its status (admin design §4.10): when it last ran and last succeeded, the failures in a row and the last
/// error, and the <c>rag.admin.sync</c> counter by <c>kind</c> and <c>outcome</c>.
/// </summary>
internal sealed class MirrorSyncState(string kind, IMeterFactory meters, IOptionsMonitor<GatewayOptions> gatewayOptions, TimeProvider time)
{
    private readonly Counter<long> _runs = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.admin.sync", description: "Organisation and model catalogue sync runs by kind and outcome");

    private int _failures;

    /// <summary>On when the gateway and this service's credential key are configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(gatewayOptions.CurrentValue.BaseUrl) && !string.IsNullOrEmpty(gatewayOptions.CurrentValue.Key);

    public DateTimeOffset? LastRunAt { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public int ConsecutiveFailures => Volatile.Read(ref _failures);

    /// <summary>Shown to platform admins: <see cref="GatewayModelSync.ErrorText"/>, never the message (it can name the gateway, #232).</summary>
    public string? LastError { get; private set; }

    public void MarkStarted() => LastRunAt = time.GetUtcNow();

    /// <summary>A run that succeeded with <paramref name="outcome"/> (<c>changed</c>, <c>unchanged</c> or <c>skipped</c>).</summary>
    public void MarkSucceeded(string outcome)
    {
        LastSuccessAt = time.GetUtcNow();
        LastError = null;
        Volatile.Write(ref _failures, 0);
        _runs.Add(1, new("kind", kind), new("outcome", outcome));
    }

    /// <summary>A run that failed with <paramref name="ex"/>; returns the failures in a row.</summary>
    public int MarkFailed(Exception ex)
    {
        LastError = GatewayModelSync.ErrorText(ex);
        var failures = Interlocked.Increment(ref _failures);
        _runs.Add(1, new("kind", kind), new("outcome", "failed"));
        return failures;
    }

    /// <summary>The counter's outcome of a written run: <c>skipped</c> when another instance held the lock (null).</summary>
    public static string Outcome(IMirrorSyncResult? result) => result is null ? "skipped" : result.Changed ? "changed" : "unchanged";
}

/// <summary>The config write the mirror syncs share (data-model §2.1).</summary>
internal static class MirrorSync
{
    /// <summary>
    /// One run's config write: under the version lock, <c>pg_try_advisory_xact_lock(<paramref name="lockKey"/>)</c> so only one
    /// instance writes, then <paramref name="apply"/> on the tracked mirror. When it changed something: the changed rows and one
    /// audit entry (<c>sync</c> of <paramref name="entityType"/> by <paramref name="systemActor"/>, stamped with
    /// <paramref name="actor"/> when an admin ran it), and after the commit the security event. Returns what changed, or null
    /// when another instance held the lock.
    /// </summary>
    public static async Task<TResult?> WriteAsync<TResult>(IServiceScopeFactory scopes, ConfigLockMetrics lockMetrics, TimeProvider time,
        long lockKey, string systemActor, string entityType, AdminActor? actor, Func<RagDbContext, DateTimeOffset, CancellationToken, Task<TResult>> apply,
        CancellationToken cancellationToken)
        where TResult : class, IMirrorSyncResult
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var outcome = await db.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            if (!await db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock({lockKey}) AS "Value" """).SingleAsync(ct))
            {
                return new RunOutcome<TResult>(null, null); // another instance is syncing right now
            }

            var changed = await apply(db, time.GetUtcNow(), ct);
            if (!changed.Changed)
            {
                return new RunOutcome<TResult>(changed, null);
            }

            await db.SaveChangesAsync(ct);
            var audit = new AuditLogEntry
            {
                Timestamp = time.GetUtcNow(), Actor = systemActor, Action = "sync", EntityType = entityType, Service = AdminContext.ServiceName,
                ConfigVersion = await db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(ct),
                Details = JsonSerializer.Serialize(new AuditDetails(null, changed), AuditJson.Options),
            };
            actor?.Stamp(audit);
            db.AuditLog.Add(audit);
            await db.SaveChangesAsync(ct);
            return new RunOutcome<TResult>(changed, audit);
        }, cancellationToken, lockMetrics);

        if (outcome.Audit is { } entry)
        {
            SecurityEvents.AdminChange(SecurityEvents.CreateLogger(scope.ServiceProvider.GetRequiredService<ILoggerFactory>()), entry.Actor,
                entry.Action, entry.EntityType, entry.EntityId);
        }

        return outcome.Result;
    }

    /// <summary>A run's result (null: another instance held the lock) and its audit entry (null: nothing changed).</summary>
    private sealed record RunOutcome<TResult>(TResult? Result, AuditLogEntry? Audit) where TResult : class;
}
