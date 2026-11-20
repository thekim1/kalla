using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Upload batches (admin design §4.5, connectors design §9.1, #92): files staged and hashed while streaming, the extension
/// pre-check (415), replace by slot, commit writing the manifest and the <c>sync-upload</c> job, discard; and the per-endpoint
/// body limit (to-verify #32: 60 MB for a file, 1 MB for everything else).
/// </summary>
[Collection(AdminDatabaseGroup8.Name)]
public sealed class UploadEndpointsTests(AdminDatabaseFixture database, UploadEndpointsTests.Host host) : IClassFixture<UploadEndpointsTests.Host>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The class's admin host on a blob store and a key directory of its own, deleted after the class.</summary>
    public sealed class Host(AdminDatabaseFixture database) : AdminHost(database)
    {
        public string Blobs { get; } = Directory.CreateTempSubdirectory("ume-rag-admin-blobs-").FullName;

        private string Keys => Path.Combine(Path.GetDirectoryName(Blobs)!, Path.GetFileName(Blobs) + "-keys");

        protected override WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> host)
        {
            Directory.CreateDirectory(Keys);
            File.WriteAllText(Path.Combine(Keys, "rag-blob-internal"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
            return host.WithWebHostBuilder(web =>
            {
                web.UseSetting("BlobStore:Root", Blobs);
                web.UseSetting("KeyProvider:Directory", Keys);
            });
        }

        protected override ValueTask CleanupAsync()
        {
            Directory.Delete(Blobs, recursive: true);
            if (Directory.Exists(Keys))
            {
                Directory.Delete(Keys, recursive: true);
            }

            return default;
        }
    }

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
        var factory = host.Factory;
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
        using (var legacy = await PutFileAsync(admin, batch, 2, "mejl.msg", pdf))
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
        var manifestPath = Path.Combine(host.Blobs, "uploads", sourceId.ToString("D"), "batches", $"{batch:D}.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, Ct));
        var entry = manifest.RootElement.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("operation").GetString().ShouldBe("add");
        entry.GetProperty("fileName").GetString().ShouldBe("Taxa för bygglov.pdf");
        var slot = entry.GetProperty("uploadId").GetGuid();
        var sha = entry.GetProperty("sha256").GetString()!;
        // The source is internal: the stored blob is URB2 (#181) and decrypts to exactly the upload; its key ends in the plaintext's SHA-256.
        var stored = await File.ReadAllBytesAsync(Path.Combine(host.Blobs, "uploads", sourceId.ToString("D"), slot.ToString("D"), sha), Ct);
        stored[..4].ShouldBe("URB2"u8.ToArray());
        var envelope = factory.Services.GetRequiredService<Kalla.Infrastructure.Blobs.IBlobEnvelope>();
        var plain = (await envelope.OpenReadAsync(new BlobKey($"uploads/{sourceId:D}/{slot:D}/{sha}"), Classification.Internal, Ct)).ShouldNotBeNull();
        await using (plain.Content)
        {
            using var buffer = new MemoryStream();
            await plain.Content.CopyToAsync(buffer, Ct);
            buffer.ToArray().ShouldBe(pdf);
        }

        Directory.Exists(Path.Combine(host.Blobs, "tmp", "upload-batches", batch.ToString("D"), "data")).ShouldBeFalse();
        ContractSchemaCheck(manifest.RootElement);

        await using var db = TestPostgres.Context(database.Connection(null));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"sync-upload:{batch}", Ct);
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            (payload.RootElement.GetProperty("trigger").GetString(), payload.RootElement.GetProperty("batchId").GetGuid()).ShouldBe(("upload", batch));
        }

        (await db.AuditLog.AsNoTracking().CountAsync(a => a.EntityType == "source" && a.EntityId == sourceId.ToString() && a.Action == "upload-commit", Ct)).ShouldBe(1);

        // A repeated commit (a lost response) answers the same job; one with other entries, or another file, is 409.
        var jobId = job.Id;
        using (var repeat = await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1 } }, Ct))
        {
            repeat.StatusCode.ShouldBe(HttpStatusCode.OK, await repeat.Content.ReadAsStringAsync(Ct));
            (await Json(repeat)).GetProperty("jobId").GetGuid().ShouldBe(jobId);
        }

        using (var other = await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1, 2 } }, Ct))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(other)).GetProperty("code").GetString().ShouldBe("upload_batch_committed");
        }

        (await PutFileAsync(admin, batch, 2, "sen.pdf", pdf)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await db.Jobs.AsNoTracking().CountAsync(j => j.DedupKey == $"sync-upload:{batch}", Ct)).ShouldBe(1);
        using var second = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        var discarded = (await Json(second)).GetProperty("batchId").GetGuid();
        (await PutFileAsync(admin, discarded, 1, "x.pdf", pdf)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutFileAsync(admin, discarded, 2, "y.pdf", "%PDF-1.7 annan"u8.ToArray())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/upload-batches/{discarded}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/upload-batches/{discarded}/commit", new { files = new[] { 1 } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(host.Blobs, "tmp", "upload-batches", discarded.ToString("D"))).ShouldBeFalse();
    }

    private static void ContractSchemaCheck(JsonElement manifest) =>
        Kalla.Pipelines.Schemas.ContractSchemas.Validator(typeof(Kalla.Pipelines.Jobs.UploadBatchManifest)).Validate(manifest).ShouldBeEmpty();

    [Fact]
    public async Task Only_the_file_request_lifts_the_1_mb_body_limit()
    {
        // to-verify #32: Kestrel's MaxRequestBodySize stays 1 MB for the admin; the upload file endpoint alone declares 60 MB
        // (IRequestSizeLimitMetadata, applied by the endpoint middleware before the body is read).
        var factory = host.Factory;
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

    [Fact]
    public async Task On_Kestrel_the_file_request_takes_60_mb_and_every_other_request_1_mb()
    {
        // to-verify #32 on real Kestrel (the TestServer above enforces no limit). An oversized body is sent with
        // Expect: 100-continue, so Kestrel refuses it from the Content-Length before the client sends it.
        var (sourceId, code) = await UploadSourceAsync();
        async Task MaxFileSizeAsync(int mb)
        {
            await using var db = TestPostgres.Context(database.Connection(null));
            (await db.Sources.SingleAsync(s => s.Id == sourceId, Ct)).Settings = $$"""{"maxFileSizeMb":{{mb}}}""";
            await db.SaveChangesAsync(Ct);
        }

        await MaxFileSizeAsync(50);
        await using var kestrel = new KestrelHost<Program>(host.Factory);
        using var admin = kestrel.CreateClient();
        admin.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, RagRoles.DepartmentAdmin);
        admin.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.DepartmentHeader, code);
        await admin.WithXsrfTokenAsync();
        using var create = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        var batch = (await Json(create)).GetProperty("batchId").GetGuid();

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, string contentType)
        {
            using var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Headers.Add("X-File-Name", "stor.pdf");
            request.Headers.ExpectContinue = true;
            return await admin.SendAsync(request, Ct);
        }

        // The file route: a 50 MB file (the platform cap) passes. A larger body meets the source's own limit first, from the
        // Content-Length before the body is read (413 upload_too_large).
        using (var at = await SendAsync(HttpMethod.Put, $"/api/upload-batches/{batch}/files/1", new byte[50 * 1024 * 1024], "application/pdf"))
        {
            at.StatusCode.ShouldBe(HttpStatusCode.OK, await at.Content.ReadAsStringAsync(Ct));
        }

        using (var source = await SendAsync(HttpMethod.Put, $"/api/upload-batches/{batch}/files/2", new byte[UploadEndpoints.FileRequestLimit + 1], "application/pdf"))
        {
            source.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            (await Json(source)).GetProperty("code").GetString().ShouldBe("upload_too_large");
        }

        // Kestrel's 60 MB is the backstop behind it: with a source limit above the cap (the admin refuses one; set here in
        // the database) exactly 60 MB passes and one byte more is Kestrel's 413.
        await MaxFileSizeAsync(100);
        using (var at = await SendAsync(HttpMethod.Put, $"/api/upload-batches/{batch}/files/2", new byte[UploadEndpoints.FileRequestLimit], "application/pdf"))
        {
            at.StatusCode.ShouldBe(HttpStatusCode.OK, await at.Content.ReadAsStringAsync(Ct));
        }

        using (var over = await SendAsync(HttpMethod.Put, $"/api/upload-batches/{batch}/files/3", new byte[UploadEndpoints.FileRequestLimit + 1], "application/pdf"))
        {
            await KestrelRefusedAsync(over);
        }

        // Any other route: the commit's JSON padded with spaces to 1 MB + 1 is 413, to exactly 1 MB it is answered.
        static byte[] Commit(int bytes) => System.Text.Encoding.UTF8.GetBytes("""{"files":[1]""" + new string(' ', bytes - 13) + "}");
        Commit(1024 * 1024).Length.ShouldBe(1024 * 1024);
        using (var over = await SendAsync(HttpMethod.Post, $"/api/upload-batches/{batch}/commit", Commit(1024 * 1024 + 1), "application/json"))
        {
            await KestrelRefusedAsync(over);
        }

        using var commit = await SendAsync(HttpMethod.Post, $"/api/upload-batches/{batch}/commit", Commit(1024 * 1024), "application/json");
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Kestrel's 413: problem details without the admin's own <c>code</c> (its 413s, such as <c>upload_too_large</c>, carry one).
    /// The pair of requests around it (the limit passes, one byte more is refused) shows which limit applied.
    /// </summary>
    private static async Task KestrelRefusedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, body);
        JsonDocument.Parse(body).RootElement.TryGetProperty("code", out _).ShouldBeFalse(body);
    }

    [Fact]
    public async Task A_manifest_left_by_a_failed_commit_with_other_entries_is_refused()
    {
        // A first commit wrote the manifest and failed after it (config lock, a conflict); a second with another list must not
        // go on, or the worker would sync the first list.
        var (sourceId, code) = await UploadSourceAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var create = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        var batch = (await Json(create)).GetProperty("batchId").GetGuid();
        (await PutFileAsync(admin, batch, 1, "a.pdf", "%PDF-1.7 a"u8.ToArray())).StatusCode.ShouldBe(HttpStatusCode.OK);

        var leftover = new Kalla.Pipelines.Jobs.UploadBatchManifest
        {
            BatchId = batch, SourceId = sourceId,
            Entries = [new() { Operation = Kalla.Pipelines.Jobs.UploadOperation.Delete, UploadId = Guid.CreateVersion7() }],
        };
        var blobs = factory.Services.GetRequiredService<IBlobStore>();
        await blobs.PutAsync(new BlobKey($"uploads/{sourceId:D}/batches/{batch:D}.json"),
            new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(leftover, Kalla.Pipelines.Json.ContractJson.Options)), null, Ct);

        using var commit = await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1 } }, Ct);
        commit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(commit)).GetProperty("code").GetString().ShouldBe("upload_batch_committed");
        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Jobs.AsNoTracking().AnyAsync(j => j.DedupKey == $"sync-upload:{batch}", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_files_list_pages_live_and_deleted_slots_and_quarantine_shows_metadata_only()
    {
        var (sourceId, code) = await UploadSourceAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            Upload Slot(string name, string state, DateTimeOffset? deleted) => new()
            {
                SourceId = sourceId, Classification = Classification.Internal, BatchId = Guid.CreateVersion7(), FileName = name, Bytes = 1234,
                Sha256 = RandomNumberGenerator.GetBytes(32), State = state, UploadedBy = "anna", UploadedAt = now, DeletedAt = deleted,
            };
            db.Uploads.AddRange(Slot("taxa-2026.pdf", UploadStates.Indexed, null), Slot("avgifter.docx", UploadStates.Refused, null), Slot("gammal.pdf", UploadStates.Deleted, now));
            db.QuarantinedFiles.Add(new QuarantinedFile
            {
                SourceId = sourceId, Classification = Classification.Internal, Sha256 = RandomNumberGenerator.GetBytes(32), Signature = "Win.Test.EICAR_HDB-1",
                Origin = QuarantineOrigins.Upload, Name = "eicar.pdf", DetectedAt = now, PurgeAfter = now.AddDays(30),
            });
            await db.SaveChangesAsync(Ct);
        }

        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, "9" + code[1..]);

        var first = await Json(await viewer.GetAsync($"/api/sources/{sourceId}/files?limit=1", Ct));
        first.GetProperty("items").GetArrayLength().ShouldBe(1);
        var next = first.GetProperty("next").GetString();
        var second = await Json(await viewer.GetAsync($"/api/sources/{sourceId}/files?limit=1&after={next}", Ct));
        var live = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(f => f.GetProperty("fileName").GetString()).ToList();
        live.ShouldBe(["taxa-2026.pdf", "avgifter.docx"], ignoreOrder: true);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);

        var deleted = await Json(await viewer.GetAsync($"/api/sources/{sourceId}/files?deleted=true", Ct));
        deleted.GetProperty("items").EnumerateArray().Single().GetProperty("fileName").GetString().ShouldBe("gammal.pdf");

        using var quarantineResponse = await viewer.GetAsync($"/api/sources/{sourceId}/quarantine", Ct);
        var quarantine = await Json(quarantineResponse);
        var row = quarantine.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("name").GetString().ShouldBe("eicar.pdf");
        row.GetProperty("signature").GetString().ShouldBe("Win.Test.EICAR_HDB-1");
        row.TryGetProperty("sha256", out _).ShouldBeFalse(); // metadata only (connectors §11)

        (await stranger.GetAsync($"/api/sources/{sourceId}/files", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/sources/{sourceId}/quarantine", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_folder_path_is_kept_as_its_file_name_and_unsafe_paths_are_refused()
    {
        // admin design §4.5, §5.5.7: X-File-Name is a relative path, stored (normalised) as the slot's file name.
        var (sourceId, code) = await UploadSourceAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var create = await admin.PostAsync($"/api/sources/{sourceId}/upload-batches", null, Ct);
        var batch = (await Json(create)).GetProperty("batchId").GetGuid();
        var pdf = "%PDF-1.7 policy"u8.ToArray();

        var longest = new string('a', 200) + "/" + new string('b', 95) + ".pdf"; // 300 characters
        var accepted = new[]
        {
            ("HR/Policys/semester.pdf", "HR/Policys/semester.pdf"),
            (@"\HR\Rutiner\Introduktion.pdf", "HR/Rutiner/Introduktion.pdf"),
            ("//Ekonomi/Budget 2026.pdf", "Ekonomi/Budget 2026.pdf"),
            (" HR / Arkiv /gammal.pdf ", "HR/Arkiv/gammal.pdf"),
            (longest, longest),
        };
        for (var i = 0; i < accepted.Length; i++)
        {
            using var put = await PutFileAsync(admin, batch, i + 1, accepted[i].Item1, pdf);
            put.StatusCode.ShouldBe(HttpStatusCode.OK, accepted[i].Item1);
            (await Json(put)).GetProperty("fileName").GetString().ShouldBe(accepted[i].Item2);
        }

        var refused = new[]
        {
            "../semester.pdf", "HR/../semester.pdf", "HR/./semester.pdf", "HR//semester.pdf", "HR/ /semester.pdf", "HR/", "", "   ",
            string.Join('/', Enumerable.Repeat("a", 10)) + "/b.pdf", // 11 segments
            new string('a', 200) + "/" + new string('b', 96) + ".pdf", // 301 characters
            new string('a', 256) + ".pdf", // a segment over 255
            "HR/semester\u0001.pdf", "HR\u0007/semester.pdf", "C:/semester.pdf", @"C:\HR\semester.pdf", "c:semester.pdf",
        };
        foreach (var name in refused)
        {
            using var put = await PutFileAsync(admin, batch, 10, name, pdf);
            put.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"'{name}'");
            (await Json(put)).GetProperty("errorCodes").GetProperty("X-File-Name")[0].GetProperty("code").GetString().ShouldBe("invalid_value", $"'{name}'");
        }

        // The extension pre-check reads the file's segment, not a folder's.
        (await PutFileAsync(admin, batch, 10, "Rapport.pdf/readme", pdf)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await PutFileAsync(admin, batch, 10, "Arkiv.v2/mejl.msg", pdf)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        using (var commit = await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1, 2 } }, Ct))
        {
            commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync(Ct));
        }

        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(host.Blobs, "uploads", sourceId.ToString("D"), "batches", $"{batch:D}.json"), Ct));
        manifest.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("fileName").GetString())
            .ShouldBe(["HR/Policys/semester.pdf", "HR/Rutiner/Introduktion.pdf"]);
    }

    /// <summary>
    /// Seven live slots and a deleted one, in the states the worker writes (connectors design §9.1, #533): an indexed slot
    /// names its document, a refused or malware one carries its reason.
    /// </summary>
    private async Task<(Guid Source, string Code, DateTimeOffset Now, Guid Document)> FilesAsync()
    {
        var (sourceId, code) = await UploadSourceAsync();
        var now = DateTimeOffset.UtcNow;
        await using var db = TestPostgres.Context(database.Connection(null));
        var bytes = 0;
        Upload Slot(string name, string state, TimeSpan ago, string? reason = null, Guid? documentId = null) => new()
        {
            SourceId = sourceId, Classification = Classification.Internal, BatchId = Guid.CreateVersion7(), FileName = name, Bytes = 1000 * ++bytes,
            Sha256 = RandomNumberGenerator.GetBytes(32), State = state, Reason = reason, DocumentId = documentId, UploadedBy = "anna", UploadedAt = now - ago,
        };

        var document = Guid.CreateVersion7();
        db.Uploads.AddRange(
            Slot("HR/Policys/semester.pdf", UploadStates.Indexed, TimeSpan.FromMinutes(5), documentId: document),
            Slot("HR/Rutiner/onboarding.docx", UploadStates.Received, TimeSpan.FromMinutes(1)),
            Slot("HRx-plan.pdf", UploadStates.Indexed, TimeSpan.FromMinutes(4), documentId: Guid.CreateVersion7()),
            Slot("HR-lista.pdf", UploadStates.Refused, TimeSpan.FromMinutes(3), "upload.type_mismatch"),
            Slot("Ekonomi/budget_2026.xlsx", UploadStates.Refused, TimeSpan.FromMinutes(2), "parser.encrypted", Guid.CreateVersion7()),
            Slot("Ekonomi/100%.pdf", UploadStates.Malware, TimeSpan.FromMinutes(6), "malware.found"),
            Slot("hr/annat.pdf", UploadStates.Clean, TimeSpan.FromSeconds(30)));
        var gone = Slot("gammal.pdf", UploadStates.Deleted, TimeSpan.FromMinutes(10)); // the 8 000 bytes the summary leaves out
        gone.DeletedAt = now;
        db.Uploads.Add(gone);
        await db.SaveChangesAsync(Ct);
        return (sourceId, code, now, document);
    }

    [Fact]
    public async Task The_files_list_searches_filters_by_state_and_pages_by_name_or_newest()
    {
        var (sourceId, code, _, document) = await FilesAsync();
        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        async Task<JsonElement> Get(string query)
        {
            using var response = await viewer.GetAsync($"/api/sources/{sourceId}/files?{query}", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{query}: {await response.Content.ReadAsStringAsync(Ct)}");
            return await Json(response);
        }

        async Task<List<JsonElement>> All(string query, int limit)
        {
            var items = new List<JsonElement>();
            string? after = null;
            do
            {
                var page = await Get($"{query}&limit={limit}" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}"));
                page.GetProperty("items").GetArrayLength().ShouldBeLessThanOrEqualTo(limit);
                items.AddRange(page.GetProperty("items").EnumerateArray());
                after = page.GetProperty("next").GetString();
            }
            while (after is not null);
            return items;
        }

        static List<string?> Names(IEnumerable<JsonElement> items) => [.. items.Select(f => f.GetProperty("fileName").GetString())];

        // Name order is path order: a folder's files are together (HR/ apart from HR-lista and HRx-plan), case aside.
        string[] byName = ["Ekonomi/100%.pdf", "Ekonomi/budget_2026.xlsx", "HR-lista.pdf", "hr/annat.pdf", "HR/Policys/semester.pdf", "HR/Rutiner/onboarding.docx", "HRx-plan.pdf"];
        Names(await All("sort=name", 3)).ShouldBe(byName);
        Names(await All("", 2)).ShouldBe(byName);
        Names(await All("sort=uploaded", 2))
            .ShouldBe(["hr/annat.pdf", "HR/Rutiner/onboarding.docx", "Ekonomi/budget_2026.xlsx", "HR-lista.pdf", "HRx-plan.pdf", "HR/Policys/semester.pdf", "Ekonomi/100%.pdf"]);

        // State, reason and document are the slot's own, as the worker wrote them.
        var states = (await All("", 50)).ToDictionary(f => f.GetProperty("fileName").GetString()!, f => (f.GetProperty("state").GetString(),
            f.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : f.GetProperty("reason").GetString()));
        states["HR/Policys/semester.pdf"].ShouldBe(("indexed", null));
        states["Ekonomi/budget_2026.xlsx"].ShouldBe(("refused", "parser.encrypted"));
        states["hr/annat.pdf"].ShouldBe(("clean", null));
        states["HR-lista.pdf"].ShouldBe(("refused", "upload.type_mismatch"));
        (await All("", 50)).Single(f => f.GetProperty("fileName").GetString() == "HR/Policys/semester.pdf").GetProperty("documentId").GetGuid().ShouldBe(document);

        Names(await All("state=processing", 50)).ShouldBe(["hr/annat.pdf", "HR/Rutiner/onboarding.docx"]);
        Names(await All("state=indexed&sort=uploaded", 1)).ShouldBe(["HRx-plan.pdf", "HR/Policys/semester.pdf"]);
        Names(await All("state=failed", 2)).ShouldBe(["Ekonomi/100%.pdf", "Ekonomi/budget_2026.xlsx", "HR-lista.pdf"]);

        // q: a case-insensitive substring of the path, with %, _ and \ taken literally.
        Names(await All("q=" + Uri.EscapeDataString("hr/"), 2)).ShouldBe(["hr/annat.pdf", "HR/Policys/semester.pdf", "HR/Rutiner/onboarding.docx"]);
        Names(await All("q=" + Uri.EscapeDataString("100%"), 50)).ShouldBe(["Ekonomi/100%.pdf"]);
        Names(await All("q=" + Uri.EscapeDataString("_"), 50)).ShouldBe(["Ekonomi/budget_2026.xlsx"]);
        Names(await All("q=" + Uri.EscapeDataString("\\"), 50)).ShouldBeEmpty();
        Names(await All("q=POLICY&state=indexed", 50)).ShouldBe(["HR/Policys/semester.pdf"]);

        // Bad values are field errors; a cursor is valid only for the sort it was made with.
        var byNameCursor = (await Get("limit=1")).GetProperty("next").GetString()!;
        foreach (var (query, field) in new[]
                 {
                     ("state=queued", "state"), ("sort=size", "sort"), ("after=not-a-cursor", "after"), ("after=" + Uri.EscapeDataString("e30"), "after"),
                     ($"sort=uploaded&after={byNameCursor}", "after"),
                 })
        {
            using var response = await viewer.GetAsync($"/api/sources/{sourceId}/files?{query}", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, query);
            (await Json(response)).GetProperty("errorCodes").GetProperty(field)[0].GetProperty("code").GetString().ShouldBe("invalid_value", query);
        }
    }

    [Fact]
    public async Task The_files_summary_counts_live_files_by_state()
    {
        var (sourceId, code, now, _) = await FilesAsync();
        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, "9" + code[1..]);

        using var response = await viewer.GetAsync($"/api/sources/{sourceId}/files/summary", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var summary = await Json(response);
        (summary.GetProperty("total").GetInt32(), summary.GetProperty("processing").GetInt32(), summary.GetProperty("indexed").GetInt32(),
            summary.GetProperty("failed").GetInt32(), summary.GetProperty("bytes").GetInt64()).ShouldBe((7, 2, 2, 3, 28_000L)); // the deleted 8 000 not counted
        summary.GetProperty("lastUploadAt").GetDateTimeOffset().ShouldBe(now - TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));

        var (empty, emptyCode) = await UploadSourceAsync();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, emptyCode);
        var none = await Json(await admin.GetAsync($"/api/sources/{empty}/files/summary", Ct));
        (none.GetProperty("total").GetInt32(), none.GetProperty("bytes").GetInt64(), none.GetProperty("lastUploadAt").ValueKind).ShouldBe((0, 0L, JsonValueKind.Null));
        (await stranger.GetAsync($"/api/sources/{sourceId}/files/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unscanned_slot_is_processing_and_each_file_says_when_it_was_scanned()
    {
        // B-47 (#535, admin design §4.5, §5.5.7): with the malware scan off a slot passes as unscanned and is indexed without ScannedAt.
        var (sourceId, code) = await UploadSourceAsync();
        var now = DateTimeOffset.UtcNow;
        Upload Slot(string name, string state, DateTimeOffset? scanned) => new()
        {
            SourceId = sourceId, Classification = Classification.Internal, BatchId = Guid.CreateVersion7(), FileName = name, DeclaredMediaType = "application/pdf",
            Bytes = 1000, Sha256 = RandomNumberGenerator.GetBytes(32), State = state, UploadedBy = "anna", UploadedAt = now, ScannedAt = scanned,
        };
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.Uploads.AddRange(Slot("pending.pdf", UploadStates.Unscanned, null), Slot("unscanned.pdf", UploadStates.Indexed, null),
                Slot("scanned.pdf", UploadStates.Indexed, now));
            await db.SaveChangesAsync(Ct);
        }

        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        var summary = await Json(await viewer.GetAsync($"/api/sources/{sourceId}/files/summary", Ct));
        (summary.GetProperty("total").GetInt32(), summary.GetProperty("processing").GetInt32(), summary.GetProperty("indexed").GetInt32()).ShouldBe((3, 1, 2));

        var processing = await Json(await viewer.GetAsync($"/api/sources/{sourceId}/files?state=processing", Ct));
        processing.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("fileName").GetString()).ShouldBe(["pending.pdf"]);
        var files = (await Json(await viewer.GetAsync($"/api/sources/{sourceId}/files", Ct))).GetProperty("items").EnumerateArray()
            .ToDictionary(f => f.GetProperty("fileName").GetString()!, f => f.GetProperty("scannedAt"));
        files["unscanned.pdf"].ValueKind.ShouldBe(JsonValueKind.Null);
        files["pending.pdf"].ValueKind.ShouldBe(JsonValueKind.Null);
        files["scanned.pdf"].GetDateTimeOffset().ShouldBe(now, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task A_platform_admin_restores_a_deleted_file_within_the_grace_through_a_batch_that_re_adds_the_slot()
    {
        // B-24, connectors §9.3: "restore adds the entry back and the document is revived" (the worker's add on a deleted slot,
        // UploadEngineTests in the worker tests). Here: the route's checks, the manifest it writes and the upload sync it starts.
        var (sourceId, code) = await UploadSourceAsync();
        var now = DateTimeOffset.UtcNow;
        Upload Slot(string name, DateTimeOffset? deleted) => new()
        {
            SourceId = sourceId, Classification = Classification.Internal, BatchId = Guid.CreateVersion7(), FileName = name, DeclaredMediaType = "application/pdf",
            Bytes = 1234, Sha256 = RandomNumberGenerator.GetBytes(32), State = deleted is null ? UploadStates.Indexed : UploadStates.Deleted, UploadedBy = "anna",
            UploadedAt = now.AddDays(-30), DeletedAt = deleted,
        };
        var (recent, old, live, gone) = (Slot("taxa-2025.pdf", now.AddDays(-3)), Slot("gammal.pdf", now.AddDays(-15)), Slot("taxa-2026.pdf", null), Slot("borta.pdf", now.AddDays(-1)));
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.Uploads.AddRange(recent, old, live, gone);
            await db.SaveChangesAsync(Ct);
        }

        var factory = host.Factory;
        foreach (var kept in new[] { recent, old, live }) // the upload blobs are tombstoned, not deleted, until the purge; "gone"'s is purged
        {
            var path = Path.Combine(host.Blobs, "uploads", sourceId.ToString("D"), kept.Id.ToString("D"), Convert.ToHexStringLower(kept.Sha256));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, "URB2"u8.ToArray(), Ct);
        }

        using var departmentAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var platformAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin, code);
        Task<HttpResponseMessage> Restore(HttpClient client, params Guid[] ids) =>
            client.PostAsJsonAsync($"/api/sources/{sourceId}/files/restore", new { uploadIds = ids }, Ct);

        (await Restore(departmentAdmin, recent.Id)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // "the platform team can bring them back"
        foreach (var (slot, why) in new[] { (old, "past the 14 days"), (live, "not deleted"), (gone, "bytes purged") })
        {
            using var refused = await Restore(platformAdmin, recent.Id, slot.Id);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, why);
            var problem = await Json(refused);
            (problem.GetProperty("code").GetString(), problem.GetProperty("params").GetProperty("name").GetString()).ShouldBe(("file_not_restorable", slot.FileName), why);
        }

        Guid batch;
        using (var restored = await Restore(platformAdmin, recent.Id))
        {
            restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync(Ct));
            var body = await Json(restored);
            body.GetProperty("entries").GetInt32().ShouldBe(1);
            batch = body.GetProperty("batchId").GetGuid();
        }

        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(host.Blobs, "uploads", sourceId.ToString("D"), "batches", $"{batch:D}.json"), Ct));
        ContractSchemaCheck(manifest.RootElement);
        var entry = manifest.RootElement.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
        (entry.GetProperty("operation").GetString(), entry.GetProperty("uploadId").GetGuid(), entry.GetProperty("fileName").GetString(),
                entry.GetProperty("bytes").GetInt64(), entry.GetProperty("sha256").GetString())
            .ShouldBe(("add", recent.Id, "taxa-2025.pdf", 1234L, Convert.ToHexStringLower(recent.Sha256)));

        await using var check = TestPostgres.Context(database.Connection(null));
        var job = await check.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"sync-upload:{batch}", Ct);
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            (payload.RootElement.GetProperty("trigger").GetString(), payload.RootElement.GetProperty("batchId").GetGuid()).ShouldBe(("upload", batch));
        }

        (await check.AuditLog.AsNoTracking().CountAsync(a => a.EntityType == "source" && a.EntityId == sourceId.ToString() && a.Action == "upload-restore", Ct)).ShouldBe(1);
    }
}
