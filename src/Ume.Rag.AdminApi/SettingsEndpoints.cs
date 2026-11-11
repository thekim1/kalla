using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Pipelines.Crawl;
using Ume.Rag.Retrieval;

namespace Ume.Rag.AdminApi;

/// <summary>
/// <c>GET/PUT /api/settings/{section}</c> (admin design §4.5, V30; platform admins): sections of the singleton
/// <c>config."Settings"</c>. No row means the defaults in code (data-model §2.1); the first save creates the row. Every
/// section carries the row's <c>version</c> (one <c>xmin</c> for the singleton), so two admins editing different sections
/// at once get <c>concurrent_change</c> on the second save and reload. <c>tools</c> is P2 and not mapped.
/// </summary>
public static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettings(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var settings = api.MapGroup("/settings").WithTags("Settings");
        Section(settings, "network", NetworkSettings.Of, (NetworkSettingsRequest r, PlatformSettings s) =>
        {
            NetworkRanges.Normalize(r.TrustedProxies, out var canonical);
            s.TrustedProxies = [.. canonical];
        });
        Section(settings, "retention", RetentionSettings.Of, (RetentionSettingsRequest r, PlatformSettings s) =>
        {
            s.AuditRetentionDays = r.AuditRetentionDays;
            s.BlobPurgeDelayDays = r.BlobPurgeDelayDays;
            s.QuarantineRetentionDays = r.QuarantineRetentionDays;
        });
        Section(settings, "deletion-guard", DeletionGuardSettings.Of, (DeletionGuardSettingsRequest r, PlatformSettings s) =>
        {
            s.DeletionGuardPercent = r.Percent;
            s.DeletionGuardMinCount = r.MinCount;
            s.DeletionGuardMaxCount = r.MaxCount;
        });
        var ceilings = ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<IOptions<CrawlCeilingOptions>>();
        Section(settings, "crawl", s => new JsonSectionSettings(Json(s.Crawl), Version(s)), (JsonSectionRequest r, PlatformSettings s) => s.Crawl = r.Value.ToJsonString(),
            (r, _, _, _) =>
            {
                CrawlPolitenessRule.Ensure(r.Value, ceilings.Value.Ceilings());
                return Task.CompletedTask;
            });
        Section(settings, "evaluation", EvaluationSettings.Of, (EvaluationSettingsRequest r, PlatformSettings s) =>
        {
            s.EvaluationModel = r.EvaluationModel;
            s.EvaluationJudgeModel = r.EvaluationJudgeModel;
            s.EvaluationMaxQuestions = r.EvaluationMaxQuestions;
        }, (r, db, models, ct) => PlatformModelRules.EnsureAsync(db, models, [("Settings.EvaluationModel", r.EvaluationModel), ("Settings.EvaluationJudgeModel", r.EvaluationJudgeModel)], ct));
        Section(settings, "retrieval", s => new JsonSectionSettings(Json(s.Retrieval), Version(s)), (RetrievalSectionRequest r, PlatformSettings s) => s.Retrieval = r.Value.ToJsonString(),
            async (r, db, models, ct) =>
            {
                await PlatformModelRules.EnsureAsync(db, models, [("Settings.Retrieval.RerankModel", StringOf(r.Value["rerankModel"]))], ct);
                await PlatformModelRules.EnsureAsync(db, models, [("Settings.Retrieval.GatewayRerankModel", StringOf(r.Value["gatewayRerankModel"]))], ct,
                    GatewayModelModelKinds.Rerank);
            });
        Section(settings, "chat", s => new JsonSectionSettings(Json(s.Chat), Version(s)), (JsonSectionRequest r, PlatformSettings s) => s.Chat = r.Value.ToJsonString(),
            (r, db, models, ct) =>
            {
                // The platform answer ceiling (chat §6.3, Decisions "Answer length from the model"): absent or null, or an integer 256–1,000,000.
                if (r.Value[PlatformChatSettings.MaxAnswerTokensKey] is { } ceiling &&
                    Ume.Rag.Answering.Chat.AssistantChatSettings.TryInt(ceiling) is not (>= PlatformChatSettings.MinAnswerCeiling and <= PlatformChatSettings.MaxAnswerCeiling))
                {
                    throw AdminFaults.Field(PlatformChatSettings.MaxAnswerTokensKey, AdminErrorCodes.InvalidValue);
                }

                return PlatformModelRules.EnsureAsync(db, models,
                    [("Settings.Chat.CondenseModel", r.Value["condenseModel"] is JsonValue m && m.TryGetValue<string>(out var model) ? model : null)], ct);
            });
        Section(settings, "pii", PiiSettings.Of, (PiiSettingsRequest r, PlatformSettings s) =>
        {
            s.PiiFlagRules = r.FlagRules.ToJsonString();
            s.PiiOwnDomains = [.. r.OwnDomains];
            s.NerMinScore = r.NerMinScore;
        });
        Section(settings, "worker", WorkerSettings.Of, (WorkerSettingsRequest r, PlatformSettings s) =>
        {
            s.WorkerConcurrency = r.Concurrency.ToJsonString();
            s.DegradedFailureRatio = r.DegradedFailureRatio;
            s.ScheduleJitterMinutes = r.ScheduleJitterMinutes;
            s.PreviewMaxUsd = r.PreviewMaxUsd;
            s.DefaultSchedules = r.DefaultSchedules.ToJsonString();
        });
        return api;
    }

    /// <summary>
    /// One section: GET (the row or the defaults) and PUT (version-checked, audited <c>settings.update</c> with the section's
    /// before/after). <paramref name="check"/> runs the section's database rules (V32, with its <c>/v1/models</c> call) inside the
    /// write, before the change.
    /// </summary>
    private static void Section<TDto, TRequest>(RouteGroupBuilder group, string name, Func<PlatformSettings, TDto> read, Action<TRequest, PlatformSettings> apply,
        Func<TRequest, RagDbContext, GatewayModelCheck, CancellationToken, Task>? check = null)
        where TRequest : SettingsRequest
    {
        var title = string.Concat(name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        group.MapGet($"/{name}", async (AdminContext admin, CancellationToken ct) =>
                read(await admin.Db.Settings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new PlatformSettings()))
            .RequireAuthorization(AdminPolicies.Admin).WithName($"Get{title}Settings").WithSummary($"Platform settings: {name}");

        group.MapPut($"/{name}", async (TRequest request, AdminContext admin, AdminClientAddresses addresses, GatewayModelCheck models, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var row = await admin.Db.Settings.SingleOrDefaultAsync(attempt);
                    if (row is null)
                    {
                        if (!string.IsNullOrEmpty(request.Version) && request.Version != "0")
                        {
                            throw AdminFaults.Of(AdminErrorCodes.ConcurrentChange);
                        }

                        row = new PlatformSettings();
                        admin.Db.Settings.Add(row);
                    }
                    else
                    {
                        admin.ExpectVersion(row, request.Version);
                    }

                    if (check is not null)
                    {
                        await check(request, admin.Db, models, attempt);
                    }

                    var before = row.Version == 0 ? null : (object?)read(row);
                    apply(request, row);
                    row.UpdatedAt = admin.Now;
                    row.UpdatedBy = admin.Actor;
                    return new AdminChange<PlatformSettings>(row, "update", AuditEntityTypes.Settings, name, AuditJson.WithoutVersion(before),
                        AuditJson.WithoutVersion(read(row)));
                }, ct);
                addresses.Invalidate();
                return read(saved);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName($"Update{title}Settings").WithSummary($"Change platform settings: {name}");
    }

    private static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    internal static string Version(PlatformSettings s) => s.Version.ToString(CultureInfo.InvariantCulture);

    private static JsonObject Json(string text) => JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? [];
}

/// <summary>
/// V37 (admin design §4.6; connectors design §6.6): every host's politeness profile in the crawl section's
/// <c>hostOverrides</c> is valid and within the deployment's ceilings (<see cref="HostPolitenessRules"/>, shared with the
/// worker); the first problem answers 400 <c>crawl_politeness_invalid</c> with the host, the member and what is wrong.
/// </summary>
public static class CrawlPolitenessRule
{
    public static void Ensure(JsonObject crawl, PolitenessCeilings ceilings)
    {
        ArgumentNullException.ThrowIfNull(crawl);
        if (HostPolitenessRules.Check(crawl["hostOverrides"], ceilings) is { } problem)
        {
            throw AdminFaults.Of(AdminErrorCodes.CrawlPolitenessInvalid,
                new Dictionary<string, string> { ["host"] = problem.Host, ["field"] = problem.Field, ["problem"] = problem.Problem });
        }
    }
}

/// <summary>
/// V32 (admin design §4.6): a platform model setting names a <c>chat</c> row of <c>GatewayModels</c> servable at the platform
/// ceiling (<see cref="Ceiling"/>): the evaluation models, the condense model and the chat rerank model (Decisions "Rerank pulled
/// forward"); <c>gatewayRerankModel</c> names a <c>rerank</c> row (B-16). An empty setting is allowed (unset): rerank is
/// optional. The skill-gate model is P2 (tools section not mapped). Each set model is also listed by the gateway's
/// <c>/v1/models</c> at the platform ceiling, as V8 (#447: <c>model_not_listed</c>, <c>gateway_unavailable</c>,
/// <c>gateway_key_missing</c>).
/// </summary>
public static class PlatformModelRules
{
    /// <summary>The platform ceiling of model settings: <c>Eu</c> until on-prem models exist (M2).</summary>
    public const Residency Ceiling = Residency.Eu;

    public static async Task EnsureAsync(RagDbContext db, GatewayModelCheck models, IReadOnlyList<(string Setting, string? Model)> settings, CancellationToken ct,
        string modelKind = GatewayModelModelKinds.Chat)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        var set = settings.Where(s => !string.IsNullOrWhiteSpace(s.Model)).ToList();
        // The mirror's kind and residency first, for every setting, so a wrong model is named before the gateway is asked.
        foreach (var (setting, model) in set)
        {
            var row = await db.GatewayModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == model, ct);
            if (row is null || row.ModelKind != modelKind ||
                !row.Residencies.Any(r => ResidencyCeiling.TryParse(r, out var residency) && residency <= Ceiling))
            {
                throw AdminFaults.Of(AdminErrorCodes.SettingsModelResidency,
                    new Dictionary<string, string> { ["model"] = model!, ["setting"] = setting, ["kind"] = modelKind, ["ceiling"] = ResidencyCeiling.ToHeader(Ceiling) });
            }
        }

        foreach (var (_, model) in set)
        {
            await models.EnsureServableAsync(model!, Ceiling, ct);
        }
    }
}

/// <summary>A settings section's body: its fields and the row's <c>version</c> (null or <c>0</c> while no row exists).</summary>
public abstract record SettingsRequest : AdminRequest
{
    public string? Version { get; init; }
}

public sealed record NetworkSettings(IReadOnlyList<string> TrustedProxies, string Version)
{
    public static NetworkSettings Of(PlatformSettings s) => new(s.TrustedProxies, SettingsEndpoints.Version(s));
}

/// <summary>Reverse proxies whose <c>X-Forwarded-For</c> the admin believes: addresses or CIDR networks (copied rules, identity design §1.2).</summary>
public sealed record NetworkSettingsRequest : SettingsRequest, IValidatableObject
{
    [Required, MaxLength(NetworkRanges.MaxEntries)]
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (NetworkRanges.Normalize(TrustedProxies, out var canonical) is { } error)
        {
            yield return new ValidationResult(error, [nameof(TrustedProxies)]);
        }
        else if (canonical.Any(e => NetworkRanges.TryParse(e, out var n, out _) && NetworkRanges.IsEverything(n)))
        {
            // Every caller could then claim any address in X-Forwarded-For.
            yield return new ValidationResult("A network that covers every address (/0) cannot be a trusted proxy. Enter the proxy's own addresses.", [nameof(TrustedProxies)]);
        }
    }
}

public sealed record RetentionSettings(int? AuditRetentionDays, int BlobPurgeDelayDays, int QuarantineRetentionDays, DateTimeOffset? RetentionLastRunAt,
    string? RetentionLastRunError, string Version)
{
    public static RetentionSettings Of(PlatformSettings s) => new(s.AuditRetentionDays, s.BlobPurgeDelayDays, s.QuarantineRetentionDays, s.RetentionLastRunAt,
        s.RetentionLastRunError, SettingsEndpoints.Version(s));
}

public sealed record RetentionSettingsRequest : SettingsRequest
{
    /// <summary>Null keeps the audit log for ever; otherwise 90–3,650 days.</summary>
    [Range(90, 3650)]
    public int? AuditRetentionDays { get; init; }

    /// <summary>= the Postgres backup retention (data-model §9).</summary>
    [Range(1, 3650)]
    public int BlobPurgeDelayDays { get; init; } = 35;

    [Range(1, 3650)]
    public int QuarantineRetentionDays { get; init; } = 30;
}

public sealed record DeletionGuardSettings(short Percent, int MinCount, int MaxCount, string Version)
{
    public static DeletionGuardSettings Of(PlatformSettings s) => new(s.DeletionGuardPercent, s.DeletionGuardMinCount, s.DeletionGuardMaxCount, SettingsEndpoints.Version(s));
}

public sealed record DeletionGuardSettingsRequest : SettingsRequest, IValidatableObject
{
    [Range(1, 100)]
    public short Percent { get; init; } = 20;

    [Range(1, int.MaxValue)]
    public int MinCount { get; init; } = 5;

    [Range(1, int.MaxValue)]
    public int MaxCount { get; init; } = 500;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxCount < MinCount)
        {
            yield return new ValidationResult("The maximum count must not be below the minimum.", [nameof(MaxCount)]);
        }
    }
}

public sealed record EvaluationSettings(string? EvaluationModel, string? EvaluationJudgeModel, int EvaluationMaxQuestions, string Version)
{
    public static EvaluationSettings Of(PlatformSettings s) => new(s.EvaluationModel, s.EvaluationJudgeModel, s.EvaluationMaxQuestions, SettingsEndpoints.Version(s));
}

public sealed record EvaluationSettingsRequest : SettingsRequest
{
    /// <summary>The gateway's on-prem model for generated test questions (P1b O1); null until chosen.</summary>
    [MaxLength(200)]
    public string? EvaluationModel { get; init; }

    [MaxLength(200)]
    public string? EvaluationJudgeModel { get; init; }

    [Range(1, 10000)]
    public int EvaluationMaxQuestions { get; init; } = 100;
}

/// <summary>A <c>jsonb</c> section (crawl, chat): any JSON object; the reader (worker, chat role) applies its own defaults per key.</summary>
public sealed record JsonSectionSettings(JsonObject Value, string Version);

public record JsonSectionRequest : SettingsRequest
{
    [Required]
    public JsonObject Value { get; init; } = [];
}

/// <summary>The retrieval section is checked as the knowledge role reads it (knowledge §12.1, V16).</summary>
public sealed record RetrievalSectionRequest : JsonSectionRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        string? error = null;
        try
        {
            _ = RetrievalSettingsResolver.Platform(Value.ToJsonString());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            yield return new ValidationResult(error, [nameof(Value)]);
        }
    }
}

public sealed record PiiSettings(JsonObject FlagRules, IReadOnlyList<string> OwnDomains, decimal NerMinScore, string Version)
{
    public static PiiSettings Of(PlatformSettings s) => new(JsonNode.Parse(string.IsNullOrWhiteSpace(s.PiiFlagRules) ? "{}" : s.PiiFlagRules) as JsonObject ?? [],
        s.PiiOwnDomains, s.NerMinScore, SettingsEndpoints.Version(s));
}

public sealed record PiiSettingsRequest : SettingsRequest
{
    [Required]
    public JsonObject FlagRules { get; init; } = [];

    [MaxLength(50)]
    public IReadOnlyList<string> OwnDomains { get; init; } = [];

    [Range(0.0, 1.0)]
    public decimal NerMinScore { get; init; } = 0.80m;
}

public sealed record WorkerSettings(JsonObject Concurrency, decimal DegradedFailureRatio, int ScheduleJitterMinutes, decimal PreviewMaxUsd, JsonObject DefaultSchedules,
    string Version)
{
    public static WorkerSettings Of(PlatformSettings s) => new(Parse(s.WorkerConcurrency), s.DegradedFailureRatio, s.ScheduleJitterMinutes, s.PreviewMaxUsd,
        Parse(s.DefaultSchedules), SettingsEndpoints.Version(s));

    private static JsonObject Parse(string text) => JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? [];
}

public sealed record WorkerSettingsRequest : SettingsRequest
{
    [Required]
    public JsonObject Concurrency { get; init; } = [];

    [Range(0.0, 1.0)]
    public decimal DegradedFailureRatio { get; init; } = 0.05m;

    [Range(0, 1440)]
    public int ScheduleJitterMinutes { get; init; } = 90;

    [Range(0.0, 1000.0)]
    public decimal PreviewMaxUsd { get; init; } = 0.50m;

    [Required]
    public JsonObject DefaultSchedules { get; init; } = [];
}
