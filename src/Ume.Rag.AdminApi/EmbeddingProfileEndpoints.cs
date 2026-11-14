using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// An embedding profile as the admin UI sees it (admin design §4.5). <see cref="InUse"/>: a <c>ChunkSets</c> row of any state
/// names the profile, so only <see cref="MaxResidency"/> may change (Decisions "Editable embedding profiles before use").
/// </summary>
public sealed record EmbeddingProfileDto(Guid Id, string Name, string GatewayModel, int Dimensions, bool RequestDimensions, int MaxInputTokens,
    string MaxResidency, string Distance, bool IsDefault, string State, bool InUse, bool CanaryRecorded, DateTimeOffset? CanaryCheckedAt,
    double? CanarySimilarity, DateTimeOffset UpdatedAt, string? UpdatedBy, string Version)
{
    public static EmbeddingProfileDto Of(EmbeddingProfile p, bool inUse, bool canaryRecorded) => new(p.Id, p.Name, p.GatewayModel, p.Dimensions,
        p.RequestDimensions, p.MaxInputTokens, ResidencyCeiling.ToHeader(p.MaxResidency), p.Distance, p.IsDefault, p.State, inUse, canaryRecorded,
        p.CanaryCheckedAt, p.CanarySimilarity, p.UpdatedAt, p.UpdatedBy, p.Version.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// The outcome of one canary embedding with a profile's values (admin design §4.5 <c>POST /api/embedding-profiles/{id}/test</c>).
/// <see cref="Error"/>: the gateway's code (<c>model_not_allowed</c>, <c>model_not_found</c>, <c>max_residency_not_met</c>, …),
/// <c>gateway_not_configured</c>, <c>gateway_key_missing</c>, <c>gateway_unavailable</c>, <c>residency_refused</c> (the answer
/// came from above the ceiling), <c>bad_vector</c> (<see cref="Dimensions"/> is the count that came back) or <c>error</c>.
/// <see cref="Residency"/>: the residency that answered (the refused one for <c>residency_refused</c>).
/// </summary>
public sealed record EmbeddingProfileTestResult(bool Ok, int? Dimensions, string? Residency, string? Error, string? Detail);

/// <summary>Every field of a profile a platform admin may set (admin design §4.5).</summary>
public sealed record EmbeddingProfileUpdateRequest : AdminRequest, IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string GatewayModel { get; init; } = "";

    [Range(1, 4000)]
    public int Dimensions { get; init; }

    public bool RequestDimensions { get; init; }

    [Range(1, 1_000_000)]
    public int MaxInputTokens { get; init; }

    /// <summary><c>OnPrem</c>, <c>Eu</c> or <c>External</c>, at most the platform ceiling (<c>Eu</c> until M2).</summary>
    [Required]
    public string MaxResidency { get; init; } = "";

    [Required]
    public string Version { get; init; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        EmbeddingProfileRules.ResidencyProblem(MaxResidency) is { } problem ? [new ValidationResult(problem, [nameof(MaxResidency)])] : [];
}

/// <summary>
/// A new profile (Decisions "Deploy configuration"; proposal deploy-configuration §2): <see cref="GatewayModel"/> is one of the
/// mirror's embedding models. Each member left out comes from the model: <see cref="MaxResidency"/> the highest residency it
/// runs at within the platform ceiling, <see cref="MaxInputTokens"/> its context window, <see cref="Dimensions"/> the length of
/// the vector the test embedding returns (required with <see cref="RequestDimensions"/>, the size the model is asked for), and
/// <see cref="Name"/> the model's name.
/// </summary>
public sealed record EmbeddingProfileCreateRequest : AdminRequest, IValidatableObject
{
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; init; }

    [Required, StringLength(200, MinimumLength = 1)]
    public string GatewayModel { get; init; } = "";

    public bool RequestDimensions { get; init; }

    [Range(1, 4000)]
    public int? Dimensions { get; init; }

    [Range(1, 1_000_000)]
    public int? MaxInputTokens { get; init; }

    /// <summary><c>OnPrem</c>, <c>Eu</c> or <c>External</c>, at most the platform ceiling (<c>Eu</c> until M2).</summary>
    public string? MaxResidency { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RequestDimensions && Dimensions is null)
        {
            yield return new ValidationResult("Required when requestDimensions is true: the size the model is asked for.", [nameof(Dimensions)]);
        }

        if (MaxResidency is not null && EmbeddingProfileRules.ResidencyProblem(MaxResidency) is { } problem)
        {
            yield return new ValidationResult(problem, [nameof(MaxResidency)]);
        }
    }
}

/// <summary>A test with values not saved yet; each member left out takes the stored value.</summary>
public sealed record EmbeddingProfileTestRequest : AdminRequest, IValidatableObject
{
    [StringLength(200, MinimumLength = 1)]
    public string? GatewayModel { get; init; }

    [Range(1, 4000)]
    public int? Dimensions { get; init; }

    public bool? RequestDimensions { get; init; }

    public string? MaxResidency { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        MaxResidency is not null && EmbeddingProfileRules.ResidencyProblem(MaxResidency) is { } problem
            ? [new ValidationResult(problem, [nameof(MaxResidency)])]
            : [];
}

internal static class EmbeddingProfileRules
{
    public static string? ResidencyProblem(string value) =>
        !ResidencyCeiling.TryParse(value, out var residency) ? "OnPrem, Eu or External."
        : residency > PlatformModelRules.Ceiling ? $"At most the platform ceiling {ResidencyCeiling.ToHeader(PlatformModelRules.Ceiling)}."
        : null;

    public static Residency Residency(string value) => ResidencyCeiling.TryParse(value, out var residency) ? residency : throw new ArgumentException(value);
}

/// <summary>
/// One embedding of a profile's canary text with given values, at the given ceiling with the admin's key of that level
/// (processing design §11). Nothing is stored here; a save stores the returned vector as the new canary. Without
/// <c>dimensions</c> (a new profile whose model is not asked for a size) the vector's length is the measured size.
/// </summary>
public sealed class EmbeddingProfileTester(GatewayApi gateway, IOptions<GatewayOptions> options)
{
    /// <summary>The sizes a profile's partial HNSW index takes (<c>content.rag_ensure_profile_index</c>).</summary>
    public const int MaxDimensions = 4000;

    public async Task<(EmbeddingProfileTestResult Result, float[]? Vector)> TestAsync(string model, int? dimensions, bool requestDimensions, Residency ceiling,
        string canaryText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BaseUrl))
        {
            return (new EmbeddingProfileTestResult(false, null, null, "gateway_not_configured", null), null);
        }

        GatewayEmbedding answer;
        try
        {
            answer = await gateway.EmbedAsync(model, [canaryText], ceiling, cancellationToken, requestDimensions ? dimensions : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var result = ex switch
            {
                GatewayKeyMissingException => new EmbeddingProfileTestResult(false, null, null, "gateway_key_missing", ResidencyCeiling.ToHeader(ceiling)),
                GatewayRefusedException refused => new EmbeddingProfileTestResult(false, null, null, refused.Code ?? "gateway_refused",
                    GatewayModelSync.ErrorText(ex)),
                GatewayUnavailableException => new EmbeddingProfileTestResult(false, null, null, "gateway_unavailable", GatewayModelSync.ErrorText(ex)),
                GatewayResidencyException residency => new EmbeddingProfileTestResult(false, null, residency.Observed, "residency_refused", null),
                _ => new EmbeddingProfileTestResult(false, null, null, "error", GatewayModelSync.ErrorText(ex)),
            };
            return (result, null);
        }

        var vector = answer.Vectors[0];
        var residencyText = ResidencyCeiling.ToHeader(answer.Residency);
        if (dimensions is { } expected && vector.Length != expected)
        {
            return (new EmbeddingProfileTestResult(false, vector.Length, residencyText, "bad_vector",
                $"{vector.Length} dimensions came back, the profile has {expected}"), null);
        }

        if (vector.Length is < 1 or > MaxDimensions)
        {
            return (new EmbeddingProfileTestResult(false, vector.Length, residencyText, "bad_vector",
                $"{vector.Length} dimensions came back; a profile takes 1 to {MaxDimensions}"), null);
        }

        if (vector.Any(f => !float.IsFinite(f)) || vector.All(f => f == 0))
        {
            return (new EmbeddingProfileTestResult(false, vector.Length, residencyText, "bad_vector", "the vector is not usable (zero or not finite)"), null);
        }

        return (new EmbeddingProfileTestResult(true, vector.Length, residencyText, null, null), vector);
    }
}

/// <summary>
/// Embedding profiles (admin design §4.5; Decisions "Editable embedding profiles before use", "Deploy configuration"): created
/// by a platform admin from the gateway's embedding models, readable by platform admins, editable while no chunk set uses the
/// profile, and then only its residency ceiling. Every create and save runs the canary test first.
/// </summary>
public static class EmbeddingProfileEndpoints
{
    public static RouteGroupBuilder MapEmbeddingProfiles(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var profiles = api.MapGroup("/embedding-profiles").WithTags("Embedding profiles");

        profiles.MapGet("", async (RagDbContext db, CancellationToken ct) =>
            {
                var rows = await db.EmbeddingProfiles.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
                var inUse = await InUseAsync(db, ct);
                var recorded = await RecordedAsync(db, ct);
                return rows.Select(p => EmbeddingProfileDto.Of(p, inUse.Contains(p.Id), recorded.Contains(p.Id))).ToList();
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("ListEmbeddingProfiles")
            .WithSummary("Every embedding profile, with whether a chunk set uses it and its canary state");

        profiles.MapPost("", CreateAsync)
            .RequireAuthorization(AdminPolicies.Admin).WithName("CreateEmbeddingProfile")
            .WithSummary("Create a profile from one of the gateway's embedding models; the test embedding measures its size and becomes its canary");

        profiles.MapGet("/{id:guid}", (Guid id, RagDbContext db, CancellationToken ct) => GetAsync(db, id, ct))
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetEmbeddingProfile").WithSummary("One embedding profile");

        profiles.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateEmbeddingProfile")
            .WithSummary("Change a profile: every field while unused, then only maxResidency; the save tests the values first and stores the new canary");

        profiles.MapPost("/{id:guid}/test", async (Guid id, EmbeddingProfileTestRequest request, RagDbContext db, EmbeddingProfileTester tester,
                CancellationToken ct) =>
            {
                var profile = await db.EmbeddingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var ceiling = request.MaxResidency is null ? profile.MaxResidency : EmbeddingProfileRules.Residency(request.MaxResidency);
                var (result, _) = await tester.TestAsync(request.GatewayModel ?? profile.GatewayModel, request.Dimensions ?? profile.Dimensions,
                    request.RequestDimensions ?? profile.RequestDimensions, ceiling, profile.CanaryText ?? EmbeddingCanaryText.Default, ct);
                return result;
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("TestEmbeddingProfile")
            .WithSummary("One embedding of the canary text with the given values (the stored ones otherwise); nothing is stored");

        return api;
    }

    private static async Task<EmbeddingProfileDto> GetAsync(RagDbContext db, Guid id, CancellationToken ct)
    {
        var profile = await db.EmbeddingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var inUse = await db.ChunkSets.AnyAsync(c => c.EmbeddingProfileId == id, ct);
        return EmbeddingProfileDto.Of(profile, inUse, (await RecordedAsync(db, ct)).Contains(id));
    }

    /// <summary>
    /// The create (Decisions "Deploy configuration"): the model must be one of the mirror's embedding models served within the
    /// platform ceiling (<c>settings_model_residency</c>); the missing values come from the model (see
    /// <see cref="EmbeddingProfileCreateRequest"/>); the test embedding must pass (<c>profile_test_failed</c> with its outcome),
    /// and its vector is the profile's canary and, unless the model is asked for a size, its measured size. The first profile
    /// becomes the default, which new collections use. A taken name is <c>already_exists</c>. The worker creates the profile's
    /// partial HNSW index before its first chunk (data-model §3.2).
    /// </summary>
    private static async Task<IResult> CreateAsync(EmbeddingProfileCreateRequest request, AdminContext admin, EmbeddingProfileTester tester,
        CancellationToken ct)
    {
        var db = admin.Db;
        var model = await db.GatewayModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == request.GatewayModel, ct);
        var residencies = model is { ModelKind: GatewayModelModelKinds.Embedding }
            ? model.Residencies.Select(r => ResidencyCeiling.TryParse(r, out var residency) ? residency : (Residency?)null)
                .Where(r => r <= PlatformModelRules.Ceiling).Select(r => r!.Value).ToList()
            : [];
        if (model is null || residencies.Count == 0)
        {
            throw AdminFaults.Field("gatewayModel", AdminErrorCodes.SettingsModelResidency, new Dictionary<string, string>
            {
                ["model"] = request.GatewayModel, ["setting"] = "the embedding profile", ["kind"] = GatewayModelModelKinds.Embedding,
                ["ceiling"] = ResidencyCeiling.ToHeader(PlatformModelRules.Ceiling),
            });
        }

        var ceiling = request.MaxResidency is null ? residencies.Max() : EmbeddingProfileRules.Residency(request.MaxResidency);
        var maxInputTokens = request.MaxInputTokens ?? model.ContextWindow ?? throw AdminFaults.Field("maxInputTokens", AdminErrorCodes.InvalidValue);
        var name = string.IsNullOrWhiteSpace(request.Name) ? model.Name[..Math.Min(model.Name.Length, 100)] : request.Name.Trim();

        var (test, vector) = await tester.TestAsync(model.Name, request.RequestDimensions ? request.Dimensions : null, request.RequestDimensions, ceiling,
            EmbeddingCanaryText.Default, ct);
        if (!test.Ok)
        {
            throw AdminFaults.Of(AdminErrorCodes.ProfileTestFailed, new Dictionary<string, string> { ["error"] = test.Error ?? "error" },
                new Dictionary<string, object?> { ["test"] = test });
        }

        var created = await admin.WriteAsync(async attempt =>
        {
            var profile = new EmbeddingProfile
            {
                Name = name,
                GatewayModel = model.Name,
                Dimensions = vector!.Length,
                RequestDimensions = request.RequestDimensions,
                MaxInputTokens = maxInputTokens,
                MaxResidency = ceiling,
                CanaryText = EmbeddingCanaryText.Default,
                IsDefault = !await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, attempt),
                UpdatedAt = admin.Now,
                UpdatedBy = admin.Actor,
            };
            db.EmbeddingProfiles.Add(profile);
            await db.SaveChangesAsync(attempt);
            var text = GatewayModelSync.VectorText(vector);
            await db.Database.ExecuteSqlAsync($"""UPDATE config."EmbeddingProfiles" SET "CanaryEmbedding" = {text}::vector WHERE "Id" = {profile.Id}""", attempt);
            return new AdminChange<Guid>(profile.Id, "create", AuditEntityTypes.EmbeddingProfile, profile.Id, null,
                new { After = Audit(profile), profile.Name, profile.IsDefault, Residency = test.Residency });
        }, ct);

        return Results.Created($"/api/embedding-profiles/{created}", await GetAsync(db, created, ct));
    }

    /// <summary>
    /// The save (admin design §4.5): field rules, <c>profile_in_use</c> for a used profile's fixed fields, the test with the new
    /// values (<c>profile_test_failed</c> with its outcome), then in one write under the profile's row lock: the fields, the new
    /// canary (its last check cleared), and, when the model, size or <c>RequestDimensions</c> changed, the profile's cached
    /// embeddings removed and its index rebuilt at the new size (<c>content.rag_reset_profile</c>). A used profile keeps its canary,
    /// and its test must match it, so a new ceiling cannot reach a provider that serves another model.
    /// </summary>
    private static async Task<EmbeddingProfileDto> UpdateAsync(Guid id, EmbeddingProfileUpdateRequest request, AdminContext admin,
        EmbeddingProfileTester tester, CancellationToken ct)
    {
        var db = admin.Db;
        var current = await db.EmbeddingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var ceiling = EmbeddingProfileRules.Residency(request.MaxResidency);
        var inUse = await db.ChunkSets.AnyAsync(c => c.EmbeddingProfileId == id, ct);
        if (inUse && FixedFieldsChange(current, request))
        {
            throw AdminFaults.Of(AdminErrorCodes.ProfileInUse, new Dictionary<string, string> { ["profile"] = current.Name });
        }

        var canaryText = current.CanaryText ?? EmbeddingCanaryText.Default;
        var (test, vector) = await tester.TestAsync(request.GatewayModel, request.Dimensions, request.RequestDimensions, ceiling, canaryText, ct);
        if (test.Ok && inUse && await RecordedCanaryAsync(db, id, ct) is { } recorded
            && EmbeddingCanaryCheck.Cosine(vector!, recorded) < EmbeddingCanaryThreshold.MinCosine)
        {
            test = test with { Ok = false, Error = "canary_mismatch", Detail = "the answer differs from the profile's recorded canary" };
        }

        if (!test.Ok)
        {
            throw AdminFaults.Of(AdminErrorCodes.ProfileTestFailed, new Dictionary<string, string> { ["error"] = test.Error ?? "error" },
                new Dictionary<string, object?> { ["test"] = test });
        }

        await admin.WriteAsync(async attempt =>
        {
            // The row lock first (#554 review): a worker inserts a chunk set only while it holds this row FOR SHARE with the
            // values it embedded with (content.rag_lock_profile_if_unchanged), so the in-use check below sees its set, or the
            // worker sees these values once this commits and inserts nothing.
            await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM config."EmbeddingProfiles" WHERE "Id" = {id} FOR UPDATE""", attempt);
            var profile = await db.EmbeddingProfiles.SingleOrDefaultAsync(p => p.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
            admin.ExpectVersion(profile, request.Version);
            var usedNow = await db.ChunkSets.AnyAsync(c => c.EmbeddingProfileId == id, attempt);
            if (usedNow && FixedFieldsChange(profile, request))
            {
                throw AdminFaults.Of(AdminErrorCodes.ProfileInUse, new Dictionary<string, string> { ["profile"] = profile.Name });
            }

            var before = Audit(profile);
            var vectorsChange = FixedVectorFieldsChange(profile, request);
            profile.GatewayModel = request.GatewayModel;
            profile.Dimensions = request.Dimensions;
            profile.RequestDimensions = request.RequestDimensions;
            profile.MaxInputTokens = request.MaxInputTokens;
            profile.MaxResidency = ceiling;
            profile.CanaryText = canaryText;
            if (!usedNow)
            {
                profile.CanaryCheckedAt = null;
                profile.CanarySimilarity = null;
            }

            profile.UpdatedAt = admin.Now;
            profile.UpdatedBy = admin.Actor;
            await db.SaveChangesAsync(attempt);
            if (!usedNow)
            {
                var text = GatewayModelSync.VectorText(vector!);
                await db.Database.ExecuteSqlAsync($"""UPDATE config."EmbeddingProfiles" SET "CanaryEmbedding" = {text}::vector WHERE "Id" = {id}""", attempt);
                if (vectorsChange)
                {
                    // Cached embeddings and the index were made with the old values.
                    await db.Database.ExecuteSqlAsync($"SELECT content.rag_reset_profile({id})", attempt);
                }
            }

            return new AdminChange<bool>(true, "update", AuditEntityTypes.EmbeddingProfile, id, before,
                new { After = Audit(profile), Residency = test.Residency, CanaryReplaced = !usedNow, Reset = !usedNow && vectorsChange });
        }, ct);

        return await GetAsync(db, id, ct);
    }

    private static bool FixedFieldsChange(EmbeddingProfile p, EmbeddingProfileUpdateRequest r) =>
        FixedVectorFieldsChange(p, r) || p.MaxInputTokens != r.MaxInputTokens;

    /// <summary>The values a vector depends on, as the worker's insert guard compares them.</summary>
    private static bool FixedVectorFieldsChange(EmbeddingProfile p, EmbeddingProfileUpdateRequest r) =>
        p.GatewayModel != r.GatewayModel || p.Dimensions != r.Dimensions || p.RequestDimensions != r.RequestDimensions;

    private static object Audit(EmbeddingProfile p) => new
    {
        p.GatewayModel, p.Dimensions, p.RequestDimensions, p.MaxInputTokens, MaxResidency = ResidencyCeiling.ToHeader(p.MaxResidency),
    };

    private static async Task<HashSet<Guid>> InUseAsync(RagDbContext db, CancellationToken ct) =>
        (await db.EmbeddingProfiles.AsNoTracking().Where(p => db.ChunkSets.Any(c => c.EmbeddingProfileId == p.Id)).Select(p => p.Id).ToListAsync(ct))
        .ToHashSet();

    private static async Task<HashSet<Guid>> RecordedAsync(RagDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM config."EmbeddingProfiles" WHERE "CanaryEmbedding" IS NOT NULL
            """).ToListAsync(ct)).ToHashSet();

    private static async Task<float[]?> RecordedCanaryAsync(RagDbContext db, Guid id, CancellationToken ct) =>
        (await db.Database.SqlQuery<float[]>($"""
            SELECT "CanaryEmbedding"::real[] AS "Value" FROM config."EmbeddingProfiles" WHERE "Id" = {id} AND "CanaryEmbedding" IS NOT NULL
            """).ToListAsync(ct)).FirstOrDefault();
}
