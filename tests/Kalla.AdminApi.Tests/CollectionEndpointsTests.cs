using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Collections (admin design §4.5, P1a item 14b): create on the default profile, department scope, update with V2/V3,
/// publish, retrieval settings (V16) with the advisory evaluation job, delete refused while used; every write audited.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class CollectionEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid Id, string Code)> DepartmentAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        if (!await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, Ct))
        {
            db.EmbeddingProfiles.Add(new EmbeddingProfile
            {
                Name = "default-" + Guid.NewGuid().ToString("N")[..8], GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192,
                MaxResidency = Residency.Eu, CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var code = "C" + Random.Shared.Next(100_000, 999_999);
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Coll " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        db.Departments.Add(department);
        await db.SaveChangesAsync(Ct);
        return (department.Id, code);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> CreateAsync(HttpClient client, Guid department, string name, string level = Classification.Public)
    {
        using var response = await client.PostAsJsonAsync("/api/collections", new { name, departmentId = department, classification = level }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await Json(response);
    }

    [Fact]
    public async Task A_collection_is_created_changed_published_and_deleted_in_its_department_with_audit()
    {
        var (department, code) = await DepartmentAsync();
        var (otherDepartment, otherCode) = await DepartmentAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, otherCode);

        var created = await CreateAsync(admin, department, "Bygglov och Ärenden " + code);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("slug").GetString().ShouldBe($"bygglov-och-arenden-{code.ToLowerInvariant()}");
        created.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        created.GetProperty("usedBy").GetArrayLength().ShouldBe(0);

        // Department scope: another department's admin neither sees it nor creates in it.
        (await stranger.GetAsync($"/api/collections/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using (var outside = await stranger.PostAsJsonAsync("/api/collections", new { name = "X", departmentId = department, classification = "public" }, Ct))
        {
            outside.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(outside)).GetProperty("errorCodes").GetProperty("departmentId")[0].GetProperty("code").GetString().ShouldBe("not_in_scope");
        }

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/collections?q={code}", Ct);
        list.GetProperty("total").GetInt32().ShouldBe(1);
        // q is text, not a pattern: "_" matches only an underscore.
        (await admin.GetFromJsonAsync<JsonElement>("/api/collections?q=_", Ct)).GetProperty("total").GetInt32().ShouldBe(0);

        // The slug is taken while the knowledge base is not deleted; the problem points at the field with a free suggestion.
        using (var taken = await admin.PostAsJsonAsync("/api/collections", new { name = "Bygglov och Ärenden " + code, departmentId = department, classification = "public" }, Ct))
        {
            taken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var entry = (await Json(taken)).GetProperty("errorCodes").GetProperty("slug")[0];
            entry.GetProperty("code").GetString().ShouldBe("slug_taken");
            entry.GetProperty("params").GetProperty("suggestion").GetString().ShouldBe($"bygglov-och-arenden-{code.ToLowerInvariant()}-2");
        }
        (await stranger.GetFromJsonAsync<JsonElement>($"/api/collections?q={code}", Ct)).GetProperty("total").GetInt32().ShouldBe(0);
        _ = otherDepartment;

        using var updated = await admin.PutAsJsonAsync($"/api/collections/{id}",
            new { name = "Bygglov", classification = Classification.Internal, version = created.GetProperty("version").GetString() }, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await Json(updated);
        after.GetProperty("classification").GetString().ShouldBe(Classification.Internal);

        using var published = await admin.PostAsJsonAsync($"/api/collections/{id}/publish", new { version = after.GetProperty("version").GetString() }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        var live = await Json(published);
        live.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.String);

        (await admin.DeleteAsync($"/api/collections/{id}?version={live.GetProperty("version").GetString()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/collections/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "collection" && a.EntityId == id.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["create", "update", "publish", "delete"]);
        (await db.Collections.AsNoTracking().SingleAsync(c => c.Id == id, Ct)).DeletedAt.ShouldNotBeNull();

        // Its name is free at once (Decisions "Deleting knowledge bases, sources and assistants"): a new one gets the same slug.
        var again = await CreateAsync(admin, department, "Bygglov och Ärenden " + code);
        again.GetProperty("slug").GetString().ShouldBe($"bygglov-och-arenden-{code.ToLowerInvariant()}");
        again.GetProperty("id").GetGuid().ShouldNotBe(id);
    }

    [Fact]
    public async Task Retrieval_settings_are_checked_against_v16_and_start_the_advisory_evaluation()
    {
        var (department, code) = await DepartmentAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        var created = await CreateAsync(admin, department, "Inställningar " + code);
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetString();

        foreach (var (settings, field) in new (object, string)[]
                 {
                     (new { candidates = 500 }, "settings.candidates"),
                     (new { coverageMaxDistance = 0.01 }, "settings.coverageMaxDistance"),
                     (new { coverageMinKeywordScore = 0 }, "settings.coverageMinKeywordScore"),
                     (new { recency = new { halfLifeDays = 10, weight = 0.1, kinds = new[] { "pdf" } } }, "settings.recency.halfLifeDays"),
                     (new { efSearch = 100 }, "settings.efSearch"),
                     (new { vectorWeight = "high" }, "settings.vectorWeight"),
                 })
        {
            using var refused = await admin.PutAsJsonAsync($"/api/collections/{id}/retrieval-settings", new { settings, version }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, field);
            (await Json(refused)).GetProperty("errorCodes").GetProperty(field)[0].GetProperty("code").GetString().ShouldBe("retrieval_settings_invalid");
        }

        // Without an evaluation set nothing is enqueued; with one, the save enqueues eval on-demand once (dedup eval:{id}).
        var good = new { candidates = 150, coverageMaxDistance = 0.4, recency = new { halfLifeDays = 730, weight = 0.15, kinds = new[] { "pdf" } } };
        using (var saved = await admin.PutAsJsonAsync($"/api/collections/{id}/retrieval-settings", new { settings = good, version }, Ct))
        {
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await Json(saved);
            body.GetProperty("retrievalSettings").GetProperty("candidates").GetInt32().ShouldBe(150);
            version = body.GetProperty("version").GetString();
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Jobs.AsNoTracking().CountAsync(j => j.DedupKey == $"eval:{id}", Ct)).ShouldBe(0);
        db.EvaluationSets.Add(new EvaluationSet { CollectionId = id, Classification = Classification.Public, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);

        for (var i = 0; i < 2; i++)
        {
            using var saved = await admin.PutAsJsonAsync($"/api/collections/{id}/retrieval-settings", new { settings = good, version }, Ct);
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);
            version = (await Json(saved)).GetProperty("version").GetString();
        }

        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"eval:{id}", Ct);
        (job.Class, job.DepartmentId).ShouldBe((JobClasses.Eval, department));
        using var payload = JsonDocument.Parse(job.Payload);
        payload.RootElement.GetProperty("kind").GetString().ShouldBe("on-demand");
        payload.RootElement.GetProperty("collectionId").GetGuid().ShouldBe(id);
    }

    [Fact]
    public async Task Level_changes_follow_v2_and_v3_and_a_used_collection_is_not_deleted()
    {
        var (department, code) = await DepartmentAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        var created = await CreateAsync(admin, department, "Nivåer " + code, Classification.Internal);
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetString();

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var pipeline = new Pipeline { Name = "p-" + Guid.NewGuid().ToString("N"), TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
            db.Pipelines.Add(pipeline);
            db.Sources.Add(new Source { CollectionId = id, Type = SourceTypes.UrlList, Name = "Interna sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow });
            var assistant = new Assistant { Slug = "a-" + Guid.NewGuid().ToString("N"), Name = "Bygglovsassistent", DepartmentId = department, Classification = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
            db.Assistants.Add(assistant);
            await db.SaveChangesAsync(Ct);
            var published = new AssistantVersion
            {
                AssistantId = assistant.Id, Number = 1, State = AssistantVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow, PublishedBy = "test", GatewayModel = "ume/chat-eu", CollectionLinks = [CollectionLink.Whole(id)],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.AssistantVersions.Add(published);
            await db.SaveChangesAsync(Ct);
            assistant.PublishedVersionId = published.Id;
            await db.SaveChangesAsync(Ct);
        }

        using (var lowered = await admin.PutAsJsonAsync($"/api/collections/{id}", new { name = "Nivåer", classification = Classification.Public, version }, Ct))
        {
            lowered.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Json(lowered);
            problem.GetProperty("code").GetString().ShouldBe("collection_level_conflict");
            problem.GetProperty("affected")[0].GetString().ShouldBe("Interna sidor");
        }

        using (var raised = await admin.PutAsJsonAsync($"/api/collections/{id}", new { name = "Nivåer", classification = Classification.Sensitive, version }, Ct))
        {
            raised.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Json(raised);
            problem.GetProperty("code").GetString().ShouldBe("assistant_below_collection");
            problem.GetProperty("affected")[0].GetString().ShouldBe("Bygglovsassistent");
        }

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/collections/{id}", Ct);
        var user = detail.GetProperty("usedBy").EnumerateArray().ShouldHaveSingleItem();
        (user.GetProperty("name").GetString(), user.GetProperty("published").GetBoolean(), user.GetProperty("draft").GetBoolean()).ShouldBe(("Bygglovsassistent", true, false));

        using var delete = await admin.DeleteAsync($"/api/collections/{id}?version={version}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var refused = await Json(delete);
        refused.GetProperty("code").GetString().ShouldBe("collection_in_use");
        refused.GetProperty("affected").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Bygglovsassistent", "Interna sidor"]);
    }

    [Fact]
    public async Task The_list_shows_the_last_fetch_and_the_latest_evaluation_of_own_rows_only()
    {
        var (department, code) = await DepartmentAsync();
        var (otherDepartment, _) = await DepartmentAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        var measured = (await CreateAsync(admin, department, "Mätt " + code)).GetProperty("id").GetGuid();
        var fresh = (await CreateAsync(admin, department, "Ny " + code)).GetProperty("id").GetGuid();
        var now = DateTimeOffset.UtcNow;
        Guid latestRun, liveSource, sharedId;

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var pipeline = new Pipeline { Name = "p-" + Guid.NewGuid().ToString("N"), TemplateId = "url-list", UpdatedAt = now };
            db.Pipelines.Add(pipeline);
            var live = new Source { CollectionId = measured, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Public, PipelineId = pipeline.Id, UpdatedAt = now };
            var deleted = new Source
            {
                CollectionId = measured, Type = SourceTypes.UrlList, Name = "Borttagen", Classification = Classification.Public, PipelineId = pipeline.Id, UpdatedAt = now, DeletedAt = now,
            };
            var profile = await db.EmbeddingProfiles.Where(p => p.IsDefault).Select(p => p.Id).FirstAsync(Ct);
            var shared = new Collection
            {
                Slug = "delad-" + code.ToLowerInvariant(), Name = "Delad " + code, DepartmentId = otherDepartment, Classification = Classification.Public,
                EmbeddingProfileId = profile, SharedWith = CollectionSharingLevels.Municipality, UpdatedAt = now,
            };
            db.AddRange(live, deleted, shared);
            await db.SaveChangesAsync(Ct);
            var sharedSource = new Source { CollectionId = shared.Id, Type = SourceTypes.UrlList, Name = "Delad", Classification = Classification.Public, PipelineId = pipeline.Id, UpdatedAt = now };
            db.Sources.Add(sharedSource);
            await db.SaveChangesAsync(Ct);

            Run RunOf(Guid source, string state, TimeSpan ago, bool finished = true) => new()
            {
                SourceId = source, Classification = Classification.Public, Kind = "sync", Trigger = "schedule", State = state, StartedAt = now - ago,
                FinishedAt = finished ? now - ago + TimeSpan.FromMinutes(5) : null,
            };
            var latest = RunOf(live.Id, RunStates.Degraded, TimeSpan.FromHours(1));
            db.Runs.AddRange(RunOf(live.Id, RunStates.Succeeded, TimeSpan.FromDays(1)), latest, RunOf(deleted.Id, RunStates.Running, TimeSpan.FromMinutes(1), finished: false),
                RunOf(sharedSource.Id, RunStates.Succeeded, TimeSpan.FromMinutes(1)));

            var set = new EvaluationSet { CollectionId = measured, Classification = Classification.Public, CreatedAt = now };
            var sharedSet = new EvaluationSet { CollectionId = shared.Id, Classification = Classification.Public, CreatedAt = now };
            db.EvaluationSets.AddRange(set, sharedSet);
            await db.SaveChangesAsync(Ct);
            EvaluationRun EvalOf(Guid setId, decimal recall, TimeSpan ago, bool finished = true, bool candidate = false, string variant = EvaluationVariants.Hybrid) => new()
            {
                SetId = setId, Classification = Classification.Public, Kind = EvaluationRunKinds.OnDemand, StartedAt = now - ago, FinishedAt = finished ? now - ago : null,
                RecallAtK = recall, Candidate = candidate, Variant = variant,
            };
            db.EvaluationRuns.AddRange(
                EvalOf(set.Id, 0.5m, TimeSpan.FromDays(2)),
                EvalOf(set.Id, 0.8m, TimeSpan.FromDays(1)),
                EvalOf(set.Id, 0.9m, TimeSpan.FromHours(3), candidate: true),
                EvalOf(set.Id, 0.95m, TimeSpan.FromHours(2), variant: EvaluationVariants.Vector),
                EvalOf(set.Id, 0.99m, TimeSpan.FromHours(1), finished: false),
                EvalOf(sharedSet.Id, 0.7m, TimeSpan.FromHours(1)));
            await db.SaveChangesAsync(Ct);
            (latestRun, liveSource, sharedId) = (latest.Id, live.Id, shared.Id);
        }

        var items = (await admin.GetFromJsonAsync<JsonElement>($"/api/collections?view=all&q={code}", Ct)).GetProperty("items").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetGuid());
        items.Count.ShouldBe(3);

        // The latest run of a source that is not deleted, whatever its state; the latest finished hybrid run of the saved settings.
        var run = items[measured].GetProperty("lastRun");
        (run.GetProperty("id").GetGuid(), run.GetProperty("sourceId").GetGuid(), run.GetProperty("state").GetString()).ShouldBe((latestRun, liveSource, RunStates.Degraded));
        run.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(now - TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(1));
        run.GetProperty("finishedAt").ValueKind.ShouldBe(JsonValueKind.String);
        var evaluation = items[measured].GetProperty("latestEvaluation");
        evaluation.GetProperty("recallAtK").GetDecimal().ShouldBe(0.8m);
        evaluation.GetProperty("finishedAt").GetDateTimeOffset().ShouldBe(now - TimeSpan.FromDays(1), TimeSpan.FromMilliseconds(1));

        // Before the first fetch and evaluation; and a shared row never shows its sources or evaluation.
        items[fresh].GetProperty("lastRun").ValueKind.ShouldBe(JsonValueKind.Null);
        items[fresh].GetProperty("latestEvaluation").ValueKind.ShouldBe(JsonValueKind.Null);
        items[sharedId].GetProperty("shared").GetBoolean().ShouldBeTrue();
        items[sharedId].GetProperty("lastRun").ValueKind.ShouldBe(JsonValueKind.Null);
        items[sharedId].GetProperty("latestEvaluation").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
