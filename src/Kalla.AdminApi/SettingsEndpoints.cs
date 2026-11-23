using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;
using Kalla.Infrastructure.Persistence;
using Kalla.Infrastructure.Security;
using Kalla.Pipelines.Crawl;
using Kalla.Retrieval;

namespace Kalla.AdminApi;

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
            s.EvaluationAnswerQuestions = r.EvaluationAnswerQuestions;
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
                    Kalla.Answering.Chat.AssistantChatSettings.TryInt(ceiling) is not (>= PlatformChatSettings.MinAnswerCeiling and <= PlatformChatSettings.MaxAnswerCeiling))
                {
                    throw AdminFaults.Field(PlatformChatSettings.MaxAnswerTokensKey, AdminErrorCodes.InvalidValue);
                }

                // V38: the platform's prompt texts (chat §7.3, Decisions "Chat prompt texts configurable").
                PromptTextRules.Ensure(r.Value[Kalla.Answering.Chat.PlatformChatSettings.PromptsKey]);
                return PlatformModelRules.EnsureAsync(db, models,
                    [("Settings.Chat.CondenseModel", r.Value["condenseModel"] is JsonValue m && m.TryGetValue<string>(out var model) ? model : null)], ct);
            });
        // Decisions "Role groups as a setting" (M-17 C10): which IdP groups grant which role, read at each sign-in.
        Section(settings, "identity", IdentitySettings.Of, (IdentitySettingsRequest r, PlatformSettings s) => s.IdentityRoleGroups = r.ToJson());
        // Identity design §19: kalla-admin's and Open WebUI's sign-in values with their sources. Read-only here: they change only in setup
        // mode, Open WebUI's provider name excepted (the openwebui section below).
        settings.MapGet("/sign-in", async (SignInSettings signIn, OpenWebUiSignInSettings openWebUi, CancellationToken ct) =>
                SignInSettingsDto.Of(await signIn.ReadAsync(ct)) with { OpenWebUi = await openWebUi.ShowAsync(ct) })
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetSignInSettings").WithSummary("Platform settings: sign-in values and where each comes from");
        // Open WebUI's sign-in button label, the one sign-in value an admin session may change (identity design §19); a deployment value wins and locks it.
        var configuration = ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<IConfiguration>();
        Section(settings, "openwebui", OpenWebUiSettings.Of, (OpenWebUiSettingsRequest r, PlatformSettings s) =>
                s.OpenWebUiOidc = (OpenWebUiOidcValues.Parse(s.OpenWebUiOidc) with { ProviderName = r.ProviderName }).Normalized().ToJson(),
            (r, _, _, _) => r.ProviderName is not null && OpenWebUiSignIn.Deploys(configuration, OpenWebUiSignIn.ProviderNameKey)
                ? throw AdminFaults.Field("providerName", AdminErrorCodes.InvalidValue)
                : Task.CompletedTask);
        Section(settings, "pii", PiiSettings.Of, (PiiSettingsRequest r, PlatformSettings s) =>
        {
            s.PiiFlagRules = r.FlagRules.ToJsonString();
            s.PiiOwnDomains = [.. r.OwnDomains];
            s.NerMinScore = r.NerMinScore;
        });
        Section(settings, "worker", WorkerSettings.Of, (WorkerSettingsRequest r, PlatformSettings s) =>
        {
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

public sealed record EvaluationSettings(string? EvaluationModel, string? EvaluationJudgeModel, int EvaluationMaxQuestions, int EvaluationAnswerQuestions, string Version)
{
    public static EvaluationSettings Of(PlatformSettings s) =>
        new(s.EvaluationModel, s.EvaluationJudgeModel, s.EvaluationMaxQuestions, s.EvaluationAnswerQuestions, SettingsEndpoints.Version(s));
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

    /// <summary>
    /// The nightly answer run's questions per assistant and night; 0 switches the run off for every assistant (Decisions
    /// "Nightly answer run", admin §5.10).
    /// </summary>
    [Range(0, 100)]
    public int EvaluationAnswerQuestions { get; init; } = 100;
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

/// <summary>
/// The personal-data section. <see cref="FlagRules"/> is checked as the worker reads it (<c>EngineSettings</c>,
/// <c>ContractJson</c> into <see cref="Kalla.Pipelines.Processing.PiiFlagRules"/>, processing design §9.4): a shape the
/// worker cannot read, an unknown type or a threshold below 1 is a field error here, not a failed run later.
/// </summary>
public sealed record PiiSettingsRequest : SettingsRequest, IValidatableObject
{
    [Required]
    public JsonObject FlagRules { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        string? error = null;
        if (FlagRules.Count > 0)
        {
            try
            {
                var rules = Kalla.Pipelines.Json.ContractJson.Deserialize<Kalla.Pipelines.Processing.PiiFlagRules>(
                    System.Text.Json.JsonSerializer.SerializeToElement(FlagRules));
                var unknown = rules.Types.Keys.Where(t => !Kalla.Pipelines.Processing.PiiTypes.All.Contains(t, StringComparer.Ordinal)).ToList();
                if (unknown.Count > 0)
                {
                    error = $"Unknown personal-data type: {string.Join(", ", unknown)}. Known: {string.Join(", ", Kalla.Pipelines.Processing.PiiTypes.All)}.";
                }
                else if (rules.Types.Values.Any(r => r.Flag == Kalla.Pipelines.Processing.PiiFlagMode.Threshold && r.Min < 1))
                {
                    error = "A threshold is at least 1.";
                }
                else if (rules.ListLike is { } list && (list.MinNames < 1 || list.MinContacts < 1))
                {
                    error = "The list rule's numbers are at least 1.";
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                error = $"The flag rules cannot be read: {ex.Message}";
            }
        }

        if (error is not null)
        {
            yield return new ValidationResult(error, [nameof(FlagRules)]);
        }
    }

    [MaxLength(50)]
    public IReadOnlyList<string> OwnDomains { get; init; } = [];

    [Range(0.0, 1.0)]
    public decimal NerMinScore { get; init; } = 0.80m;
}

public sealed record WorkerSettings(decimal DegradedFailureRatio, int ScheduleJitterMinutes, decimal PreviewMaxUsd, JsonObject DefaultSchedules, string Version)
{
    public static WorkerSettings Of(PlatformSettings s) => new(s.DegradedFailureRatio, s.ScheduleJitterMinutes, s.PreviewMaxUsd, Parse(s.DefaultSchedules),
        SettingsEndpoints.Version(s));

    private static JsonObject Parse(string text) => JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? [];
}

public sealed record WorkerSettingsRequest : SettingsRequest, IValidatableObject
{
    [Range(0.0, 1.0)]
    public decimal DegradedFailureRatio { get; init; } = 0.05m;

    [Range(0, 1440)]
    public int ScheduleJitterMinutes { get; init; } = 90;

    [Range(0.0, 1000.0)]
    public decimal PreviewMaxUsd { get; init; } = 0.50m;

    [Required]
    public JsonObject DefaultSchedules { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The worker reads the object as source type → cron string and skips a schedule that does not parse.
        foreach (var (type, value) in DefaultSchedules)
        {
            if (value is not JsonValue schedule || !schedule.TryGetValue<string>(out var cron) || !CronSchedules.IsValid(cron))
            {
                yield return new ValidationResult(CronSchedules.Invalid, [$"{nameof(DefaultSchedules)}.{type}"]);
            }
        }
    }
}

/// <summary>
/// <c>Settings.Identity.RoleGroups</c> (Decisions "Role groups as a setting", M-17 C10): per role the IdP groups that grant it
/// at sign-in. The deployment's <c>Oidc:RoleGroups:rag-platform-admin</c> stays as the break-glass and is merged in, never
/// replaced (<see cref="RoleGroupMap"/>); a role in the token's role claim still counts.
/// </summary>
public sealed record IdentitySettings(IReadOnlyList<string> PlatformAdmin, IReadOnlyList<string> DepartmentAdmin, IReadOnlyList<string> PipelineEditor,
    IReadOnlyList<string> Reviewer, IReadOnlyList<string> Editor, IReadOnlyList<string> Viewer, string Version)
{
    public static IdentitySettings Of(PlatformSettings s)
    {
        var map = RoleGroupMap.Parse(s.IdentityRoleGroups);
        IReadOnlyList<string> Of(string role) => map.TryGetValue(role, out var groups) ? groups : [];
        return new(Of(RagRoles.PlatformAdmin), Of(RagRoles.DepartmentAdmin), Of(RagRoles.PipelineEditor), Of(RagRoles.Reviewer), Of(RagRoles.Editor),
            Of(RagRoles.Viewer), SettingsEndpoints.Version(s));
    }
}

public sealed record IdentitySettingsRequest : SettingsRequest, IValidatableObject
{
    [MaxLength(50)]
    public IReadOnlyList<string> PlatformAdmin { get; init; } = [];

    [MaxLength(50)]
    public IReadOnlyList<string> DepartmentAdmin { get; init; } = [];

    [MaxLength(50)]
    public IReadOnlyList<string> PipelineEditor { get; init; } = [];

    [MaxLength(50)]
    public IReadOnlyList<string> Reviewer { get; init; } = [];

    [MaxLength(50)]
    public IReadOnlyList<string> Editor { get; init; } = [];

    [MaxLength(50)]
    public IReadOnlyList<string> Viewer { get; init; } = [];

    private IEnumerable<(string Field, string Role, IReadOnlyList<string> Groups)> Roles() =>
    [
        (nameof(PlatformAdmin), RagRoles.PlatformAdmin, PlatformAdmin), (nameof(DepartmentAdmin), RagRoles.DepartmentAdmin, DepartmentAdmin),
        (nameof(PipelineEditor), RagRoles.PipelineEditor, PipelineEditor), (nameof(Reviewer), RagRoles.Reviewer, Reviewer),
        (nameof(Editor), RagRoles.Editor, Editor), (nameof(Viewer), RagRoles.Viewer, Viewer),
    ];

    /// <summary>The stored map: each role's trimmed, distinct groups; roles without groups left out.</summary>
    public string ToJson()
    {
        var map = new JsonObject();
        foreach (var (_, role, groups) in Roles())
        {
            var clean = groups.Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (clean.Count > 0)
            {
                map[role] = new JsonArray([.. clean.Select(g => (JsonNode?)g)]);
            }
        }

        return map.ToJsonString();
    }

    /// <summary>The deployment's rule (#413): a role group is a <c>rag-</c> name or an Entra object id.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (field, _, groups) in Roles())
        {
            foreach (var group in groups.Select(g => g.Trim()).Where(g => g.Length > 0))
            {
                if (group.Length > 200 || !OidcOptionsValidator.IsRoleGroupName(group))
                {
                    yield return new ValidationResult(
                        $"'{group}' is not a role group: it must start with '{OidcOptionsValidator.RoleGroupPrefix}' or be an Entra object id, so that a gateway group of the same name grants no role.",
                        [field]);
                }
            }
        }
    }
}

/// <summary>
/// The <c>openwebui</c> section: Open WebUI's sign-in button label as the wizard's setting stores it (null: the deployment's or the
/// default applies; <c>GET /api/settings/sign-in</c> shows the effective one and its source).
/// </summary>
public sealed record OpenWebUiSettings(string? ProviderName, string Version)
{
    public static OpenWebUiSettings Of(PlatformSettings s) => new(OpenWebUiOidcValues.Parse(s.OpenWebUiOidc).ProviderName, SettingsEndpoints.Version(s));
}

/// <summary>Sets <c>Settings.OpenWebUi.Oidc.ProviderName</c>; null or blank clears it. Refused while the deployment sets the name. Open WebUI follows within a minute (it restarts).</summary>
public sealed record OpenWebUiSettingsRequest : SettingsRequest, IValidatableObject
{
    [MaxLength(100)]
    public string? ProviderName { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProviderName?.Any(char.IsControl) == true)
        {
            yield return new ValidationResult("The value may not hold control characters.", [nameof(ProviderName)]);
        }
    }
}

/// <summary>Role name → groups, from <c>Settings.Identity.RoleGroups</c> and the deployment's <c>Oidc:RoleGroups</c>.</summary>
public static class RoleGroupMap
{
    /// <summary>The stored setting; anything that is not a role-to-groups object reads as empty.</summary>
    public static Dictionary<string, string[]> Parse(string? json)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject root)
        {
            return map;
        }

        foreach (var (role, node) in root)
        {
            if (node is JsonArray groups)
            {
                map[role] = [.. groups.Select(g => g?.GetValue<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!.Trim())];
            }
        }

        return map;
    }

    /// <summary>
    /// The setting merged with the deployment's <c>Oidc:RoleGroups</c> (its <c>;</c>-separated values): a group in either grants
    /// the role, so the break-glass works whatever the setting says.
    /// </summary>
    public static Dictionary<string, string[]> Merge(IReadOnlyDictionary<string, string> deployment, IReadOnlyDictionary<string, string[]> setting)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(setting);
        var merged = setting.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        foreach (var (role, groups) in deployment)
        {
            var split = groups.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            merged[role] = [.. (merged.TryGetValue(role, out var existing) ? existing : []).Concat(split).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return merged;
    }
}
