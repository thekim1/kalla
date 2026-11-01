using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Jobs and previews (admin design §4.5, §4.7, P1b item 9): polling a job in the caller's departments, the preflight of
/// unsaved settings (V25 first, a URL list's first 20 URLs), the PII highlights (<c>stage-preview</c> to personal-data,
/// audited <c>pii.view</c>), and a preview output read only from the job's own <c>previews/{jobId}/</c>.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class PreviewEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid Department, Guid Collection, Guid Source, Guid Document, Guid PipelineVersion);

    private async Task<Seed> SeedAsync()
    {
        var code = "V" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Prev " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = new EmbeddingProfile
        {
            Name = "prev-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var collection = new Collection
        {
            Slug = "prev-" + code.ToLowerInvariant(), Name = "Prev " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "prev-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Webb", Classification = Classification.Public, PipelineId = pipeline.Id,
            Settings = $$"""{"urls":["https://www.umea.se/{{code}}"]}""", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, profile, collection, pipeline, version, source);
        await db.SaveChangesAsync(Ct);
        var document = new Document
        {
            SourceId = source.Id, Classification = Classification.Public, IdentityKind = "url", CanonicalIdentity = $"https://www.umea.se/{code}/kontakt",
            IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(code)), Title = "Kontakt", Url = $"https://www.umea.se/{code}/kontakt",
        };
        db.Add(document);
        await db.SaveChangesAsync(Ct);
        var set = new ChunkSet
        {
            DocumentId = document.Id, Classification = Classification.Public, CollectionId = collection.Id, PipelineVersionId = version.Id,
            EmbeddingProfileId = profile.Id, ContentHash = RandomNumberGenerator.GetBytes(32), InputHash = RandomNumberGenerator.GetBytes(32),
            State = ChunkSetStates.Live, PiiFlagged = true, CreatedAt = DateTimeOffset.UtcNow, LiveAt = DateTimeOffset.UtcNow,
        };
        db.Add(set);
        await db.SaveChangesAsync(Ct);
        document.LiveChunkSetId = set.Id;
        await db.SaveChangesAsync(Ct);
        return new Seed(code, department.Id, collection.Id, source.Id, document.Id, version.Id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<Job> JobAsync(Guid id)
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }

    private static void Valid<T>(Job job)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        ContractSchemas.Validator(typeof(T)).Validate(payload.RootElement).ShouldBeEmpty(job.Payload);
    }

    [Fact]
    public async Task Preflight_checks_the_settings_first_samples_twenty_urls_and_the_job_is_polled_in_scope()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);
        var urls = Enumerable.Range(1, 25).Select(i => $"https://www.umea.se/{seed.Code}/sida-{i}").ToArray();

        (await viewer.PostAsJsonAsync("/api/sources/preflight", new { collectionId = seed.Collection, type = "url-list", settings = new { urls } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var invalid = await editor.PostAsJsonAsync("/api/sources/preflight",
                   new { collectionId = seed.Collection, type = "url-list", settings = new { urls, depth = 3 } }, Ct))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await invalid.Content.ReadAsStringAsync(Ct)).ShouldContain("source_settings_invalid");
        }

        (await editor.PostAsJsonAsync("/api/sources/preflight", new { collectionId = seed.Collection, type = "upload", settings = new { } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stranger.PostAsJsonAsync("/api/sources/preflight", new { collectionId = seed.Collection, type = "url-list", settings = new { urls } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var started = await editor.PostAsJsonAsync("/api/sources/preflight", new { collectionId = seed.Collection, type = "url-list", settings = new { urls } }, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, await started.Content.ReadAsStringAsync(Ct));
        var jobId = (await Json(started)).GetProperty("jobId").GetGuid();
        var job = await JobAsync(jobId);
        (job.Class, job.DepartmentId, job.Classification).ShouldBe((JobClasses.Preview, seed.Department, Classification.Public));
        Valid<PreflightPayload>(job);
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            payload.RootElement.GetProperty("settings").GetProperty("urls").EnumerateArray().Select(u => u.GetString()).ShouldBe(urls.Take(20));
        }

        var polled = await viewer.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Ct);
        (polled.GetProperty("kind").GetString(), polled.GetProperty("state").GetString(), polled.GetProperty("class").GetString())
            .ShouldBe(("preflight", JobStates.Queued, JobClasses.Preview));
        (await stranger.GetAsync($"/api/jobs/{jobId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pii_preview_needs_manage_runs_to_the_personal_data_stage_with_spans_and_is_audited()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);

        (await editor.PostAsync($"/api/documents/{seed.Document}/pii-preview", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var started = await admin.PostAsync($"/api/documents/{seed.Document}/pii-preview", null, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, await started.Content.ReadAsStringAsync(Ct));
        var job = await JobAsync((await Json(started)).GetProperty("jobId").GetGuid());
        job.DedupKey.ShouldBe($"pii-preview:{seed.Document}");
        Valid<StagePreviewPayload>(job);
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            var root = payload.RootElement;
            (root.GetProperty("untilStage").GetString(), root.GetProperty("piiSpans").GetBoolean(), root.GetProperty("pipelineVersionId").GetGuid(),
                root.GetProperty("sourceId").GetGuid()).ShouldBe(("personal-data", true, seed.PipelineVersion, seed.Source));
            root.GetProperty("documentIds").EnumerateArray().Select(d => d.GetGuid()).ShouldBe([seed.Document]);
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.AuditLog.AsNoTracking().CountAsync(a => a.Action == "pii.view" && a.EntityId == seed.Document.ToString(), Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_preview_output_is_read_from_the_job_s_own_blobs_only()
    {
        var seed = await SeedAsync();
        var root = Directory.CreateTempSubdirectory("rag-preview-blobs-");
        var keys = Directory.CreateTempSubdirectory("rag-preview-keys-");
        try
        {
            await ReadsOwnBlobsAsync(seed, root.FullName, keys.FullName);
        }
        finally
        {
            root.Delete(recursive: true);
            keys.Delete(recursive: true);
        }
    }

    private async Task ReadsOwnBlobsAsync(Seed seed, string root, string keys)
    {
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("BlobStore:Root", root);
            web.UseSetting("KeyProvider:Directory", keys);
        });
        using var pipelines = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PipelineEditor, seed.Code);
        using var outputEditor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        var jobId = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var own = $"previews/{jobId:D}/documents/{seed.Document:D}/parse.md";
        var foreign = $"previews/{other:D}/documents/{seed.Document:D}/clean.md";
        var blobs = factory.Services.GetRequiredService<IBlobStore>().ForLevel(Classification.Public);
        await blobs.PutAsync(new BlobKey(own), new MemoryStream(Encoding.UTF8.GetBytes("# Kontakt\n\nRing 090-16 10 00.")), null, Ct);
        await blobs.PutAsync(new BlobKey(foreign), new MemoryStream(Encoding.UTF8.GetBytes("någon annans")), null, Ct);
        var result = JsonSerializer.Serialize(new
        {
            documents = new[]
            {
                new
                {
                    documentId = seed.Document,
                    stages = new object[]
                    {
                        new { stage = "parse", state = "succeeded", output = own },
                        new { stage = "clean", state = "succeeded", output = foreign }, // a path outside the job's prefix
                    },
                },
            },
            expiresAt = DateTimeOffset.UtcNow.AddHours(24),
        });
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.Jobs.Add(new Job
            {
                Id = jobId, Class = JobClasses.Preview, DepartmentId = seed.Department, Classification = Classification.Public,
                Payload = """{"kind":"stage-preview"}""", State = JobStates.Succeeded, Result = result, EnqueuedAt = DateTimeOffset.UtcNow,
                AvailableAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var path = $"/api/previews/{jobId}/documents/{seed.Document}/stages";
        (await viewer.GetAsync($"{path}/parse/output", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await outputEditor.GetAsync($"{path}/parse/output", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // #283: Edit or EditPipelines
        using var parse = await pipelines.GetAsync($"{path}/parse/output", Ct);
        parse.StatusCode.ShouldBe(HttpStatusCode.OK);
        parse.Content.Headers.ContentType!.MediaType.ShouldBe("text/markdown");
        (await parse.Content.ReadAsStringAsync(Ct)).ShouldBe("# Kontakt\n\nRing 090-16 10 00.");
        (await pipelines.GetAsync($"{path}/clean/output", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await pipelines.GetAsync($"{path}/chunk/output", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await pipelines.GetAsync($"/api/previews/{jobId}/documents/{Guid.NewGuid()}/stages/parse/output", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
