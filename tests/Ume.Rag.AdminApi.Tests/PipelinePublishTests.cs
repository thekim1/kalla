using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Pipeline publish (admin design §6.6, §6.7; P1b item 9c; #256): copy on write for a department on a platform pipeline,
/// V24, the plan (publish-plan job), confirm refused while the plan is not ready or after the pipeline changed, publish
/// with one reprocess sync per source, and rollback.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class PipelinePublishTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_department_plans_on_a_copy_confirms_after_the_plan_and_rolls_back()
    {
        var code = "U" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var template = TemplateCatalog.BuiltIn.All.Where(t => t.Template.SourceType == SourceTypes.UrlList).MaxBy(t => t.Template.Version)!.Template;
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Pub " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct) ?? Guid.Empty;
        if (profile == Guid.Empty)
        {
            var created = new EmbeddingProfile
            {
                Name = "pub-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            await db.SaveChangesAsync(Ct);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "pub-" + code.ToLowerInvariant(), Name = "Publicering " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var platform = new Pipeline { Name = template.Name + "-" + code.ToLowerInvariant(), TemplateId = template.Name, UpdatedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, collection, platform);
        await db.SaveChangesAsync(Ct);
        var published = new PipelineVersion
        {
            PipelineId = platform.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow,
            Definition = JsonSerializer.Serialize(new { schema = PipelineDefinition.CurrentSchema, template = template.Name, templateVersion = template.Version, @params = new { } }),
        };
        db.Add(published);
        await db.SaveChangesAsync(Ct);
        platform.PublishedVersionId = published.Id;
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Public, PipelineId = platform.Id,
            Settings = """{"urls":["https://www.umea.se/a"]}""", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(source);
        await db.SaveChangesAsync(Ct);

        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        var version = (await admin.GetFromJsonAsync<JsonElement>($"/api/pipelines/{platform.Id}", Ct)).GetProperty("version").GetString();

        // A platform pipeline: only through a collection (copy on write), and not for an editor without EditPipelines.
        (await editor.PostAsJsonAsync($"/api/pipelines/{platform.Id}/publications", new { parameters = new { }, version, collectionId = collection.Id }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var direct = await admin.PostAsJsonAsync($"/api/pipelines/{platform.Id}/publications", new { parameters = new { }, version }, Ct))
        {
            direct.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Json(direct)).GetProperty("code").GetString().ShouldBe("admin_only");
        }

        using (var invalid = await admin.PostAsJsonAsync($"/api/pipelines/{platform.Id}/publications",
                   new { parameters = new { notAParameter = 1 }, version, collectionId = collection.Id }, Ct))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync(Ct));
            JsonSerializer.Serialize(await Json(invalid)).ShouldContain("pipeline_params_invalid");
        }

        using var planned = await admin.PostAsJsonAsync($"/api/pipelines/{platform.Id}/publications", new { parameters = new { }, version, collectionId = collection.Id }, Ct);
        planned.StatusCode.ShouldBe(HttpStatusCode.Accepted, await planned.Content.ReadAsStringAsync(Ct));
        var plan = await Json(planned);
        plan.GetProperty("copiedOnWrite").GetBoolean().ShouldBeTrue();
        var copyId = plan.GetProperty("pipelineId").GetGuid();
        var publicationId = plan.GetProperty("publicationId").GetGuid();
        copyId.ShouldNotBe(platform.Id);
        (await db.Sources.AsNoTracking().SingleAsync(s => s.Id == source.Id, Ct)).PipelineId.ShouldBe(copyId);
        var copy = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == copyId, Ct);
        copy.DepartmentId.ShouldBe(department.Id);
        var planJob = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == publicationId, Ct);
        Ume.Rag.Pipelines.Schemas.ContractSchemas.Validator(typeof(Ume.Rag.Pipelines.Jobs.PublishPlanPayload))
            .Validate(JsonDocument.Parse(planJob.Payload).RootElement).ShouldBeEmpty();

        // Confirm: refused while the plan runs, refused with the version from before the plan, accepted after.
        var publication = await admin.GetFromJsonAsync<JsonElement>($"/api/publications/{publicationId}", Ct);
        var current = publication.GetProperty("version").GetString();
        using (var early = await admin.PostAsJsonAsync($"/api/publications/{publicationId}/confirm", new { version = current }, Ct))
        {
            (await Json(early)).GetProperty("code").GetString().ShouldBe("publication_not_ready");
        }

        await db.Jobs.Where(j => j.Id == publicationId).ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobStates.Succeeded)
            .SetProperty(j => j.Result, $$"""{"pipelineVersionId":"{{plan.GetProperty("draftVersionId").GetGuid()}}","documents":1,"sources":[{"sourceId":"{{source.Id}}","documents":1}]}"""), Ct);
        // #298 review: a second plan (other parameters, the same draft row) makes the first stale, even with a fresh version.
        var older = publicationId;
        using (var replanned = await admin.PostAsJsonAsync($"/api/pipelines/{copyId}/publications",
                   new { parameters = new { }, version = (await admin.GetFromJsonAsync<JsonElement>($"/api/pipelines/{copyId}", Ct)).GetProperty("version").GetString() }, Ct))
        {
            replanned.StatusCode.ShouldBe(HttpStatusCode.Accepted, await replanned.Content.ReadAsStringAsync(Ct));
            publicationId = (await Json(replanned)).GetProperty("publicationId").GetGuid();
        }

        var olderView = await admin.GetFromJsonAsync<JsonElement>($"/api/publications/{older}", Ct);
        olderView.GetProperty("superseded").GetBoolean().ShouldBeTrue();
        using (var stalePlan = await admin.PostAsJsonAsync($"/api/publications/{older}/confirm", new { version = olderView.GetProperty("version").GetString() }, Ct))
        {
            (await Json(stalePlan)).GetProperty("code").GetString().ShouldBe("publication_stale");
        }

        await db.Jobs.Where(j => j.Id == publicationId).ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobStates.Succeeded), Ct);
        current = (await admin.GetFromJsonAsync<JsonElement>($"/api/publications/{publicationId}", Ct)).GetProperty("version").GetString();
        using (var stale = await admin.PostAsJsonAsync($"/api/publications/{publicationId}/confirm", new { version = "1" }, Ct))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(stale)).GetProperty("code").GetString().ShouldBe("publication_stale");
        }

        using (var confirmed = await admin.PostAsJsonAsync($"/api/publications/{publicationId}/confirm", new { version = current }, Ct))
        {
            confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
        }

        var versions = await db.PipelineVersions.AsNoTracking().Where(v => v.PipelineId == copyId).OrderBy(v => v.Number).Select(v => v.State).ToListAsync(Ct);
        versions.ShouldBe([PipelineVersionStates.Superseded, PipelineVersionStates.Published]);
        async Task<List<string>> TriggersAsync() => await db.Database.SqlQuery<string>($"""
            SELECT "Payload"::jsonb ->> 'trigger' AS "Value" FROM content."Jobs" WHERE "Class" = 'sync' AND "Payload"::jsonb ->> 'sourceId' = {source.Id.ToString("D")}
              AND "Payload"::jsonb ->> 'kind' = 'reprocess' ORDER BY "EnqueuedAt"
            """).ToListAsync(Ct);
        (await TriggersAsync()).ShouldBe(["publish"]);

        // Rollback to version 1: it is published again and the source re-processed with trigger rollback.
        var first = await db.PipelineVersions.AsNoTracking().SingleAsync(v => v.PipelineId == copyId && v.Number == 1, Ct);
        var copyVersion = (await admin.GetFromJsonAsync<JsonElement>($"/api/pipelines/{copyId}", Ct)).GetProperty("version").GetString();
        (await admin.PostAsJsonAsync($"/api/pipelines/{copyId}/rollback", new { versionId = first.Id, version = copyVersion }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await db.PipelineVersions.AsNoTracking().Where(v => v.PipelineId == copyId).OrderBy(v => v.Number).Select(v => v.State).ToListAsync(Ct))
            .ShouldBe([PipelineVersionStates.Published, PipelineVersionStates.Superseded]);
        (await TriggersAsync()).ShouldBe(["publish", "rollback"]);
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "pipeline" && a.EntityId == copyId.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["plan", "plan", "publish", "rollback"]);
    }

    [Fact]
    public async Task A_preview_runs_the_published_version_or_the_draft_on_urls_or_documents()
    {
        var code = "W" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var template = TemplateCatalog.BuiltIn.All.Where(t => t.Template.SourceType == SourceTypes.UrlList).MaxBy(t => t.Template.Version)!.Template;
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Prv " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "prv-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            await db.SaveChangesAsync(Ct);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "prv-" + code.ToLowerInvariant(), Name = "Förhandsgranskning " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Value, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = template.Name + "-" + code.ToLowerInvariant(), TemplateId = template.Name, DepartmentId = department.Id, UpdatedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, collection, pipeline);
        await db.SaveChangesAsync(Ct);
        var published = new PipelineVersion
        {
            PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow,
            Definition = JsonSerializer.Serialize(new { schema = PipelineDefinition.CurrentSchema, template = template.Name, templateVersion = template.Version, @params = new { } }),
        };
        db.Add(published);
        await db.SaveChangesAsync(Ct);
        pipeline.PublishedVersionId = published.Id;
        await db.SaveChangesAsync(Ct);

        await using var factory = database.HeaderFactory();
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        var path = $"/api/pipelines/{pipeline.Id}/preview";

        (await viewer.PostAsJsonAsync(path, new { collectionId = collection.Id, urls = new[] { "https://www.umea.se/a" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await editor.PostAsJsonAsync(path, new { collectionId = collection.Id }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await editor.PostAsJsonAsync(path, new { collectionId = collection.Id, documentIds = new[] { Guid.CreateVersion7() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Without a draft: the published version (a new source's form), on URLs fetched live.
        using var started = await editor.PostAsJsonAsync(path, new { collectionId = collection.Id, urls = new[] { "https://www.umea.se/a", "https://www.umea.se/b" } }, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        var jobId = (await Json(started)).GetProperty("jobId").GetGuid();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        using var payload = JsonDocument.Parse(job.Payload);
        Ume.Rag.Pipelines.Schemas.ContractSchemas.Validator(typeof(Ume.Rag.Pipelines.Jobs.StagePreviewPayload)).Validate(payload.RootElement).ShouldBeEmpty();
        payload.RootElement.GetProperty("pipelineVersionId").GetGuid().ShouldBe(published.Id);
        payload.RootElement.GetProperty("urls").GetArrayLength().ShouldBe(2);
    }
}
