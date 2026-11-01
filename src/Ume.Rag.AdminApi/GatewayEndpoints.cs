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
/// The ceiling is capped by <c>Gateway:MaxResidency</c> first, as chat caps it (#237).
/// </summary>
public sealed class GatewayModelCheck(RagDbContext db, GatewayApi gateway, IOptions<GatewayOptions> options)
{
    /// <summary>
    /// Throws the V8 fault (<c>model_residency</c>, <c>model_not_listed</c>, <c>gateway_key_missing</c>,
    /// <c>gateway_unavailable</c>) unless servable.
    /// </summary>
    public async Task EnsureServableAsync(string model, Residency ceiling, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ceiling = options.Value.Cap(ceiling);
        var parameters = new Dictionary<string, string> { ["model"] = model, ["ceiling"] = ResidencyCeiling.ToHeader(ceiling) };
        var row = await db.GatewayModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == model, cancellationToken);
        if (row is null || !row.Residencies.Any(r => ResidencyCeiling.TryParse(r, out var residency) && residency <= ceiling))
        {
            throw AdminFaults.Of(AdminErrorCodes.ModelResidency, parameters);
        }

        IReadOnlySet<string> listed;
        try
        {
            listed = await gateway.ListModelsAsync(ceiling, cancellationToken);
        }
        catch (GatewayKeyMissingException)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayKeyMissing, parameters);
        }
        catch (Exception ex) when (ex is GatewayUnavailableException or GatewayRefusedException or GatewayResidencyException or GatewayCallRefusedException)
        {
            throw AdminFaults.Of(AdminErrorCodes.GatewayUnavailable, parameters);
        }

        if (!listed.Contains(model))
        {
            throw AdminFaults.Of(AdminErrorCodes.ModelNotListed, parameters);
        }
    }
}

/// <summary>
/// One level's key check: one embedding call at that ceiling (admin design §4.5 <c>GET /api/gateway/status</c>).
/// <c>Outcome</c>: <c>ok</c>, <c>not_configured</c> (no key here), <c>refused</c> (4xx), <c>unavailable</c>, <c>residency_refused</c>;
/// <c>Residency</c> the residency that answered (or the refused one), <c>Code</c> the gateway's error code.
/// </summary>
public sealed record GatewayLevelStatus(string Level, string Outcome, string? Residency, int? HttpStatus, string? Code, double? LatencyMs);

/// <summary>A profile's canary state.</summary>
public sealed record EmbeddingProfileCanaryStatus(Guid Id, string Name, string GatewayModel, int Dimensions, string MaxResidency, bool CanaryRecorded);

/// <summary>The gateway as this admin instance sees it.</summary>
public sealed record GatewayStatus(bool Configured, string? Model, IReadOnlyList<GatewayLevelStatus> Levels, GatewayModelSyncStatus Sync,
    IReadOnlyList<EmbeddingProfileCanaryStatus> Profiles);

/// <summary>A row of the model mirror.</summary>
public sealed record GatewayModelDto(string Name, string Kind, IReadOnlyList<string> Residencies, string Source, bool SupportsTools, int? ContextWindow,
    decimal? InputPerMillionUsd, decimal? CachedInputPerMillionUsd, decimal? OutputPerMillionUsd, DateTimeOffset? SyncedAt);

/// <summary>The gateway area (admin design §4.5): the model mirror, its sync, and the key status per level.</summary>
public static class GatewayEndpoints
{
    /// <summary>The input of a status call: fixed, carries no content.</summary>
    public const string StatusProbeText = "status";

    public static RouteGroupBuilder MapGateway(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var gateway = api.MapGroup("/gateway").WithTags("Gateway");

        gateway.MapGet("/models", async (RagDbContext db, CancellationToken ct) =>
            (await db.GatewayModels.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct))
                .Select(m => new GatewayModelDto(m.Name, m.Kind, m.Residencies, m.Source, m.SupportsTools, m.ContextWindow, m.InputPerMillionUsd,
                    m.CachedInputPerMillionUsd, m.OutputPerMillionUsd, m.SyncedAt)).ToList())
            .RequireAuthorization(AdminPolicies.Read).WithName("ListGatewayModels").WithSummary("The gateway model mirror with residencies and prices");

        gateway.MapPost("/sync", async (HttpContext http, GatewayModelSync sync, CancellationToken ct) =>
            !sync.Enabled
                ? throw AdminFaults.Of(AdminErrorCodes.SyncOff)
                : await sync.RunAsync(AdminActor.Of(http), ct) is { } result
                    ? Results.Ok(result)
                    : throw AdminFaults.Of(AdminErrorCodes.SyncFailed, new Dictionary<string, string> { ["error"] = sync.Status.LastError ?? "" }))
            .RequireAuthorization(AdminPolicies.Admin).WithName("SyncGatewayModels").WithSummary("Sync the model catalogue from the gateway now");

        gateway.MapGet("/status", StatusAsync)
            .RequireAuthorization(AdminPolicies.Admin).WithName("GatewayStatus").WithSummary("Per level key: one embedding call at that ceiling; sync and canary state");

        return api;
    }

    /// <summary>
    /// Per level key held by this instance, one embedding of a fixed word with the default profile's model at that ceiling
    /// (the delegation echo is added with gateway release 2, P2). A level whose ceiling is below the model's residency is
    /// refused by the gateway (403 <c>max_residency_not_met</c>), which the result shows as such; any other failure of one
    /// level is reported as <c>error</c> with <see cref="GatewayModelSync.ErrorText"/>, so the page always answers.
    /// </summary>
    private static async Task<GatewayStatus> StatusAsync(RagDbContext db, GatewayApi gateway, GatewayModelSync sync, IOptions<GatewayOptions> options,
        CancellationToken ct)
    {
        var profiles = await db.EmbeddingProfiles.AsNoTracking().Where(p => p.State == EmbeddingProfileStates.Active)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
        var recorded = (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM config."EmbeddingProfiles" WHERE "CanaryEmbedding" IS NOT NULL
            """).ToListAsync(ct)).ToHashSet();
        var canaries = profiles.Select(p => new EmbeddingProfileCanaryStatus(p.Id, p.Name, p.GatewayModel, p.Dimensions,
            ResidencyCeiling.ToHeader(p.MaxResidency), recorded.Contains(p.Id))).ToList();
        var configured = !string.IsNullOrEmpty(options.Value.BaseUrl);
        var model = profiles.FirstOrDefault()?.GatewayModel;
        var dimensions = profiles.FirstOrDefault() is { RequestDimensions: true } first ? first.Dimensions : (int?)null;
        var levels = new List<GatewayLevelStatus>();
        foreach (var level in Enum.GetValues<Residency>())
        {
            var name = ResidencyCeiling.ToHeader(level);
            if (!configured || model is null || string.IsNullOrEmpty(options.Value.Keys.GetValueOrDefault(level)))
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

        return new GatewayStatus(configured, model, levels, sync.Status, canaries);
    }
}
