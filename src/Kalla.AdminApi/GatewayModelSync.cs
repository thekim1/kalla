using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kalla.Domain;
using Kalla.ServiceDefaults;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Gateway;
using Kalla.Infrastructure.Persistence;

namespace Kalla.AdminApi;

/// <summary>What one run changed: mirror rows added or changed, removed, and canaries recorded.</summary>
public sealed record GatewayModelSyncResult(int Written, int Removed, int CanariesRecorded) : IMirrorSyncResult
{
    public bool Changed => Written + Removed > 0;
}

/// <summary>The last runs (admin design §4.10), held in memory per instance.</summary>
public sealed record GatewayModelSyncStatus(bool Enabled, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures,
    string? LastError, GatewayModelSyncResult? LastResult, string? LastCanaryError);

/// <summary>
/// Mirrors the gateway's models into <c>config."GatewayModels"</c> (admin design §4.10) every <c>OrgSync:IntervalMinutes</c>
/// and on demand, from <c>GET /v1/models</c> at the service's maximum ceiling with the admin's credential key and the
/// gateway's facts (<c>kalla</c>, gateway-changes §5.5); <c>If-None-Match</c> with the last <c>ETag</c>, and a <c>304</c> writes
/// nothing. The same pattern as <see cref="OrganisationSync"/>: one config write under the version lock and an advisory
/// lock (<see cref="MirrorSync.WriteAsync"/>), only changed rows written (data-model §2.1: otherwise every run bumps the
/// config version), one audit entry. A row per chat, embedding or rerank model or alias: a model is <c>Kind = model</c>
/// with its residency, an alias <c>Kind = route</c> with its targets' residencies; <c>ModelKind</c>, prices and <c>SupportsTools</c> from the facts; the
/// limits from <c>context_window</c> and <c>max_output_tokens</c> once the gateway sends one (<c>LimitsSource = catalog</c>),
/// else the manual ones are kept. A catalogue row no longer listed is removed; manual rows are never touched. After each
/// run the canary embedding of every active profile that has none is recorded (<see cref="RecordCanariesAsync"/>).
/// </summary>
public sealed partial class GatewayModelSync(
    IServiceScopeFactory scopes, GatewayApi gateway, IOptions<OrgSyncOptions> options,
    IOptionsMonitor<GatewayOptions> gatewayOptions, GatewayCeiling serviceCeiling, TimeProvider time, IMeterFactory meters, ConfigLockMetrics lockMetrics, ILogger<GatewayModelSync> logger) : BackgroundService
{
    /// <summary>The audit actor of a run and of a recorded canary.</summary>
    public const string SystemActor = "system:gateway-sync";

    /// <summary>Advisory lock key of the sync ("RAG0" + 0x11), one writer across instances.</summary>
    public const long LockKey = 0x5241_4730_0011;

    private readonly MirrorSyncState _state = new("models", meters, gatewayOptions, time);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastCanaryError;
    private GatewayModelSyncResult? _lastResult;
    private string? _etag;

    /// <summary>On when the gateway and this service's credential key are configured.</summary>
    public bool Enabled => _state.Enabled;

    public GatewayModelSyncStatus Status =>
        new(Enabled, _state.LastRunAt, _state.LastSuccessAt, _state.ConsecutiveFailures, _state.LastError, _lastResult, _lastCanaryError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAsync(null, stoppingToken);
            // Without its credential key yet, check again soon: a stack update may write it (the secret files reload).
            await Task.Delay(Enabled ? TimeSpan.FromMinutes(options.Value.IntervalMinutes) : GatewayContextReader.KeyWait, time, stoppingToken);
        }
    }

    /// <summary>
    /// One run, by <paramref name="actor"/> or the schedule (null): the mirror when the gateway is configured, then the
    /// canaries. Never throws except on shutdown; returns null when the mirror could not be synced.
    /// </summary>
    public async Task<GatewayModelSyncResult?> RunAsync(AdminActor? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state.MarkStarted();
            var mirror = Enabled ? await SyncMirrorAsync(actor, cancellationToken) : new GatewayModelSyncResult(0, 0, 0);
            var canaries = await RecordCanariesAsync(cancellationToken);
            if (mirror is null)
            {
                return null;
            }

            var result = mirror with { CanariesRecorded = canaries };
            _lastResult = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GatewayModelSyncResult?> SyncMirrorAsync(AdminActor? actor, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await gateway.GetModelListAsync(await serviceCeiling.MaximumAsync(cancellationToken), _etag, cancellationToken);
            if (answer.Models is not { } models)
            {
                // 304: the list is as at the last run, which was written.
                _state.MarkSucceeded("unchanged");
                return new GatewayModelSyncResult(0, 0, 0);
            }

            var result = await MirrorSync.WriteAsync(scopes, lockMetrics, time, LockKey, SystemActor, "gateway-models", actor,
                (db, now, ct) => ApplyAsync(db, models, now, ct), cancellationToken);
            _etag = result is null ? null : answer.ETag; // another instance's run: ask again in full next time
            _state.MarkSucceeded(MirrorSyncState.Outcome(result));
            LogRun(logger, result?.Written ?? 0, result?.Removed ?? 0);
            return result ?? new GatewayModelSyncResult(0, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failures = _state.MarkFailed(ex);
            LogRunFailed(logger, ErrorText(ex), ExceptionText.Types(ex), failures); // never the messages: they can name the gateway (#232)
            return null;
        }
    }

    /// <summary>
    /// Brings the tracked mirror in line with <paramref name="models"/> without saving: adds and updates catalogue rows (with
    /// <c>SyncedAt</c>) and removes catalogue rows no longer listed. Models of another kind than chat, embedding or rerank
    /// are not mirrored. Manual rows are left as they are, and so are a row's manual limits while the gateway sends none.
    /// </summary>
    public static async Task<GatewayModelSyncResult> ApplyAsync(RagDbContext db, IReadOnlyList<GatewayListedModel> models, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(models);
        var listed = models.Where(m => ModelKindOf(m.Fors.Kind) is not null).GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stored = await db.GatewayModels.ToDictionaryAsync(m => m.Name, StringComparer.Ordinal, ct);
        int written = 0, removed = 0;

        foreach (var (name, model) in listed)
        {
            if (stored.TryGetValue(name, out var row) && row.Source == GatewayModelSources.Manual)
            {
                continue; // a manual row (route alias) with the same name wins
            }

            var facts = model.Fors;
            var alias = facts.Type == GatewayModelFacts.Alias;
            var residencies = (alias ? facts.Residencies ?? [] : facts.Residency is { } r ? [r] : [])
                .Where(x => ResidencyCeiling.TryParse(x, out _)).Distinct(StringComparer.Ordinal)
                .OrderBy(x => ResidencyCeiling.TryParse(x, out var residency) ? residency : default).ToArray();
            if (residencies.Length == 0)
            {
                continue; // nothing servable to record (the mirror's CHECK needs one residency)
            }

            var kind = alias ? GatewayModelKinds.Route : GatewayModelKinds.Model;
            var modelKind = ModelKindOf(facts.Kind)!;
            var tools = facts.Features.Contains("tools", StringComparer.Ordinal);
            // The gateway's limits once it sends one, both together so they stay consistent; else the manual ones stay.
            var fromGateway = facts.ContextWindow is not null || facts.MaxOutputTokens is not null;
            if (row is null)
            {
                row = new GatewayModel { Name = name, Source = GatewayModelSources.Catalog };
                db.GatewayModels.Add(row);
            }
            else if (row.Kind == kind && row.ModelKind == modelKind && row.Residencies.SequenceEqual(residencies, StringComparer.Ordinal) &&
                     row.InputPerMillionUsd == facts.Pricing?.InputPerMillionUsd && row.CachedInputPerMillionUsd == facts.Pricing?.CachedInputPerMillionUsd &&
                     row.OutputPerMillionUsd == facts.Pricing?.OutputPerMillionUsd && row.SupportsTools == tools &&
                     row.SupportsToolsSource == GatewayModelSources.Catalog &&
                     (fromGateway
                         ? row.LimitsSource == GatewayModelSources.Catalog && row.ContextWindow == facts.ContextWindow && row.MaxOutputTokens == facts.MaxOutputTokens
                         : row.LimitsSource == GatewayModelSources.Manual))
            {
                continue;
            }

            row.Kind = kind;
            row.ModelKind = modelKind;
            row.Residencies = residencies;
            row.InputPerMillionUsd = facts.Pricing?.InputPerMillionUsd;
            row.CachedInputPerMillionUsd = facts.Pricing?.CachedInputPerMillionUsd;
            row.OutputPerMillionUsd = facts.Pricing?.OutputPerMillionUsd;
            row.SupportsTools = tools;
            row.SupportsToolsSource = GatewayModelSources.Catalog;
            if (fromGateway)
            {
                row.ContextWindow = facts.ContextWindow;
                row.MaxOutputTokens = facts.MaxOutputTokens;
                row.LimitsSource = GatewayModelSources.Catalog;
            }
            else
            {
                row.LimitsSource = GatewayModelSources.Manual;
            }

            row.SyncedAt = now;
            written++;
        }

        foreach (var row in stored.Values.Where(r => r.Source == GatewayModelSources.Catalog && !listed.ContainsKey(r.Name)))
        {
            db.GatewayModels.Remove(row);
            removed++;
        }

        return new GatewayModelSyncResult(written, removed, 0);
    }

    /// <summary>The mirror's <c>ModelKind</c> of the gateway's <c>fors.kind</c> (<c>Chat</c>, <c>Embedding</c>, <c>Rerank</c>); null for a kind not mirrored.</summary>
    internal static string? ModelKindOf(string kind) => kind switch
    {
        "Chat" => GatewayModelModelKinds.Chat,
        "Embedding" => GatewayModelModelKinds.Embedding,
        "Rerank" => GatewayModelModelKinds.Rerank,
        _ => null,
    };

    /// <summary>
    /// Records the canary embedding (processing design §11) of every active profile that has a canary text and no
    /// embedding yet: the profile's model at the profile's ceiling, checked for the profile's dimension count. A recorded
    /// canary is never replaced here: it is what the worker compares against to detect a changed gateway model. Returns the
    /// number recorded; failures are kept for the status page and retried next run.
    /// </summary>
    public async Task<int> RecordCanariesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(gatewayOptions.CurrentValue.BaseUrl))
        {
            return 0; // no gateway configured: nothing can be recorded
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var missing = await db.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM config."EmbeddingProfiles"
                WHERE "State" = 'active' AND "CanaryText" IS NOT NULL AND "CanaryEmbedding" IS NULL
                """).ToListAsync(cancellationToken);
            var recorded = 0;
            foreach (var id in missing)
            {
                var profile = await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == id, cancellationToken);
                var embedding = await gateway.EmbedAsync(profile.GatewayModel, [profile.CanaryText!], profile.MaxResidency, cancellationToken,
                    profile.RequestDimensions ? profile.Dimensions : null);
                var vector = embedding.Vectors[0];
                if (vector.Length != profile.Dimensions)
                {
                    throw new InvalidDataException($"The canary of profile {profile.Name} has {vector.Length} dimensions, the profile {profile.Dimensions}.");
                }

                var text = VectorText(vector);
                var now = time.GetUtcNow();
                var written = await db.ExecuteConfigWriteAsync(async (_, ct) =>
                {
                    var rows = await db.Database.ExecuteSqlAsync($"""
                        UPDATE config."EmbeddingProfiles" SET "CanaryEmbedding" = {text}::vector, "UpdatedAt" = {now}, "UpdatedBy" = {SystemActor}
                        WHERE "Id" = {id} AND "CanaryEmbedding" IS NULL
                        """, ct);
                    if (rows == 0)
                    {
                        return false;
                    }

                    db.AuditLog.Add(new AuditLogEntry
                    {
                        Timestamp = now, Actor = SystemActor, Action = "record-canary", EntityType = "embedding-profile", EntityId = id.ToString(),
                        Service = AdminContext.ServiceName,
                        ConfigVersion = await db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(ct),
                        Details = JsonSerializer.Serialize(new AuditDetails(null, new { profile.GatewayModel, profile.Dimensions, Residency = ResidencyCeiling.ToHeader(embedding.Residency) }), AuditJson.Options),
                    });
                    await db.SaveChangesAsync(ct);
                    return true;
                }, cancellationToken, lockMetrics);
                if (written)
                {
                    recorded++;
                    LogCanaryRecorded(logger, profile.Name, embedding.Residency);
                }
            }

            _lastCanaryError = null;
            return recorded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastCanaryError = ErrorText(ex);
            LogCanaryFailed(logger, ErrorText(ex), ExceptionText.Types(ex));
            return 0;
        }
    }

    /// <summary>pgvector's text form, round-trip exact.</summary>
    internal static string VectorText(float[] vector)
    {
        var builder = new StringBuilder(vector.Length * 10 + 2).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            builder.Append(i > 0 ? "," : "").Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Shown to platform admins: the type and SQL state, HTTP status or gateway code only, never connection details.</summary>
    internal static string ErrorText(Exception ex) => ex switch
    {
        DbException { SqlState: { } state } => $"{ex.GetType().Name} (SQL state {state})",
        HttpRequestException { StatusCode: { } status } => $"{ex.GetType().Name} (HTTP {(int)status})",
        GatewayRefusedException refused => $"{ex.GetType().Name} (HTTP {refused.Status} {refused.Code})",
        GatewayUnavailableException { Status: { } status } unavailable => $"{ex.GetType().Name} (HTTP {status} {unavailable.Code})",
        _ => ex.GetType().Name,
    };

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway model sync wrote {Written} and removed {Removed} models")]
    private static partial void LogRun(ILogger logger, int written, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway model sync failed: {Error} ({Types}; {Failures} in a row); the mirror is kept")]
    private static partial void LogRunFailed(ILogger logger, string error, string types, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded the canary embedding of profile {Profile} (answered at {Residency})")]
    private static partial void LogCanaryRecorded(ILogger logger, string profile, Residency residency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording a canary embedding failed: {Error} ({Types}); it is retried at the next sync")]
    private static partial void LogCanaryFailed(ILogger logger, string error, string types);
}
