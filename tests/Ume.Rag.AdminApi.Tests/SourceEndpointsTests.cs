using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Sources and runs (admin design §4.5, §4.6 V1, V25; §4.7 sync and cancel-run; P1a item 14b): settings checked against
/// the J3 schemas, the impact token for scope changes, sync now with dedup and the running run, runs and items by keyset,
/// and cancel-run.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class SourceEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>A department, the platform url-list pipeline (as the migrator seeds it) and an internal collection.</summary>
    private async Task<(Guid Collection, string Code)> CollectionAsync()
    {
        var code = "S" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Src " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        db.Departments.Add(department);
        if (!await db.Pipelines.AnyAsync(p => p.DepartmentId == null && p.TemplateId == "url-list" && p.PublishedVersionId != null, Ct))
        {
            var pipeline = new Pipeline { Name = "url-list", TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
            var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
            db.Pipelines.Add(pipeline);
            db.PipelineVersions.Add(version);
            await db.SaveChangesAsync(Ct);
            pipeline.PublishedVersionId = version.Id;
        }

        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "src-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "src-" + code.ToLowerInvariant(), Name = "Källor " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile.Value, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(Ct);
        return (collection.Id, code);
    }

    private static object UrlList(params string[] urls) => new { urls };

    [Fact]
    public async Task The_first_website_source_turns_on_age_decay_for_pdfs_unless_recency_is_set()
    {
        // weakness review §3, admin design §5.4 step 3: half-life 730 days, weight 0.15, PDFs; audited on the collection.
        var (collection, code) = await CollectionAsync();
        var (keptOff, _) = await CollectionAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            if (!await db.Pipelines.AnyAsync(p => p.DepartmentId == null && p.TemplateId == "website" && p.PublishedVersionId != null, Ct))
            {
                var pipeline = new Pipeline { Name = "website", TemplateId = "website", UpdatedAt = DateTimeOffset.UtcNow };
                var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
                db.Pipelines.Add(pipeline);
                db.PipelineVersions.Add(version);
                await db.SaveChangesAsync(Ct);
                pipeline.PublishedVersionId = version.Id;
                await db.SaveChangesAsync(Ct);
            }

            await db.Collections.Where(c => c.Id == keptOff).ExecuteUpdateAsync(c => c.SetProperty(x => x.RetrievalSettings, """{"recency": null}"""), Ct);
            await db.Departments.Where(d => d.Id == db.Collections.Where(c => c.Id == keptOff).Select(c => c.DepartmentId).Single())
                .ExecuteUpdateAsync(d => d.SetProperty(x => x.CostCenterCode, code + "B"), Ct);
        }

        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin, code);
        object Crawl(string host) => new { type = "crawl", name = host, classification = "internal", settings = new { startUrls = new[] { $"https://{host}/" } } };
        async Task<string> SettingsAsync(Guid id)
        {
            await using var db = TestPostgres.Context(database.Connection(null));
            return await db.Collections.AsNoTracking().Where(c => c.Id == id).Select(c => c.RetrievalSettings).SingleAsync(Ct);
        }

        using (var first = await admin.PostAsJsonAsync($"/api/collections/{collection}/sources", Crawl("www.a.se"), Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        }

        using (var settings = JsonDocument.Parse(await SettingsAsync(collection)))
        {
            var recency = settings.RootElement.GetProperty("recency");
            (recency.GetProperty("halfLifeDays").GetInt32(), recency.GetProperty("weight").GetDouble(), recency.GetProperty("kinds")[0].GetString())
                .ShouldBe((730, 0.15, "pdf"));
        }

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityId == collection.ToString() && a.Action == "retrieval-settings").ToListAsync(Ct);
            audit.ShouldHaveSingleItem().Details!.ShouldContain("first-website-source");
        }

        // An editor's change survives a second website source; an explicit null stays off.
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Collections.Where(c => c.Id == collection).ExecuteUpdateAsync(c => c.SetProperty(x => x.RetrievalSettings, """{"recency": {"halfLifeDays": 365, "weight": 0.3, "kinds": ["pdf"]}}"""), Ct);
        }

        using (var second = await admin.PostAsJsonAsync($"/api/collections/{collection}/sources", Crawl("www.b.se"), Ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await SettingsAsync(collection)).ShouldContain("365");
        using (var off = await admin.PostAsJsonAsync($"/api/collections/{keptOff}/sources", Crawl("www.c.se"), Ct))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.Created, await off.Content.ReadAsStringAsync(Ct));
        }

        (await SettingsAsync(keptOff)).ShouldNotContain("730");
    }

    [Fact]
    public async Task A_source_is_validated_against_the_contract_and_a_scope_change_needs_the_impact_token()
    {
        var (collection, code) = await CollectionAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        var path = $"/api/collections/{collection}/sources";

        async Task<string> Refused(object body, string field)
        {
            using var response = await admin.PostAsJsonAsync(path, body, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, text);
            var codes = JsonDocument.Parse(text).RootElement.GetProperty("errorCodes");
            codes.TryGetProperty(field, out _).ShouldBeTrue(text);
            return codes.GetProperty(field)[0].GetProperty("code").GetString()!;
        }

        (await Refused(new { type = "url-list", name = "A", classification = "internal", settings = new { urls = Array.Empty<string>() } }, "settings.urls"))
            .ShouldBe("source_settings_invalid");
        (await Refused(new { type = "url-list", name = "A", classification = "internal", settings = new { urls = new[] { "https://a.se/x" }, colour = "red" } }, "settings.colour"))
            .ShouldBe("source_settings_invalid");
        (await Refused(new { type = "url-list", name = "A", classification = "internal", settings = UrlList("https://a.se/x", "https://a.se/x") }, "settings.urls"))
            .ShouldBe("source_settings_invalid");
        (await Refused(new { type = "url-list", name = "A", classification = "sensitive", settings = UrlList("https://a.se/x") }, "classification"))
            .ShouldBe("source_above_collection");
        (await Refused(new { type = "url-list", name = "A", classification = "internal", settings = UrlList("https://a.se/x"), containsThirdPartyText = false }, "containsThirdPartyText"))
            .ShouldBe("admin_only");

        using var created = await admin.PostAsJsonAsync(path, new { type = "url-list", name = "Taxor", classification = "internal", settings = UrlList("https://a.se/x") }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var source = await Json(created);
        var id = source.GetProperty("id").GetGuid();
        source.GetProperty("containsThirdPartyText").GetBoolean().ShouldBeTrue();
        var version = source.GetProperty("version").GetString();

        // A behaviour change saves directly; a scope change (the URL list) needs the impact token of exactly that change.
        using (var behaviour = await admin.PutAsJsonAsync($"/api/sources/{id}",
                   new { name = "Taxor", classification = "internal", settings = new { urls = new[] { "https://a.se/x" }, timeoutSeconds = 60 }, version }, Ct))
        {
            behaviour.StatusCode.ShouldBe(HttpStatusCode.OK, await behaviour.Content.ReadAsStringAsync(Ct));
            version = (await Json(behaviour)).GetProperty("version").GetString();
        }

        var scope = new { urls = new[] { "https://a.se/x", "https://a.se/y" }, timeoutSeconds = 60 };
        using (var unconfirmed = await admin.PutAsJsonAsync($"/api/sources/{id}", new { name = "Taxor", classification = "internal", settings = scope, version }, Ct))
        {
            unconfirmed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(unconfirmed)).GetProperty("code").GetString().ShouldBe("impact_not_confirmed");
        }

        using var impact = await admin.PostAsJsonAsync($"/api/sources/{id}/impact", new { settings = scope }, Ct);
        var report = await Json(impact);
        report.GetProperty("effects").EnumerateArray().Select(e => e.GetString()).ShouldContain("scope");
        report.GetProperty("liveDocuments").GetInt32().ShouldBe(0);
        var token = report.GetProperty("impactToken").GetString();
        token.ShouldNotBeNullOrEmpty();

        using (var confirmed = await admin.PutAsJsonAsync($"/api/sources/{id}", new { name = "Taxor", classification = "internal", settings = scope, version, impactToken = token }, Ct))
        {
            confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
            (await Json(confirmed)).GetProperty("settings").GetProperty("urls").GetArrayLength().ShouldBe(2);
        }

        (await admin.GetFromJsonAsync<JsonElement[]>($"/api/collections/{collection}/sources", Ct))!.ShouldHaveSingleItem().GetProperty("name").GetString().ShouldBe("Taxor");

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "source" && a.EntityId == id.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["create", "update", "update"]);
    }

    [Fact]
    public async Task Sync_now_enqueues_once_answers_the_running_run_and_runs_are_listed_and_cancelled()
    {
        var (collection, code) = await CollectionAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "OTHER" + code);
        using var created = await admin.PostAsJsonAsync($"/api/collections/{collection}/sources",
            new { type = "url-list", name = "Sidor", classification = "internal", settings = UrlList("https://a.se/x") }, Ct);
        var id = (await Json(created)).GetProperty("id").GetGuid();

        Guid firstJob;
        using (var sync = await admin.PostAsync($"/api/sources/{id}/sync", null, Ct))
        {
            firstJob = (await Json(sync)).GetProperty("jobId").GetGuid();
        }

        using (var queued = await admin.PostAsync($"/api/sources/{id}/sync", null, Ct)) // dedup sync:{id}: the queued job is answered
        {
            var body = await Json(queued);
            (body.GetProperty("jobId").GetGuid(), body.GetProperty("alreadyRunning").GetBoolean(), body.GetProperty("kind").GetString()).ShouldBe((firstJob, true, "sync"));
        }
        (await stranger.PostAsync($"/api/sources/{id}/sync", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var db = TestPostgres.Context(database.Connection(null));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"sync:{id}", Ct);
        (job.Class, job.Priority).ShouldBe((JobClasses.Sync, (short)10));
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            (payload.RootElement.GetProperty("sourceId").GetGuid(), payload.RootElement.GetProperty("kind").GetString(), payload.RootElement.GetProperty("trigger").GetString())
                .ShouldBe((id, "sync", "manual"));
        }

        // The worker opened two runs; the newer is running with three items.
        var now = DateTimeOffset.UtcNow;
        var old = new Run { SourceId = id, Classification = "internal", Kind = "sync", Trigger = "manual", State = RunStates.Succeeded, StartedAt = now.AddHours(-1), FinishedAt = now.AddMinutes(-50), Report = """{"traversal":"complete"}""" };
        var running = new Run { SourceId = id, Classification = "internal", Kind = "sync", Trigger = "manual", State = RunStates.Running, StartedAt = now };
        db.Runs.AddRange(old, running);
        await db.SaveChangesAsync(Ct);
        db.RunItems.AddRange(
            new RunItem { RunId = running.Id, CreatedAt = now, Classification = "internal", Identity = "https://a.se/x", Outcome = "added" },
            new RunItem { RunId = running.Id, CreatedAt = now, Classification = "internal", Identity = "https://a.se/y", Outcome = "failed", Reason = "http_404" },
            new RunItem { RunId = running.Id, CreatedAt = now, Classification = "internal", Identity = "https://a.se/z", Outcome = "failed", Reason = "http_404" });
        await db.SaveChangesAsync(Ct);

        using (var again = await admin.PostAsync($"/api/sources/{id}/sync", null, Ct))
        {
            var body = await Json(again);
            (body.GetProperty("alreadyRunning").GetBoolean(), body.GetProperty("runId").GetGuid()).ShouldBe((true, running.Id));
        }

        var first = await admin.GetFromJsonAsync<JsonElement>($"/api/sources/{id}/runs?limit=1", Ct);
        first.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(running.Id);
        var next = first.GetProperty("next").GetGuid();
        var second = await admin.GetFromJsonAsync<JsonElement>($"/api/sources/{id}/runs?limit=1&after={next}", Ct);
        second.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(old.Id);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/runs/{old.Id}", Ct);
        detail.GetProperty("report").GetProperty("traversal").GetString().ShouldBe("complete");
        var failed = await admin.GetFromJsonAsync<JsonElement>($"/api/runs/{running.Id}/items?outcome=failed&limit=1", Ct);
        failed.GetProperty("items")[0].GetProperty("reason").GetString().ShouldBe("http_404");
        var rest = await admin.GetFromJsonAsync<JsonElement>($"/api/runs/{running.Id}/items?outcome=failed&after={failed.GetProperty("next").GetInt64()}", Ct);
        rest.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await stranger.GetAsync($"/api/runs/{running.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await admin.PostAsync($"/api/runs/{running.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var ended = await admin.PostAsync($"/api/runs/{old.Id}/cancel", null, Ct))
        {
            ended.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(ended)).GetProperty("code").GetString().ShouldBe("run_not_active");
        }

        var cancel = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"cancel:{running.Id}", Ct);
        (cancel.Class, cancel.RunId).ShouldBe((JobClasses.Command, running.Id));

        // A paused source is not synced (the worker would start no run): 409 source_paused, no implicit resume.
        var current = (await admin.GetFromJsonAsync<JsonElement>($"/api/sources/{id}", Ct)).GetProperty("version").GetString();
        (await admin.PostAsJsonAsync($"/api/sources/{id}/pause", new { version = current }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var paused = await admin.PostAsync($"/api/sources/{id}/sync", null, Ct);
        paused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(paused)).GetProperty("code").GetString().ShouldBe("source_paused");
    }
}
