extern alias fakegateway;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// P1a item 14's done-when (P1 README §3): "the whole pilot set-up works through Scalar alone; every write audited and bumps the
/// config version". A platform admin sets the pilot up with the admin API's HTTP endpoints only (what Scalar calls), in the
/// order of item 20: data protection, the Open WebUI client and its key, the pilot collection, the umea.se site
/// profile, a crawl, a URL-list and an upload source (one batch), sync now, a gold-set question, and one assistant published
/// to the Open WebUI client. The department and the model mirror are the syncs' (seeded as they write them; the syncs have
/// their own tests).
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class PilotSetupTests(AdminDatabaseFixture database) : IDisposable
{
    private const string AdminKey = "fake-eu-key-pilot-setup-0123456789"; // rag-admin\'s credential key
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("ume-rag-pilot-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<JsonElement> Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<long> ConfigVersionAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        return await db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(Ct);
    }

    [Fact]
    public async Task The_pilot_is_set_up_through_the_admin_api_alone_with_every_write_audited()
    {
        // The department and the model mirror as the org and model syncs write them (they have their own tests; enabling the
        // syncs here would also run them in the background against the shared database), and the platform pipelines as the
        // migrator seeds them.
        var code = "P" + Random.Shared.Next(100_000, 999_999);
        var department = Guid.CreateVersion7();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.Departments.Add(new Department { Id = department, Name = "Pilotförvaltningen " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow });
            foreach (var template in TemplateCatalog.BuiltIn.All.Select(t => t.Template.Name).Distinct())
            {
                if (!await db.Pipelines.AnyAsync(p => p.DepartmentId == null && p.TemplateId == template && p.PublishedVersionId != null, Ct))
                {
                    var pipeline = new Pipeline { Name = template, TemplateId = template, UpdatedAt = DateTimeOffset.UtcNow };
                    var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
                    db.Pipelines.Add(pipeline);
                    db.PipelineVersions.Add(version);
                    await db.SaveChangesAsync(Ct);
                    pipeline.PublishedVersionId = version.Id;
                }
            }

            if (!await db.GatewayModels.AnyAsync(m => m.Name == "ume/chat-eu", Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = "ume/chat-eu", Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
            }

            if (!await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, Ct))
            {
                db.EmbeddingProfiles.Add(new EmbeddingProfile
                {
                    Name = "pilot-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                    CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        var keys = Directory.CreateDirectory(Path.Combine(_root, "keys")).FullName;
        File.WriteAllText(Path.Combine(keys, "rag-blob-internal"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        });
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", AdminKey);
            web.UseSetting("Security:KeyPepper", "pilot-setup-test-pepper-0123456789abcdef");
            web.UseSetting("BlobStore:Root", Path.Combine(_root, "blobs"));
            web.UseSetting("KeyProvider:Directory", keys);
            web.ConfigureTestServices(services =>
                services.UseGatewayCredentialCeiling().AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
        });
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin, code);
        var startVersion = await ConfigVersionAsync();

        // 1. The public data-protection row (history none).
        var policy = await Ok(await admin.GetAsync("/api/data-protection/public", Ct));
        var policyBody = JsonNode.Parse(policy.GetRawText())!.AsObject();
        await Ok(await admin.PutAsJsonAsync("/api/data-protection/public", policyBody, Ct));

        // 2. The Open WebUI client and its key (secret shown once).
        var client = await Ok(await admin.PostAsJsonAsync("/api/clients", new { name = "Open WebUI " + code, kind = ClientKinds.OpenWebUi, classificationCeiling = "internal" }, Ct),
            HttpStatusCode.Created);
        var clientId = client.GetProperty("client").GetProperty("id").GetGuid();
        var key = await Ok(await admin.PostAsJsonAsync("/api/keys", new { clientId, name = "Open WebUI" }, Ct), HttpStatusCode.Created);
        key.GetProperty("secret").GetString()!.ShouldStartWith("ume-rag-sk-");

        // 3. The pilot collection, published.
        var collection = await Ok(await admin.PostAsJsonAsync("/api/collections", new { name = "Umeå kommun " + code, departmentId = department, classification = "public" }, Ct),
            HttpStatusCode.Created);
        var collectionId = collection.GetProperty("id").GetGuid();
        await Ok(await admin.PostAsJsonAsync($"/api/collections/{collectionId}/publish", new { version = collection.GetProperty("version").GetString() }, Ct));

        // 4. Sources: the crawl, the pilot URL list, and one upload.
        var host = $"www.{code.ToLowerInvariant()}.umea.se";
        var crawl = await Ok(await admin.PostAsJsonAsync($"/api/collections/{collectionId}/sources",
            new { type = "crawl", name = "umea.se", classification = "public", settings = new { startUrls = new[] { $"https://{host}/" } } }, Ct), HttpStatusCode.Created);
        await Ok(await admin.PostAsJsonAsync($"/api/collections/{collectionId}/sources",
            new { type = "url-list", name = "Pilotens sidor", classification = "public", settings = new { urls = new[] { $"https://{host}/bygglov", $"https://{host}/taxor" } } }, Ct),
            HttpStatusCode.Created);
        var upload = await Ok(await admin.PostAsJsonAsync($"/api/collections/{collectionId}/sources",
            new { type = "upload", name = "Dokument", classification = "public", settings = new { } }, Ct), HttpStatusCode.Created);
        var batch = (await Ok(await admin.PostAsync($"/api/sources/{upload.GetProperty("id").GetGuid()}/upload-batches", null, Ct), HttpStatusCode.Created))
            .GetProperty("batchId").GetGuid();
        using (var file = new HttpRequestMessage(HttpMethod.Put, $"/api/upload-batches/{batch}/files/1") { Content = new ByteArrayContent("%PDF-1.7 taxa"u8.ToArray()) })
        {
            file.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            file.Headers.Add("X-File-Name", Uri.EscapeDataString("Taxa bygglov 2026.pdf"));
            await Ok(await admin.SendAsync(file, Ct));
        }

        await Ok(await admin.PostAsJsonAsync($"/api/upload-batches/{batch}/commit", new { files = new[] { 1 } }, Ct));
        var crawlId = crawl.GetProperty("id").GetGuid();
        await Ok(await admin.PostAsync($"/api/sources/{crawlId}/sync", null, Ct));

        // 5. The umea.se site profile (manual, the hand-checked Sitevision profile).
        var profile = JsonNode.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("contracts", "site-profiles", "v1", "www.umea.se.profile.json"))));
        await Ok(await admin.PostAsJsonAsync("/api/site-profiles", new { host, level = "public", presetId = "sitevision", profile }, Ct), HttpStatusCode.Accepted);

        // 6. The gold set: a human question (item 21 enters the rest the same way).
        await Ok(await admin.PostAsJsonAsync($"/api/collections/{collectionId}/evaluation/questions", new { question = "Hur ansöker jag om bygglov?", expectedDocumentIds = Array.Empty<Guid>() }, Ct),
            HttpStatusCode.Accepted);

        // 7. One assistant with the pilot collection, published to the Open WebUI client.
        var assistant = await Ok(await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Bygglovshjälpen " + code, departmentId = department, classification = "internal", description = "Svarar på frågor om bygglov från umea.se.",
            contactName = "Bygglov", contactEmail = "bygglov@umea.se",
        }, Ct), HttpStatusCode.Created);
        var assistantId = assistant.GetProperty("id").GetGuid();
        var draft = await Ok(await admin.PutAsJsonAsync($"/api/assistants/{assistantId}/draft", new
        {
            gatewayModel = "ume/chat-eu", systemPrompt = "Svara kort och hänvisa till källorna.", collectionLinks = new[] { new { collectionId, tags = Array.Empty<string>() } },
            version = assistant.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct));
        // The pilot runs on the platform's credential key until a gateway key is chosen (V39's warning, B-36).
        draft.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ShouldBe([AdminErrorCodes.GatewayKeyRequired.Code]);
        var published = await Ok(await admin.PostAsJsonAsync($"/api/assistants/{assistantId}/publish", new
        {
            version = assistant.GetProperty("version").GetString(), draftVersion = draft.GetProperty("version").GetString(),
        }, Ct));
        var offered = await Ok(await admin.PutAsJsonAsync($"/api/assistants/{assistantId}/audiences", new
        {
            clientIds = new[] { clientId }, version = published.GetProperty("version").GetString(),
        }, Ct));
        offered.GetProperty("publishedVersion").GetInt32().ShouldBe(1);
        offered.GetProperty("audienceClientIds").EnumerateArray().Select(c => c.GetGuid()).ShouldBe([clientId]);

        // Every write is audited, the config version moved, and the worker's jobs are queued.
        await using var check = TestPostgres.Context(database.Connection(null));
        var actions = await check.AuditLog.AsNoTracking().Where(a => a.Actor == "test-user" && a.ConfigVersion > startVersion)
            .OrderBy(a => a.Id).Select(a => a.EntityType + ":" + a.Action).ToListAsync(Ct);
        actions.ShouldBe(
        [
            "dataprotection:update", "client:create", "key:create", "collection:create", "collection:publish",
            // The crawl is the collection's first website source: it turns on age decay for PDFs (P1b item 3).
            "source:create", "collection:retrieval-settings", "source:create", "source:create", "source:upload-commit", "source:sync",
            "siteprofile:site-profile.create",
            "evaluationquestion:evaluation-question.create",
            "assistant:create", "assistant:draft", "assistant:publish", "assistant:audiences",
        ], ignoreOrder: false);
        (await ConfigVersionAsync()).ShouldBeGreaterThan(startVersion);
        var jobs = await check.Jobs.AsNoTracking().Where(j => j.DepartmentId == department).Select(j => j.DedupKey ?? j.Class).ToListAsync(Ct);
        jobs.ShouldContain($"sync:{crawlId}");
        jobs.ShouldContain($"sync-upload:{batch}");
        jobs.ShouldContain(j => j.StartsWith("site-profile:public:", StringComparison.Ordinal));
        jobs.ShouldContain(JobClasses.Command); // the gold-set question
    }
}
