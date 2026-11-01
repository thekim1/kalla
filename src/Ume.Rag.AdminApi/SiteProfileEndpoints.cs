using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Schemas;
using ProfileContract = Ume.Rag.Pipelines.SiteProfiles.SiteProfile;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Site profiles (admin design §4.5 "Site profiles", connectors design §5.6; P1a item 14c): read the hosts' profiles and
/// versions, create a host's first profile, edit (a new <c>manual</c> version), roll back. <c>content."SiteProfiles"</c> is
/// the worker's, so changes are <c>command</c> jobs (<c>edit-site-profile</c>, <c>rollback-site-profile</c>, J3) carrying
/// <c>decidedBy</c> = the admin's actor (#116); the worker writes the version and re-processes the host's sources. Platform
/// admins only in P1a (the platform team sets the pilot up, admin design §12).
/// </summary>
public static class SiteProfileEndpoints
{
    public static RouteGroupBuilder MapSiteProfiles(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var profiles = api.MapGroup("/site-profiles").WithTags("Site profiles");

        profiles.MapGet("", async (string? host, string? level, AdminContext admin, CancellationToken ct) =>
            {
                var query = admin.Db.SiteProfiles.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(host))
                {
                    var wanted = host.Trim().ToLowerInvariant(); // stored hosts are lower case
                    query = query.Where(p => p.Host == wanted);
                }

                if (!string.IsNullOrWhiteSpace(level))
                {
                    query = query.Where(p => p.Classification == level);
                }

                return await (from p in query
                              join v in admin.Db.SiteProfileVersions.AsNoTracking() on p.AppliedVersionId equals v.Id into applied
                              from v in applied.DefaultIfEmpty()
                              orderby p.Host, p.Classification
                              select new SiteProfileSummary(p.Id, p.Classification, p.Host, p.PresetId, v == null ? null : v.Number, v == null ? null : v.Origin,
                                  v == null ? null : v.AppliedAt, p.UpdatedAt))
                    .ToListAsync(ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("ListSiteProfiles").WithSummary("The hosts' site profiles with their applied version");

        profiles.MapGet("/{id:guid}/versions", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                _ = await admin.Db.SiteProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                return await admin.Db.SiteProfileVersions.AsNoTracking().Where(v => v.SiteProfileId == id).OrderByDescending(v => v.Number)
                    .Select(v => new SiteProfileVersionSummary(v.Number, v.Origin, v.State, v.CreatedBy, v.CreatedAt, v.AppliedAt, v.AppliedBy)).ToListAsync(ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("ListSiteProfileVersions").WithSummary("A site profile's versions, newest first");

        profiles.MapGet("/{id:guid}/versions/{number}", async (Guid id, string number, AdminContext admin, CancellationToken ct) =>
            {
                var n = int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var version = await admin.Db.SiteProfileVersions.AsNoTracking().SingleOrDefaultAsync(v => v.SiteProfileId == id && v.Number == n, ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                using var profile = JsonDocument.Parse(version.Profile);
                return new SiteProfileVersionDto(version.Number, version.Origin, version.State, profile.RootElement.Clone(), version.CreatedBy, version.CreatedAt,
                    version.AppliedAt, version.AppliedBy);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("GetSiteProfileVersion").WithSummary("One version with its profile JSON");

        profiles.MapPost("", async (SiteProfileCreateRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var profile = Profile(request.Profile);
                var host = request.Host.Trim().ToLowerInvariant();
                return Results.Accepted(value: await admin.WriteAsync(async attempt =>
                {
                    if (await admin.Db.SiteProfiles.AsNoTracking().AnyAsync(p => p.Classification == request.Level && p.Host == host, attempt))
                    {
                        throw AdminFaults.Field("host", AdminErrorCodes.AlreadyExists);
                    }

                    var command = new EditSiteProfileCommand { Host = host, PresetId = request.PresetId, Profile = profile, DecidedBy = admin.Actor };
                    var job = new JobRequest(JobClasses.Command, await DepartmentForAsync(admin, host, attempt), request.Level, ContractJson.Serialize(command))
                    {
                        DedupKey = JobDedupKeys.SiteProfileHost(request.Level, host),
                    };
                    return new AdminChange<SiteProfileJob>(new SiteProfileJob(job.Id, null), "site-profile.create", AuditEntityTypes.SiteProfile, $"{request.Level}:{host}",
                        null, new { request.Level, host, request.PresetId, profile }) { Classification = request.Level, Jobs = [job] };
                }, ct));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("CreateSiteProfile").WithSummary("A host's first profile (command edit-site-profile)");

        profiles.MapPut("/{id:guid}", async (Guid id, SiteProfileEditRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var profile = Profile(request.Profile);
                return Results.Accepted(value: await admin.WriteAsync(async attempt =>
                {
                    var (current, applied) = await LoadAsync(admin, id, attempt);
                    // The optimistic check is the applied version's number (the worker refuses a stale base: site_profile.version_conflict).
                    if (applied?.Number != request.Version)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.ConcurrentChange, extensions: new Dictionary<string, object?> { ["version"] = applied?.Number });
                    }

                    var command = new EditSiteProfileCommand
                    {
                        SiteProfileId = current.Id, Host = current.Host, BaseVersion = applied?.Number, PresetId = request.PresetId ?? current.PresetId, Profile = profile,
                        DecidedBy = admin.Actor,
                    };
                    var job = new JobRequest(JobClasses.Command, await DepartmentForAsync(admin, current.Host, attempt), current.Classification, ContractJson.Serialize(command))
                    {
                        DedupKey = JobDedupKeys.SiteProfile(current.Id),
                    };
                    return new AdminChange<SiteProfileJob>(new SiteProfileJob(job.Id, current.Id), "site-profile.edit", AuditEntityTypes.SiteProfile, current.Id,
                        new { version = applied?.Number }, new { baseVersion = applied?.Number, command.PresetId, profile })
                    {
                        Classification = current.Classification, Jobs = [job],
                    };
                }, ct));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("EditSiteProfile").WithSummary("A new manual version of a host's profile (command edit-site-profile)");

        profiles.MapPost("/{id:guid}/rollback", async (Guid id, SiteProfileRollbackRequest request, AdminContext admin, CancellationToken ct) =>
                Results.Accepted(value: await admin.WriteAsync(async attempt =>
                {
                    var (current, applied) = await LoadAsync(admin, id, attempt);
                    if (!await admin.Db.SiteProfileVersions.AsNoTracking().AnyAsync(v => v.SiteProfileId == id && v.Number == request.ToVersion, attempt))
                    {
                        throw AdminFaults.Field("toVersion", AdminErrorCodes.InvalidValue);
                    }

                    if (applied?.Number == request.ToVersion)
                    {
                        throw AdminFaults.Field("toVersion", AdminErrorCodes.SiteProfileVersionApplied);
                    }

                    var command = new RollbackSiteProfileCommand { SiteProfileId = id, ToVersion = request.ToVersion, DecidedBy = admin.Actor };
                    var job = new JobRequest(JobClasses.Command, await DepartmentForAsync(admin, current.Host, attempt), current.Classification, ContractJson.Serialize(command))
                    {
                        DedupKey = JobDedupKeys.SiteProfile(id),
                    };
                    return new AdminChange<SiteProfileJob>(new SiteProfileJob(job.Id, id), "site-profile.rollback", AuditEntityTypes.SiteProfile, id,
                        new { version = applied?.Number }, new { toVersion = request.ToVersion }) { Classification = current.Classification, Jobs = [job] };
                }, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("RollbackSiteProfile").WithSummary("Apply an earlier version again (command rollback-site-profile)");

        return api;
    }

    private static async Task<(SiteProfile Profile, SiteProfileVersion? Applied)> LoadAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        var profile = await admin.Db.SiteProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var applied = profile.AppliedVersionId is { } v ? await admin.Db.SiteProfileVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == v, ct) : null;
        return (profile, applied);
    }

    /// <summary>The profile checked against <c>contracts/site-profiles/v1</c> (J3); 400 <c>site_profile_invalid</c> at the first problem.</summary>
    private static ProfileContract Profile(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            throw AdminFaults.Field("profile", AdminErrorCodes.SiteProfileInvalid, new Dictionary<string, string> { ["field"] = "profile", ["problem"] = "must be an object" });
        }

        var errors = ContractSchemas.Validator(typeof(ProfileContract)).Validate(profile);
        if (errors.Count > 0)
        {
            var problem = errors[0].Message.Contains("Boolean false", StringComparison.Ordinal) ? "not a profile field" : errors[0].Message;
            var field = errors[0].InstanceLocation.TrimStart('/');
            throw AdminFaults.Field("profile" + (field.Length > 0 ? "." + field.Replace('/', '.') : ""), AdminErrorCodes.SiteProfileInvalid,
                new Dictionary<string, string> { ["field"] = field.Length > 0 ? field : "profile", ["problem"] = problem });
        }

        return ContractJson.Deserialize<ProfileContract>(profile);
    }

    /// <summary>
    /// The department a site-profile command is queued for (fairness only, data-model §4.2): the department of a live source
    /// whose settings name the host, else the first department. A site profile is a platform object of no department.
    /// </summary>
    private static async Task<Guid> DepartmentForAsync(AdminContext admin, string host, CancellationToken ct)
    {
        // Settings is jsonb: matched as text. A validated host holds only letters, digits, '-' and '.', so nothing to escape.
        var pattern = $"%//{host}%";
        var department = await admin.Db.Database.SqlQuery<Guid?>($"""
            SELECT c."DepartmentId" AS "Value"
            FROM config."Sources" s JOIN config."Collections" c ON c."Id" = s."CollectionId"
            WHERE s."DeletedAt" IS NULL AND s."Settings"::text ILIKE {pattern}
            ORDER BY s."CreatedAt" LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return department
            ?? await admin.Db.Departments.AsNoTracking().OrderBy(d => d.Id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NoDepartment);
    }
}

public sealed record SiteProfileSummary(Guid Id, string Level, string Host, string? PresetId, int? AppliedVersion, string? Origin, DateTimeOffset? AppliedAt,
    DateTimeOffset UpdatedAt);

public sealed record SiteProfileVersionSummary(int Number, string Origin, string State, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? AppliedAt,
    string? AppliedBy);

public sealed record SiteProfileVersionDto(int Number, string Origin, string State, JsonElement Profile, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt, string? AppliedBy);

/// <summary>The command enqueued; the worker writes the version (poll <c>GET /api/jobs/{jobId}</c> or the versions).</summary>
public sealed record SiteProfileJob(Guid JobId, Guid? SiteProfileId);

public sealed record SiteProfileCreateRequest : AdminRequest, IValidatableObject
{
    [Required, StringLength(253), RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$")]
    public string Host { get; init; } = "";

    [Required]
    public string Level { get; init; } = "";

    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public string? PresetId { get; init; }

    public JsonElement Profile { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Classification.IsValid(Level))
        {
            yield return new ValidationResult($"The level must be one of {string.Join(", ", Classification.All)}.", [nameof(Level)]);
        }
    }
}

public sealed record SiteProfileEditRequest : AdminRequest
{
    /// <summary>The number of the applied version the edit is based on (null when the host has none applied).</summary>
    public int? Version { get; init; }

    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public string? PresetId { get; init; }

    public JsonElement Profile { get; init; }
}

public sealed record SiteProfileRollbackRequest : AdminRequest
{
    [Range(1, int.MaxValue)]
    public int ToVersion { get; init; }
}
