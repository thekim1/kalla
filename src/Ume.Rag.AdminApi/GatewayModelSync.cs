using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.ServiceDefaults;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>A provider account of the gateway catalogue (<c>/v1/catalog/providers</c>).</summary>
public sealed record CatalogProviderItem(Guid Id, string Name, Residency Residency, bool IsEnabled);

/// <summary>A model deployment of the gateway catalogue (<c>/v1/catalog/models</c>); <see cref="Kind"/> is the gateway's <c>ModelKind</c>.</summary>
public sealed record CatalogModelItem(Guid Id, string Name, string Kind, Guid ProviderId, bool IsEnabled);

/// <summary>A price row of the gateway catalogue (<c>/v1/catalog/prices</c>), per million tokens in USD.</summary>
public sealed record CatalogPriceItem(Guid ModelId, DateTimeOffset EffectiveFrom, decimal? InputPerMillionUsd, decimal? CachedInputPerMillionUsd, decimal? OutputPerMillionUsd);

/// <summary>What one run changed: mirror rows added or changed, removed, and canaries recorded.</summary>
public sealed record GatewayModelSyncResult(int Written, int Removed, int CanariesRecorded)
{
    public bool Changed => Written + Removed > 0;
}

/// <summary>The last runs (admin design §4.10), held in memory per instance.</summary>
public sealed record GatewayModelSyncStatus(bool Enabled, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures,
    string? LastError, GatewayModelSyncResult? LastResult, string? LastCanaryError);

/// <summary>
/// Mirrors the gateway's model catalogue into <c>config."GatewayModels"</c> (admin design §4.10; P1a item 13) every
/// <c>OrgSync:IntervalMinutes</c> and on demand, with the same Data API key and the same pattern as
/// <see cref="OrganisationSync"/>: one config write under the version lock and an advisory lock, only changed rows written
/// (data-model §2.1: otherwise every run bumps the config version), one audit entry. A row per enabled chat or embedding
/// deployment of an enabled provider: <c>Kind = model</c>, <c>ModelKind</c> from the catalogue's <c>Kind</c>, <c>Residencies = [provider residency]</c>, the latest price in
/// effect. A catalogue row whose deployment is gone or disabled is removed; manual rows (route aliases) are never touched.
/// After each run the canary embedding of every active profile that has none is recorded (<see cref="RecordCanariesAsync"/>).
/// </summary>
public sealed partial class GatewayModelSync(
    IServiceScopeFactory scopes, IHttpClientFactory httpClients, GatewayApi gateway, IOptions<OrgSyncOptions> options,
    IOptions<GatewayOptions> gatewayOptions, TimeProvider time, IMeterFactory meters, ConfigLockMetrics lockMetrics, ILogger<GatewayModelSync> logger) : BackgroundService
{
    /// <summary>The audit actor of a run and of a recorded canary.</summary>
    public const string SystemActor = "system:gateway-sync";

    /// <summary>Advisory lock key of the sync ("RAG0" + 0x11), one writer across instances.</summary>
    public const long LockKey = 0x5241_4730_0011;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Counter<long> _runs = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.admin.sync", description: "Organisation and model catalogue sync runs by kind and outcome");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRunAt;
    private DateTimeOffset? _lastSuccessAt;
    private int _failures;
    private string? _lastError;
    private string? _lastCanaryError;
    private GatewayModelSyncResult? _lastResult;

    public bool Enabled => options.Value.BaseUrl is not null;

    public GatewayModelSyncStatus Status => new(Enabled, _lastRunAt, _lastSuccessAt, Volatile.Read(ref _failures), _lastError, _lastResult, _lastCanaryError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAsync(null, stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(options.Value.IntervalMinutes), time, stoppingToken);
        }
    }

    /// <summary>
    /// One run, by <paramref name="actor"/> or the schedule (null): the mirror when the Data API is configured, then the
    /// canaries. Never throws except on shutdown; returns null when the mirror could not be synced.
    /// </summary>
    public async Task<GatewayModelSyncResult?> RunAsync(AdminActor? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _lastRunAt = time.GetUtcNow();
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
            var providers = await FetchAsync<CatalogProviderItem>("v1/catalog/providers", cancellationToken);
            var models = await FetchAsync<CatalogModelItem>("v1/catalog/models", cancellationToken);
            var prices = await FetchAsync<CatalogPriceItem>("v1/catalog/prices", cancellationToken);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var outcome = await db.ExecuteConfigWriteAsync(async (_, ct) =>
            {
                if (!await db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock({LockKey}) AS "Value" """).SingleAsync(ct))
                {
                    return new RunOutcome(null, null); // another instance is syncing
                }

                var changed = await ApplyAsync(db, providers, models, prices, time.GetUtcNow(), ct);
                if (!changed.Changed)
                {
                    return new RunOutcome(changed, null);
                }

                await db.SaveChangesAsync(ct);
                var audit = new AuditLogEntry
                {
                    Timestamp = time.GetUtcNow(), Actor = SystemActor, Action = "sync", EntityType = "gateway-models", Service = AdminContext.ServiceName,
                    ConfigVersion = await db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(ct),
                    Details = JsonSerializer.Serialize(new AuditDetails(null, changed), AuditJson.Options),
                };
                actor?.Stamp(audit);
                db.AuditLog.Add(audit);
                await db.SaveChangesAsync(ct);
                return new RunOutcome(changed, audit);
            }, cancellationToken, lockMetrics);

            if (outcome.Audit is { } entry)
            {
                SecurityEvents.AdminChange(SecurityEvents.CreateLogger(scope.ServiceProvider.GetRequiredService<ILoggerFactory>()), entry.Actor,
                    entry.Action, entry.EntityType, entry.EntityId);
            }

            _lastSuccessAt = time.GetUtcNow();
            _lastError = null;
            Volatile.Write(ref _failures, 0);
            _runs.Add(1, new("kind", "models"), new("outcome", outcome.Result is null ? "skipped" : outcome.Result.Changed ? "changed" : "unchanged"));
            LogRun(logger, outcome.Result?.Written ?? 0, outcome.Result?.Removed ?? 0);
            return outcome.Result ?? new GatewayModelSyncResult(0, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastError = ErrorText(ex);
            var failures = Interlocked.Increment(ref _failures);
            _runs.Add(1, new("kind", "models"), new("outcome", "failed"));
            LogRunFailed(logger, ErrorText(ex), ExceptionText.Types(ex), failures); // never the messages: they can name the gateway (#232)
            return null;
        }
    }

    /// <summary>
    /// Brings the tracked mirror in line with the catalogue without saving: adds and updates catalogue rows (with
    /// <c>SyncedAt</c>) and removes catalogue rows no longer served. Manual rows, and the manual <c>SupportsTools</c> and
    /// <c>ContextWindow</c> of catalogue rows, are left as they are.
    /// </summary>
    public static async Task<GatewayModelSyncResult> ApplyAsync(RagDbContext db, IReadOnlyList<CatalogProviderItem> providers,
        IReadOnlyList<CatalogModelItem> models, IReadOnlyList<CatalogPriceItem> prices, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(prices);
        var enabledProviders = providers.Where(p => p.IsEnabled).ToDictionary(p => p.Id);
        var latestPrice = prices.Where(p => p.EffectiveFrom <= now).GroupBy(p => p.ModelId)
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.EffectiveFrom)!);
        var served = models
            .Where(m => m.IsEnabled && m.Kind is "Chat" or "Embedding" or "Rerank" && enabledProviders.ContainsKey(m.ProviderId))
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var stored = await db.GatewayModels.ToDictionaryAsync(m => m.Name, StringComparer.Ordinal, ct);
        int written = 0, removed = 0;

        foreach (var (name, deployments) in served)
        {
            if (stored.TryGetValue(name, out var row) && row.Source == GatewayModelSources.Manual)
            {
                continue; // a manual row (route alias) with the same name wins
            }

            var residencies = deployments.Select(d => enabledProviders[d.ProviderId].Residency).Distinct().Order()
                .Select(ResidencyCeiling.ToHeader).ToArray();
            var price = deployments.Select(d => latestPrice.GetValueOrDefault(d.Id)).OfType<CatalogPriceItem>().MaxBy(p => p.EffectiveFrom);
            var modelKind = ModelKindOf(deployments[0].Kind);
            if (row is null)
            {
                row = new GatewayModel { Name = name, Source = GatewayModelSources.Catalog };
                db.GatewayModels.Add(row);
            }
            else if (row.Kind == GatewayModelKinds.Model && row.ModelKind == modelKind && row.Residencies.SequenceEqual(residencies, StringComparer.Ordinal) &&
                     row.InputPerMillionUsd == price?.InputPerMillionUsd && row.CachedInputPerMillionUsd == price?.CachedInputPerMillionUsd &&
                     row.OutputPerMillionUsd == price?.OutputPerMillionUsd)
            {
                continue;
            }

            row.Kind = GatewayModelKinds.Model;
            row.ModelKind = modelKind;
            row.Residencies = residencies;
            row.InputPerMillionUsd = price?.InputPerMillionUsd;
            row.CachedInputPerMillionUsd = price?.CachedInputPerMillionUsd;
            row.OutputPerMillionUsd = price?.OutputPerMillionUsd;
            row.SyncedAt = now;
            written++;
        }

        foreach (var row in stored.Values.Where(r => r.Source == GatewayModelSources.Catalog && !served.ContainsKey(r.Name)))
        {
            db.GatewayModels.Remove(row);
            removed++;
        }

        return new GatewayModelSyncResult(written, removed, 0);
    }

    /// <summary>The mirror's <c>ModelKind</c> of a catalogue <c>Kind</c> (gateway <c>ModelKind</c>: <c>Chat</c>, <c>Embedding</c> or <c>Rerank</c>; others are not mirrored).</summary>
    internal static string ModelKindOf(string catalogKind) => catalogKind switch
    {
        "Embedding" => GatewayModelModelKinds.Embedding,
        "Rerank" => GatewayModelModelKinds.Rerank,
        _ => GatewayModelModelKinds.Chat,
    };

    /// <summary>
    /// Records the canary embedding (processing design §11) of every active profile that has a canary text and no
    /// embedding yet: the profile's model at the profile's ceiling, checked for the profile's dimension count. A recorded
    /// canary is never replaced here: it is what the worker compares against to detect a changed gateway model. Returns the
    /// number recorded; failures are kept for the status page and retried next run.
    /// </summary>
    public async Task<int> RecordCanariesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(gatewayOptions.Value.BaseUrl))
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

    private async Task<IReadOnlyList<T>> FetchAsync<T>(string path, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(OrganisationSync.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Value.BaseUrl!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.DataApiKey);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<CatalogPage<T>>(Json, ct);
        return page?.Items ?? throw new InvalidDataException($"{path} returned no items.");
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private sealed record CatalogPage<T>(IReadOnlyList<T>? Items);

    /// <summary>A run's result (null: another instance held the lock) and its audit entry (null: nothing changed).</summary>
    private sealed record RunOutcome(GatewayModelSyncResult? Result, AuditLogEntry? Audit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway model sync wrote {Written} and removed {Removed} models")]
    private static partial void LogRun(ILogger logger, int written, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway model sync failed: {Error} ({Types}; {Failures} in a row); the mirror is kept")]
    private static partial void LogRunFailed(ILogger logger, string error, string types, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded the canary embedding of profile {Profile} (answered at {Residency})")]
    private static partial void LogCanaryRecorded(ILogger logger, string profile, Residency residency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording a canary embedding failed: {Error} ({Types}); it is retried at the next sync")]
    private static partial void LogCanaryFailed(ILogger logger, string error, string types);
}
