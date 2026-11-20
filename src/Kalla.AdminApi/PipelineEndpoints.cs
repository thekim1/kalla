using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Definitions;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;

namespace Kalla.AdminApi;

/// <summary>
/// Pipeline publish (admin design §4.5 "Pipelines", §6.6, §6.7; P1b item 9c): plan → confirm (409 when the pipeline changed
/// since the plan) → re-process → keep or roll back. The publication's id is the <c>publish-plan</c> job's id. Copy on
/// write (#256): platform pipelines (<c>DepartmentId</c> null) are published by <c>rag-platform-admin</c> only; a department
/// user's first publication on one creates a department pipeline whose version 1 is the platform's published version,
/// re-points that collection's sources of the template to it, and plans the draft there, in one config transaction.
/// </summary>
public static class PipelineEndpoints
{
    public static RouteGroupBuilder MapPipelines(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var pipelines = api.MapGroup("/pipelines").WithTags("Pipelines");

        pipelines.MapGet("", async (Guid collectionId, string sourceType, AdminContext admin, CancellationToken ct) =>
            {
                // §5.4 step 5: the pipelines a new source of the type can use in the collection, the default (the one a source
                // created without pipelineId gets) first; the preview of an unsaved source runs on it.
                var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == collectionId, ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var candidates = SourceRules.Candidates(admin, collection, sourceType);
                var fallback = await SourceRules.Default(candidates).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
                var list = await candidates.OrderBy(p => p.DepartmentId == null).ThenBy(p => p.Name)
                    .Select(p => new PipelineSummary(p.Id, p.Name, p.TemplateId, p.DepartmentId, p.PublishedVersionId, false)).ToListAsync(ct);
                return list.Select(p => p with { IsDefault = p.Id == fallback }).OrderByDescending(p => p.IsDefault).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListPipelines").WithSummary("The pipelines a new source of a type can use in a collection, the default first");

        pipelines.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var pipeline = await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var versions = await admin.Db.PipelineVersions.AsNoTracking().Where(v => v.PipelineId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
                return PipelineDto.Of(pipeline, versions);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetPipeline").WithSummary("A pipeline with its versions (template version and parameters per version)");

        pipelines.MapPost("/{id:guid}/publications", async (Guid id, PublicationRequest request, AdminContext admin, CancellationToken ct) =>
                Results.Accepted(value: await admin.WriteAsync(attempt => PlanAsync(admin, id, request, attempt), ct)))
            .RequireAuthorization(AdminPolicies.EditPipelines).WithName("PlanPublication")
            .WithSummary("Save the draft and plan its publication (publish-plan); copy on write for a department on a platform pipeline");

        pipelines.MapPost("/{id:guid}/rollback", async (Guid id, RollbackRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var pipeline = await Writable(admin, id, attempt);
                    admin.ExpectVersion(pipeline, request.Version);
                    var target = await admin.Db.PipelineVersions.SingleOrDefaultAsync(v => v.Id == request.VersionId && v.PipelineId == id, attempt);
                    if (target is not { State: PipelineVersionStates.Superseded })
                    {
                        throw AdminFaults.Of(AdminErrorCodes.VersionNotRollbackable);
                    }

                    var jobs = await PublishAsync(admin, pipeline, target, RunTrigger.Rollback, attempt);
                    return new AdminChange<PipelineDto>(PipelineDto.Of(pipeline, []), "rollback", AuditEntityTypes.Pipeline, id, null,
                        new { versionId = target.Id, target.Number, sources = jobs.Count })
                    {
                        DepartmentId = pipeline.DepartmentId, Jobs = jobs,
                    };
                }, ct);
                return saved;
            })
            .RequireAuthorization(AdminPolicies.EditPipelines).WithName("RollbackPipeline").WithSummary("Re-publish an earlier version; its sources are re-processed (§6.7)");

        pipelines.MapPost("/{id:guid}/preview", async (Guid id, PipelinePreviewRequest request, AdminContext admin, CancellationToken ct) =>
                Results.Accepted(value: await admin.WriteAsync(async attempt =>
                {
                    // §6.4, §5.4 step 5: the saved draft (or, without one, the published version a new source would use), on up to
                    // 5 documents of a source or 5 URLs fetched live; outputs through GET /api/previews/{jobId}/... (24 h).
                    var pipeline = await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CollectionId, attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    var documents = request.DocumentIds.Distinct().ToList();
                    var urls = request.Urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                    if ((documents.Count == 0) == (urls.Count == 0) || documents.Count > 5 || urls.Count > 5)
                    {
                        throw AdminFaults.Field(documents.Count > 0 ? "documentIds" : "urls", AdminErrorCodes.InvalidValue);
                    }

                    if (urls.Any(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                    {
                        throw AdminFaults.Field("urls", AdminErrorCodes.InvalidValue);
                    }

                    if (request.SourceId is { } sid)
                    {
                        if (!await admin.Db.Sources.AsNoTracking().AnyAsync(s => s.Id == sid && s.CollectionId == collection.Id && s.DeletedAt == null, attempt))
                        {
                            throw AdminFaults.Of(AdminErrorCodes.NotFound);
                        }

                        if (documents.Count > 0 && await admin.Db.Documents.AsNoTracking().CountAsync(d => documents.Contains(d.Id) && d.SourceId == sid, attempt) != documents.Count)
                        {
                            throw AdminFaults.Field("documentIds", AdminErrorCodes.InvalidValue);
                        }
                    }
                    else if (documents.Count > 0)
                    {
                        throw AdminFaults.Field("sourceId", AdminErrorCodes.InvalidValue);
                    }

                    var versionId = await admin.Db.PipelineVersions.AsNoTracking().Where(v => v.PipelineId == id && v.State == PipelineVersionStates.Draft)
                        .Select(v => (Guid?)v.Id).SingleOrDefaultAsync(attempt) ?? pipeline.PublishedVersionId ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    var payload = new StagePreviewPayload
                    {
                        PipelineVersionId = versionId, CollectionId = collection.Id, SourceId = request.SourceId,
                        DocumentIds = documents.Count > 0 ? documents : null, Urls = urls.Count > 0 ? urls : null,
                    };
                    var job = new JobRequest(JobClasses.Preview, collection.DepartmentId, collection.Classification, ContractJson.Serialize(payload)) { TeamId = collection.OwnerTeamId };
                    return new AdminChange<CommandStarted>(new CommandStarted(job.Id), "preview", AuditEntityTypes.Pipeline, id, null,
                        new { versionId, collectionId = collection.Id, request.SourceId, documents = documents.Count, urls = urls.Count })
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification, Jobs = [job],
                    };
                }, ct)))
            .RequireAuthorization(AdminPolicies.EditOrEditPipelines).WithName("PreviewPipeline")
            .WithSummary("Preview the draft (or published) version on up to 5 documents or URLs (stage-preview)");

        var publications = api.MapGroup("/publications").WithTags("Pipelines");

        publications.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) => await PublicationAsync(admin, id, ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetPublication").WithSummary("The plan (job state and result), its draft and the pipeline version to confirm with");

        publications.MapPost("/{id:guid}/confirm", async (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var publication = await PublicationAsync(admin, id, attempt);
                    var pipeline = await Writable(admin, publication.PipelineId, attempt);
                    if (!string.Equals(pipeline.Version.ToString(CultureInfo.InvariantCulture), request.Version, StringComparison.Ordinal))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.PublicationStale);
                    }

                    // A newer plan for the pipeline (another user, other parameters, the same draft row) makes this one stale (#298 review).
                    if (publication.Superseded)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.PublicationStale);
                    }

                    var draft = await admin.Db.PipelineVersions.SingleOrDefaultAsync(v => v.Id == publication.PipelineVersionId, attempt);
                    if (publication.State != JobStates.Succeeded || draft is not { State: PipelineVersionStates.Draft })
                    {
                        throw AdminFaults.Of(AdminErrorCodes.PublicationNotReady);
                    }

                    var jobs = await PublishAsync(admin, pipeline, draft, RunTrigger.Publish, attempt);
                    return new AdminChange<PublicationDto>(publication with { Published = true }, "publish", AuditEntityTypes.Pipeline, pipeline.Id, null,
                        new { publicationId = id, versionId = draft.Id, draft.Number, sources = jobs.Count })
                    {
                        DepartmentId = pipeline.DepartmentId, Jobs = jobs,
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.EditPipelines).WithName("ConfirmPublication")
            .WithSummary("Publish the planned draft and re-process its sources; refused (409) when the pipeline changed since the plan");

        return api;
    }

    /// <summary>Pipelines the caller sees: the platform's and their departments'.</summary>
    private static IQueryable<Pipeline> Scoped(AdminContext admin)
    {
        var departments = admin.Departments().Select(d => d.Id);
        return admin.Db.Pipelines.Where(p => p.DepartmentId == null || departments.Contains(p.DepartmentId.Value));
    }

    /// <summary>A pipeline the caller may change: a department's of theirs, or a platform one for a platform admin (403 <c>admin_only</c>).</summary>
    private static async Task<Pipeline> Writable(AdminContext admin, Guid id, CancellationToken ct)
    {
        var pipeline = await Scoped(admin).SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        if (pipeline.DepartmentId is null && !admin.IsPlatformAdmin)
        {
            throw AdminFaults.Of(AdminErrorCodes.AdminOnly);
        }

        return pipeline;
    }

    private static async Task<AdminChange<PublicationStarted>> PlanAsync(AdminContext admin, Guid id, PublicationRequest request, CancellationToken ct)
    {
        var pipeline = await Scoped(admin).SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        Collection? collection = null;
        var copied = false;
        if (pipeline.DepartmentId is null && !admin.IsPlatformAdmin)
        {
            // Copy on write (#256): only through a collection of the caller's, whose sources move to the department pipeline.
            collection = request.CollectionId is { } cid
                ? await CollectionEndpoints.Scoped(admin).SingleOrDefaultAsync(c => c.Id == cid, ct)
                : null;
            if (collection is null)
            {
                throw AdminFaults.Of(AdminErrorCodes.AdminOnly);
            }

            admin.ExpectVersion(pipeline, request.Version);
            pipeline = await CopyOnWriteAsync(admin, pipeline, collection, ct);
            copied = true;
        }
        else
        {
            admin.ExpectVersion(pipeline, request.Version);
        }

        var sourceType = TemplateCatalog.BuiltIn.All.FirstOrDefault(t => t.Template.Name == pipeline.TemplateId)?.Template.SourceType;
        var templateVersion = request.TemplateVersion ?? TemplateCatalog.BuiltIn.All.Where(t => t.Template.Name == pipeline.TemplateId).Max(t => (int?)t.Template.Version)
            ?? throw AdminFaults.Of(AdminErrorCodes.TemplateUnsupported, new Dictionary<string, string> { ["type"] = pipeline.TemplateId });
        var definition = new JsonObject
        {
            ["schema"] = PipelineDefinition.CurrentSchema, ["template"] = pipeline.TemplateId, ["templateVersion"] = templateVersion,
            ["params"] = request.Parameters.ValueKind == JsonValueKind.Object ? JsonNode.Parse(request.Parameters.GetRawText()) : new JsonObject(),
        };
        // V24 (engine §4 rules 1–4): parameters valid for the template version, the template matching the sources' type, and
        // every stage version of the template run by a live worker in each job class that hosts it (a rolling update).
        var validation = new PipelineValidator(TemplateCatalog.BuiltIn).ValidateForPublish(JsonSerializer.SerializeToElement(definition), sourceType,
            await LiveWorkersAsync(admin, ct), admin.Now);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            if (first.Code == PipelineValidationCodes.StageVersionUnsupported)
            {
                throw AdminFaults.Of(AdminErrorCodes.TemplateUnsupported, new Dictionary<string, string> { ["type"] = sourceType ?? pipeline.TemplateId });
            }

            throw AdminFaults.Field(first.Param is { } param ? "parameters." + param : "parameters", AdminErrorCodes.PipelineParamsInvalid,
                new Dictionary<string, string> { ["param"] = first.Param ?? "-", ["message"] = first.Message });
        }

        var draft = await admin.Db.PipelineVersions.SingleOrDefaultAsync(v => v.PipelineId == pipeline.Id && v.State == PipelineVersionStates.Draft, ct);
        if (draft is null)
        {
            var next = (await admin.Db.PipelineVersions.Where(v => v.PipelineId == pipeline.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
            draft = new PipelineVersion { PipelineId = pipeline.Id, Number = next, Definition = definition.ToJsonString(), CreatedBy = admin.Actor };
            admin.Db.PipelineVersions.Add(draft);
        }
        else
        {
            draft.Definition = definition.ToJsonString();
        }

        // The pipeline row's stamp moves its xmin, so a confirm with the version read before this plan is stale (§6.6).
        pipeline.UpdatedAt = admin.Now;
        pipeline.UpdatedBy = admin.Actor;
        var (department, level) = await JobScopeAsync(admin, pipeline, collection, ct);
        var job = new JobRequest(JobClasses.Preview, department, level,
            ContractJson.Serialize(new PublishPlanPayload { PipelineId = pipeline.Id, PipelineVersionId = draft.Id }));
        // No dedup key: each plan is its own publication; a newer one makes the older stale (GET and confirm, #298 review).
        await admin.Db.SaveChangesAsync(ct);
        return new AdminChange<PublicationStarted>(new PublicationStarted(job.Id, pipeline.Id, draft.Id, draft.Number, copied), "plan", AuditEntityTypes.Pipeline, pipeline.Id,
            null, new { draftVersionId = draft.Id, draft.Number, templateVersion, copiedForCollection = copied ? collection?.Id : null })
        {
            DepartmentId = pipeline.DepartmentId ?? collection?.DepartmentId, Jobs = [job],
        };
    }

    /// <summary>
    /// The worker instances whose heartbeat is inside <see cref="PipelineValidator.SupportHeartbeatWindow"/>, with their
    /// <c>WorkerNodeSupport</c> rows (engine §4 rule 4, §5.2).
    /// </summary>
    private static async Task<IReadOnlyCollection<WorkerSupport>> LiveWorkersAsync(AdminContext admin, CancellationToken ct)
    {
        var since = admin.Now - PipelineValidator.SupportHeartbeatWindow;
        var instances = await admin.Db.WorkerInstances.AsNoTracking().Where(w => w.LastHeartbeatAt > since).ToListAsync(ct);
        var ids = instances.Select(w => w.InstanceId).ToList();
        var support = (await admin.Db.WorkerNodeSupport.AsNoTracking().Where(s => ids.Contains(s.InstanceId)).ToListAsync(ct))
            .ToLookup(s => s.InstanceId);
        return instances.Select(w => new WorkerSupport(w.InstanceId, w.LastHeartbeatAt, w.Classes,
            support[w.InstanceId].Select(s => new StageVersion(s.NodeType, s.TypeVersion)).ToList())).ToList();
    }

    /// <summary>
    /// #256 option 1: a department pipeline for the collection's department with version 1 = the platform pipeline's
    /// published version, and the collection's sources of that pipeline re-pointed to it (an existing one is reused).
    /// </summary>
    private static async Task<Pipeline> CopyOnWriteAsync(AdminContext admin, Pipeline platform, Collection collection, CancellationToken ct)
    {
        var name = $"{platform.Name}-{collection.Slug}";
        // Only for a collection that uses the platform pipeline, and only from a published version (#298 review).
        if (platform.PublishedVersionId is null ||
            !await admin.Db.Sources.AnyAsync(s => s.CollectionId == collection.Id && s.PipelineId == platform.Id && s.DeletedAt == null, ct))
        {
            throw AdminFaults.Field("collectionId", AdminErrorCodes.InvalidValue);
        }

        var copy = await admin.Db.Pipelines.SingleOrDefaultAsync(p => p.DepartmentId == collection.DepartmentId && p.Name == name, ct);
        if (copy is null)
        {
            var published = await admin.Db.PipelineVersions.AsNoTracking().SingleAsync(v => v.Id == platform.PublishedVersionId, ct);
            copy = new Pipeline { Name = name, TemplateId = platform.TemplateId, DepartmentId = collection.DepartmentId, UpdatedAt = admin.Now, UpdatedBy = admin.Actor };
            admin.Db.Pipelines.Add(copy);
            await admin.Db.SaveChangesAsync(ct);
            var first = new PipelineVersion
            {
                PipelineId = copy.Id, Number = 1, State = PipelineVersionStates.Published, Definition = published.Definition, CreatedBy = admin.Actor, PublishedAt = admin.Now,
            };
            admin.Db.PipelineVersions.Add(first);
            await admin.Db.SaveChangesAsync(ct);
            copy.PublishedVersionId = first.Id;
        }

        var sources = await admin.Db.Sources.Where(s => s.CollectionId == collection.Id && s.PipelineId == platform.Id && s.DeletedAt == null).ToListAsync(ct);
        foreach (var source in sources)
        {
            source.PipelineId = copy.Id;
            source.UpdatedAt = admin.Now;
            source.UpdatedBy = admin.Actor;
        }

        await admin.Db.SaveChangesAsync(ct);
        return copy;
    }

    /// <summary>
    /// Publishes <paramref name="version"/> (the previous published version superseded) and enqueues one <c>reprocess</c>
    /// sync per source of the pipeline (§6.6 step 3, §6.7). No dedup key: a running sync of a source gets the re-process
    /// as a follow-up job (§4.7).
    /// </summary>
    private static async Task<List<JobRequest>> PublishAsync(AdminContext admin, Pipeline pipeline, PipelineVersion version, RunTrigger trigger, CancellationToken ct)
    {
        if (pipeline.PublishedVersionId is { } current && current != version.Id)
        {
            var old = await admin.Db.PipelineVersions.SingleAsync(v => v.Id == current, ct);
            old.State = PipelineVersionStates.Superseded;
            await admin.Db.SaveChangesAsync(ct);
        }

        version.State = PipelineVersionStates.Published;
        version.PublishedAt = admin.Now;
        pipeline.PublishedVersionId = version.Id;
        pipeline.UpdatedAt = admin.Now;
        pipeline.UpdatedBy = admin.Actor;
        var sources = await (from s in admin.Db.Sources.AsNoTracking()
                             join c in admin.Db.Collections.AsNoTracking() on s.CollectionId equals c.Id
                             where s.PipelineId == pipeline.Id && s.DeletedAt == null && c.DeletedAt == null
                             select new { s.Id, s.Classification, c.DepartmentId, c.OwnerTeamId }).ToListAsync(ct);
        return [.. sources.Select(s => new JobRequest(JobClasses.Sync, s.DepartmentId, s.Classification,
            ContractJson.Serialize(new SyncPayload { SourceId = s.Id, Kind = SyncKind.Reprocess, Trigger = trigger }))
        {
            Priority = 10, TeamId = s.OwnerTeamId,
        })];
    }

    /// <summary>The plan job's department and level: the pipeline's (or collection's) department and the highest level of its sources.</summary>
    private static async Task<(Guid Department, string Level)> JobScopeAsync(AdminContext admin, Pipeline pipeline, Collection? collection, CancellationToken ct)
    {
        var sources = await (from s in admin.Db.Sources.AsNoTracking()
                             join c in admin.Db.Collections.AsNoTracking() on s.CollectionId equals c.Id
                             where s.PipelineId == pipeline.Id && s.DeletedAt == null && c.DeletedAt == null
                             select new { s.Classification, c.DepartmentId }).ToListAsync(ct);
        var level = sources.Select(s => s.Classification).Append(Classification.Public).MaxBy(Levels.Rank)!;
        var department = pipeline.DepartmentId ?? collection?.DepartmentId ?? sources.Select(s => (Guid?)s.DepartmentId).FirstOrDefault()
            ?? await admin.Departments().Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct) ?? throw AdminFaults.Of(AdminErrorCodes.NoDepartment);
        return (department, level);
    }

    /// <summary>A plan job the caller sees, with its draft and the pipeline's current version to confirm with.</summary>
    private static async Task<PublicationDto> PublicationAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        var job = await admin.Db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.Class == JobClasses.Preview, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != PreviewKinds.PublishPlan)
        {
            throw AdminFaults.Of(AdminErrorCodes.NotFound);
        }

        var pipelineId = payload.RootElement.GetProperty("pipelineId").GetGuid();
        var versionId = payload.RootElement.GetProperty("pipelineVersionId").GetGuid();
        var pipeline = await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(p => p.Id == pipelineId, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var draft = await admin.Db.PipelineVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct);
        JsonElement? result = job.Result is { } r ? JsonDocument.Parse(r).RootElement.Clone() : null;
        var pipelineKey = pipelineId.ToString("D");
        var superseded = await admin.Db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM content."Jobs"
            WHERE "Class" = 'preview' AND "Payload"::jsonb ->> 'kind' = 'publish-plan' AND "Payload"::jsonb ->> 'pipelineId' = {pipelineKey}
              AND ("EnqueuedAt", "Id") > ({job.EnqueuedAt}, {id})
            """).AnyAsync(ct);
        return new PublicationDto(id, job.State, result, job.LastError, pipelineId, versionId, draft?.Number, draft?.State,
            pipeline.Version.ToString(CultureInfo.InvariantCulture), draft?.State == PipelineVersionStates.Published, superseded);
    }
}

/// <summary>A pipeline a source can use; <see cref="IsDefault"/>: the one a source created without <c>pipelineId</c> gets.</summary>
public sealed record PipelineSummary(Guid Id, string Name, string TemplateId, Guid? DepartmentId, Guid? PublishedVersionId, bool IsDefault);

public sealed record PipelineVersionDto(Guid Id, int Number, string State, JsonElement Definition, DateTimeOffset? PublishedAt);

public sealed record PipelineDto(Guid Id, string Name, string TemplateId, Guid? DepartmentId, Guid? PublishedVersionId, IReadOnlyList<PipelineVersionDto> Versions,
    string Version)
{
    public static PipelineDto Of(Pipeline p, IReadOnlyList<PipelineVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(versions);
        return new(p.Id, p.Name, p.TemplateId, p.DepartmentId, p.PublishedVersionId,
            [.. versions.Select(v => new PipelineVersionDto(v.Id, v.Number, v.State, JsonDocument.Parse(v.Definition).RootElement.Clone(), v.PublishedAt))],
            p.Version.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>The plan started: the publication (= plan job) id, the pipeline it is for (a new department copy when <see cref="CopiedOnWrite"/>) and the draft.</summary>
public sealed record PublicationStarted(Guid PublicationId, Guid PipelineId, Guid DraftVersionId, int DraftNumber, bool CopiedOnWrite);

/// <summary>
/// A publication: the plan job's state and result, the draft, and the pipeline <see cref="Version"/> the confirm sends.
/// <see cref="Superseded"/>: a newer plan exists for the pipeline, so this one can no longer be confirmed (409 <c>publication_stale</c>).
/// </summary>
public sealed record PublicationDto(
    Guid Id, string State, JsonElement? Plan, string? LastError, Guid PipelineId, Guid PipelineVersionId, int? DraftNumber, string? DraftState, string Version,
    bool Published, bool Superseded);

public sealed record PublicationRequest : AdminRequest
{
    /// <summary>The template version; default the latest built-in one (§6.6 step 6).</summary>
    [Range(1, 10_000)]
    public int? TemplateVersion { get; init; }

    /// <summary>The template parameters (V24 against its <c>paramsSchema</c>); unset ones take the defaults.</summary>
    public JsonElement Parameters { get; init; }

    /// <summary>A department user on a platform pipeline: the collection whose sources move to the department copy (#256).</summary>
    public Guid? CollectionId { get; init; }

    [Required]
    public string? Version { get; init; }
}

public sealed record RollbackRequest : AdminRequest
{
    [Required]
    public Guid VersionId { get; init; }

    [Required]
    public string? Version { get; init; }
}

public sealed record PipelinePreviewRequest : AdminRequest
{
    [Required]
    public Guid CollectionId { get; init; }

    /// <summary>The source whose documents are previewed; absent for a new source's form (URLs only).</summary>
    public Guid? SourceId { get; init; }

    [MaxLength(5)]
    public IReadOnlyList<Guid> DocumentIds { get; init; } = [];

    [MaxLength(5)]
    public IReadOnlyList<string> Urls { get; init; } = [];
}
