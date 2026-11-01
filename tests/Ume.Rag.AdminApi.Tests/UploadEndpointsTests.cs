using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Upload batches (admin design §4.5, connectors design §9.1, #92): files staged and hashed while streaming, the extension
/// pre-check (415), replace by slot, commit writing the manifest and the <c>sync-upload</c> job, discard; and the per-endpoint
/// body limit (to-verify #32: 60 MB for a file, 1 MB for everything else).
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class UploadEndpointsTests(AdminDatabaseFixture database) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _blobs = Directory.CreateTempSubdirectory("ume-rag-admin-blobs-").FullName;

    public void Dispose() => Directory.Delete(_blobs, recursive: true);

    private WebApplicationFactory<Program> Factory() => database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("BlobStore:Root", _blobs));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>A department with an internal collection and an upload source (max 1 MB per file).</summary>
    private async Task<(Guid Source, string Code)> UploadSourceAsync()
    {
        var code = "U" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Upl " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "upl-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "upl-" + code.ToLowerInvariant(), Name = "Filer " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile.Value, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "files-" + code, TemplateId = "files", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.Upload, Name = "Taxor", Classification = Classification.Internal, PipelineId = pipeline.Id,
            Settings = """{"maxFileSizeMb":1}""", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, pipeline, source);
        await db.SaveChangesAsync(Ct);
        return (source.Id, code);
    }

    private static async Task<HttpResponseMessage> PutFileAsync(HttpClient client, Guid batch, int n, string name, byte[] bytes, Guid? replaces = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/upload-batches/{batch}/files/{n}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        if (replaces is { } slot)
        {
            request.Headers.Add("X-Upload-Id", slot.ToString());
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task A_batch_stages_files_commits_the_manifest_and_the_upload_sync()
    {
        var (sourceId, code) = await UploadSourceAsync();
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);

        using var create = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var batch = (await Json(create)).GetProperty("batchId").GetGuid();

        var pdf = "%PDF-1.7 taxa för bygglov"u8.ToArray();
        using (var first = await PutFileAsync(admin, batch, 1, "Taxa för bygglov.pdf", pdf))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
            var file = await Json(first);
            file.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(pdf)));
            file.GetProperty("operation").GetString().ShouldBe("add");
        }

        (await PutFileAsync(admin, batch, 1, "again.pdf", pdf)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using (var legacy = await PutFileAsync(admin, batch, 2, "gammal.doc", pdf))
        {
            legacy.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
            (await Json(legacy)).GetProperty("code").GetString().ShouldBe("upload_format_deferred");
        }

        (await PutFileAsync(admin, batch, 2, "program.exe", pdf)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        using (var large = await PutFileAsync(admin, batch, 3, "stor.pdf", new byte[1024 * 1024 + 1]))
        {
            large.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            (await Json(large)).GetProperty("code").GetString().ShouldBe("upload_too_large");
        }

        using (var missing = await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1, 7 } }, Ct))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var commit = await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1 } }, Ct))
        {
            commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync(Ct));
            (await Json(commit)).GetProperty("entries").GetInt32().ShouldBe(1);
        }

        // The manifest the worker reads (contracts/jobs/v1/upload-batch.manifest.schema.json) and the promoted file.
        var manifestPath = Path.Combine(_blobs, "uploads", sourceId.ToString("D"), "batches", $"{batch:D}.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, Ct));
        var entry = manifest.RootElement.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("operation").GetString().ShouldBe("add");
        entry.GetProperty("fileName").GetString().ShouldBe("Taxa för bygglov.pdf");
        var slot = entry.GetProperty("uploadId").GetGuid();
        var sha = entry.GetProperty("sha256").GetString()!;
        (await File.ReadAllBytesAsync(Path.Combine(_blobs, "uploads", sourceId.ToString("D"), slot.ToString("D"), sha), Ct)).ShouldBe(pdf);
        Directory.Exists(Path.Combine(_blobs, "tmp", "upload-batches", batch.ToString("D"))).ShouldBeFalse();
        ContractSchemaCheck(manifest.RootElement);

        await using var db = TestPostgres.Context(database.Connection(null));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"sync-upload:{batch}", Ct);
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            (payload.RootElement.GetProperty("trigger").GetString(), payload.RootElement.GetProperty("batchId").GetGuid()).ShouldBe(("upload", batch));
        }

        (await db.AuditLog.AsNoTracking().CountAsync(a => a.EntityType == "source" && a.EntityId == sourceId.ToString() && a.Action == "upload-commit", Ct)).ShouldBe(1);

        // A committed batch is gone; a discarded one too.
        (await PutFileAsync(admin, batch, 2, "sen.pdf", pdf)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var second = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        var discarded = (await Json(second)).GetProperty("batchId").GetGuid();
        (await PutFileAsync(admin, discarded, 1, "x.pdf", pdf)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/upload-batches/{discarded}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/upload-batches/{discarded}/commit", new { files = new[] { 1 } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(_blobs, "tmp", "upload-batches", discarded.ToString("D"))).ShouldBeFalse();
    }

    private static void ContractSchemaCheck(JsonElement manifest) =>
        Ume.Rag.Pipelines.Schemas.ContractSchemas.Validator(typeof(Ume.Rag.Pipelines.Jobs.UploadBatchManifest)).Validate(manifest).ShouldBeEmpty();

    [Fact]
    public async Task Only_the_file_request_lifts_the_1_mb_body_limit()
    {
        // to-verify #32: Kestrel's MaxRequestBodySize stays 1 MB for the admin; the upload file endpoint alone declares 60 MB
        // (IRequestSizeLimitMetadata, applied by the endpoint middleware before the body is read).
        await using var factory = Factory();
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>()
            .Value.Limits.MaxRequestBodySize.ShouldBe(1024 * 1024);
        var limits = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>() is not null)
            .Select(e => (e.RoutePattern.RawText, e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()!.MaxRequestBodySize))
            .ToList();
        limits.ShouldBe([("/api/upload-batches/{batchId:guid}/files/{n}", 60L * 1024 * 1024)]);

        // And a file of more than 1 MB goes through the endpoint (the source allows 50 MB).
        var (sourceId, code) = await UploadSourceAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.Sources.SingleAsync(s => s.Id == sourceId, Ct)).Settings = """{"maxFileSizeMb":50}""";
            await db.SaveChangesAsync(Ct);
        }

        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var create = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        var batch = (await Json(create)).GetProperty("batchId").GetGuid();
        using var file = await PutFileAsync(admin, batch, 1, "stor.pdf", new byte[3 * 1024 * 1024]);
        file.StatusCode.ShouldBe(HttpStatusCode.OK, await file.Content.ReadAsStringAsync(Ct));
    }
}
