using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Item 14a (admin design §4.5, V29, V30): platform settings by section, data protection per level, the template catalogue,
/// and OpenAPI/Scalar for platform admins. The database is shared by the collection, so each test reads the current
/// version before it writes.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class PlatformSettingsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<string?> Code(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return json.TryGetProperty("code", out var code) ? code.GetString()
            : json.TryGetProperty("errorCodes", out var fields) ? fields.EnumerateObject().First().Value[0].GetProperty("code").GetString() : null;
    }

    [Fact]
    public async Task A_section_saves_with_its_version_is_audited_and_a_stale_version_is_refused()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var current = await Json(await admin.GetAsync("/api/settings/deletion-guard", Ct));
        var version = current.GetProperty("version").GetString();

        var saved = await Json(await admin.PutAsJsonAsync("/api/settings/deletion-guard", new { percent = 25, minCount = 4, maxCount = 400, version }, Ct));
        (saved.GetProperty("percent").GetInt32(), saved.GetProperty("maxCount").GetInt32()).ShouldBe((25, 400));
        saved.GetProperty("version").GetString().ShouldNotBe(version);

        // Another section with the old version: the singleton's version moved, so the second admin reloads.
        using var stale = await admin.PutAsJsonAsync("/api/settings/retention", new { auditRetentionDays = 365, blobPurgeDelayDays = 35, quarantineRetentionDays = 30, version }, Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(stale)).ShouldBe(AdminErrorCodes.ConcurrentChange.Code);

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var audit = await db.AuditLog.Where(a => a.EntityType == AuditEntityTypes.Settings && a.EntityId == "deletion-guard").OrderByDescending(a => a.Id).FirstAsync(Ct);
        audit.Action.ShouldBe("update");
        audit.Details!.ShouldContain("\"percent\": 25");
        audit.Details!.ShouldNotContain("version"); // the token is no audit content
    }

    [Fact]
    public async Task Field_rules_refuse_out_of_range_values_and_everything_proxies()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var version = (await Json(await admin.GetAsync("/api/settings/network", Ct))).GetProperty("version").GetString();

        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/settings/deletion-guard", new { percent = 0, minCount = 5, maxCount = 500, version }),
            ("/api/settings/deletion-guard", new { percent = 20, minCount = 50, maxCount = 5, version }),
            ("/api/settings/retention", new { auditRetentionDays = 30, blobPurgeDelayDays = 35, quarantineRetentionDays = 30, version }),
            ("/api/settings/network", new { trustedProxies = new[] { "0.0.0.0/0" }, version }),
            ("/api/settings/network", new { trustedProxies = new[] { "not-an-address" }, version }),
            ("/api/settings/retrieval", new { value = new { candidates = 5 }, version }),
        })
        {
            using var response = await admin.PutAsJsonAsync(path, body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{path} {JsonSerializer.Serialize(body)}");
        }

        var saved = await Json(await admin.PutAsJsonAsync("/api/settings/network", new { trustedProxies = new[] { " 10.20.0.0/16 ", "192.0.2.10/32" }, version }, Ct));
        saved.GetProperty("trustedProxies").EnumerateArray().Select(p => p.GetString()).ShouldBe(["10.20.0.0/16", "192.0.2.10"]);
    }

    [Fact]
    public async Task V32_refuses_a_model_setting_that_is_not_a_chat_model_within_the_platform_ceiling()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string chat = "v32-chat-" + suffix, embedding = "v32-embed-" + suffix, external = "v32-external-" + suffix, rerank = "v32-rerank-" + suffix;
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            db.GatewayModels.AddRange(
                new GatewayModel { Name = chat, Residencies = ["Eu"], Source = GatewayModelSources.Catalog },
                new GatewayModel { Name = embedding, ModelKind = GatewayModelModelKinds.Embedding, Residencies = ["OnPrem"], Source = GatewayModelSources.Catalog },
                new GatewayModel { Name = external, Residencies = ["External"], Source = GatewayModelSources.Catalog },
                new GatewayModel { Name = rerank, ModelKind = GatewayModelModelKinds.Rerank, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
            await db.SaveChangesAsync(Ct);
        }

        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var evaluation = await Json(await admin.GetAsync("/api/settings/evaluation", Ct));
        var chatSection = await Json(await admin.GetAsync("/api/settings/chat", Ct));
        var retrievalSection = await Json(await admin.GetAsync("/api/settings/retrieval", Ct));
        var version = evaluation.GetProperty("version").GetString();

        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/settings/evaluation", new { evaluationModel = embedding, evaluationMaxQuestions = 100, version }), // an embedding model (#363 point 1)
            ("/api/settings/evaluation", new { evaluationModel = chat, evaluationJudgeModel = external, evaluationMaxQuestions = 100, version }), // above Eu
            ("/api/settings/evaluation", new { evaluationModel = "no-such-model-" + suffix, evaluationMaxQuestions = 100, version }),
            ("/api/settings/chat", new { value = new { condenseModel = embedding }, version }),
            ("/api/settings/retrieval", new { value = new { rerankModel = embedding }, version }), // B-12: the rerank model is V32's too
            ("/api/settings/retrieval", new { value = new { rerankModel = external }, version }),
            ("/api/settings/retrieval", new { value = new { rerankModel = rerank }, version }), // the chat reranker needs a chat model
            ("/api/settings/retrieval", new { value = new { gatewayRerankModel = chat }, version }), // B-16: /v1/rerank needs a rerank model
        })
        {
            using var refused = await admin.PutAsJsonAsync(path, body, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"{path} {JsonSerializer.Serialize(body)}");
            (await Code(refused)).ShouldBe(AdminErrorCodes.SettingsModelResidency.Code);
        }

        // A chat model within Eu saves; then the shared database gets its settings back.
        var saved = await Json(await admin.PutAsJsonAsync("/api/settings/evaluation", new { evaluationModel = chat, evaluationJudgeModel = chat, evaluationMaxQuestions = 100, version }, Ct));
        saved.GetProperty("evaluationModel").GetString().ShouldBe(chat);
        saved = await Json(await admin.PutAsJsonAsync("/api/settings/chat", new { value = new { condenseModel = chat }, version = saved.GetProperty("version").GetString() }, Ct));
        saved = await Json(await admin.PutAsJsonAsync("/api/settings/retrieval", new { value = new { rerankModel = chat }, version = saved.GetProperty("version").GetString() }, Ct));
        saved.GetProperty("value").GetProperty("rerankModel").GetString().ShouldBe(chat);
        saved = await Json(await admin.PutAsJsonAsync("/api/settings/retrieval", new { value = new { gatewayRerankModel = rerank }, version = saved.GetProperty("version").GetString() }, Ct));
        saved.GetProperty("value").GetProperty("gatewayRerankModel").GetString().ShouldBe(rerank);
        saved = await Json(await admin.PutAsJsonAsync("/api/settings/retrieval", new { value = retrievalSection.GetProperty("value"), version = saved.GetProperty("version").GetString() }, Ct));
        saved = await Json(await admin.PutAsJsonAsync("/api/settings/evaluation", new
        {
            evaluationModel = evaluation.GetProperty("evaluationModel").GetString(), evaluationJudgeModel = evaluation.GetProperty("evaluationJudgeModel").GetString(),
            evaluationMaxQuestions = evaluation.GetProperty("evaluationMaxQuestions").GetInt32(), version = saved.GetProperty("version").GetString(),
        }, Ct));
        await Json(await admin.PutAsJsonAsync("/api/settings/chat", new { value = chatSection.GetProperty("value"), version = saved.GetProperty("version").GetString() }, Ct));
    }

    [Fact]
    public async Task V37_refuses_a_host_politeness_profile_outside_its_ranges_or_ceilings_and_saves_a_valid_one_audited()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = await Json(await admin.GetAsync("/api/settings/crawl", Ct));
        var version = before.GetProperty("version").GetString();

        async Task RefusedAsync(object hostOverrides, string host, string field)
        {
            using var response = await admin.PutAsJsonAsync("/api/settings/crawl", new { value = new { allowedPorts = new[] { 8443 }, hostOverrides }, version }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{host} {field}");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.CrawlPolitenessInvalid.Code);
            problem.GetProperty("params").GetProperty("host").GetString().ShouldBe(host);
            problem.GetProperty("params").GetProperty("field").GetString().ShouldBe(field);
        }

        foreach (var (profile, field) in new (object, string)[]
        {
            // The deployment's ceilings (Crawl:MaxHostRate 50, Crawl:MaxHostConnections 16 by default).
            (new { requestsPerSecond = 51 }, "requestsPerSecond"),
            (new { requestsPerSecond = 0.05 }, "requestsPerSecond"),
            (new { maxConnections = 17 }, "maxConnections"),
            (new { maxConnections = 2.5 }, "maxConnections"),
            // robots.txt is ignored only with a reason.
            (new { robots = new { mode = "ignore" } }, "robots.reason"),
            (new { robots = new { mode = "ignore", reason = "  " } }, "robots.reason"),
            (new { robots = new { mode = "skip", reason = "x" } }, "robots.mode"),
            (new { userAgent = "UmeåBot/1.0" }, "userAgent"),
            (new { userAgent = new string('x', 257) }, "userAgent"),
            (new { userAgent = "" }, "userAgent"),
            (new { preset = "turbo" }, "preset"),
            (new { crawlDelay = "never" }, "crawlDelay"),
            (new { crawlWindow = "day" }, "crawlWindow"),
            (new { backoff = new { maxRetryAfterSeconds = 7200 } }, "backoff.maxRetryAfterSeconds"),
            (new { backoff = new { maxFactor = 32 } }, "backoff.maxFactor"),
            (new { circuitBreaker = new { threshold = 0.1 } }, "circuitBreaker.threshold"),
            (new { circuitBreaker = new { pauseMinutes = 121 } }, "circuitBreaker.pauseMinutes"),
            (new { circuitBreaker = "on" }, "circuitBreaker"),
            (new { renderPerHost = 3 }, "renderPerHost"),
            // A typo is no silent default.
            (new { requestPerSecond = 5 }, "requestPerSecond"),
            (new { backoff = new { maxWait = 5 } }, "backoff.maxWait"),
        })
        {
            await RefusedAsync(new Dictionary<string, object> { ["www.example.org"] = profile }, "www.example.org", field);
        }

        foreach (var host in new[] { "www.example.org:8443", "WWW.example.org", "https://www.example.org", "*.example.org", "www.example.org/", "rådhus.example.org" })
        {
            await RefusedAsync(new Dictionary<string, object> { [host] = new { requestsPerSecond = 2 } }, host, "host");
        }

        await RefusedAsync(new[] { "www.example.org" }, "", "hostOverrides");

        var value = JsonNode.Parse("""
            {
              "municipalHosts": ["*.umea.se"],
              "hostOverrides": {
                "www.umea.se": { "preset": "fast", "requestsPerSecond": 50, "maxConnections": 16, "circuitBreaker": "off",
                                 "robots": { "mode": "ignore", "reason": "own site, web team consent 2026-11-10" },
                                 "userAgent": "Mozilla/5.0 (X11; Linux x86_64)", "renderPerHost": 2 },
                "xn--rdhus-mra.example.org": { "preset": "custom", "crawlDelay": "ignore", "adaptToLatency": false, "crawlWindow": "any",
                                               "backoff": { "maxRetryAfterSeconds": 600, "maxFactor": 4 }, "circuitBreaker": { "threshold": 0.8, "pauseMinutes": 10 },
                                               "robots": { "mode": "respect" } }
              }
            }
            """)!.AsObject();
        try
        {
            var saved = await Json(await admin.PutAsJsonAsync("/api/settings/crawl", new { value, version }, Ct));
            JsonNode.DeepEquals(JsonNode.Parse(saved.GetProperty("value").GetRawText()), value).ShouldBeTrue();

            await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
            var audit = await db.AuditLog.Where(a => a.EntityType == AuditEntityTypes.Settings && a.EntityId == "crawl").OrderByDescending(a => a.Id).FirstAsync(Ct);
            audit.Action.ShouldBe("update");
            audit.Details!.ShouldContain("own site, web team consent 2026-11-10");
        }
        finally
        {
            var current = await Json(await admin.GetAsync("/api/settings/crawl", Ct));
            using var restored = await admin.PutAsJsonAsync("/api/settings/crawl",
                new { value = JsonNode.Parse(before.GetProperty("value").GetRawText()), version = current.GetProperty("version").GetString() }, Ct);
            restored.IsSuccessStatusCode.ShouldBeTrue(await restored.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Settings_are_platform_admin_only()
    {
        await using var factory = database.HeaderFactory();
        using var department = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin);
        (await department.GetAsync("/api/settings/network", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Data_protection_lists_every_level_and_enforces_v29()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer);

        var levels = await Json(await viewer.GetAsync("/api/data-protection", Ct));
        levels.EnumerateArray().Select(l => l.GetProperty("classification").GetString()).ShouldBe(["public", "internal", "sensitive"]);
        var internalPolicy = await Json(await viewer.GetAsync("/api/data-protection/internal", Ct));
        internalPolicy.GetProperty("blobMasterKeyName").GetString().ShouldBe("rag-blob-internal");
        var version = internalPolicy.GetProperty("version").GetString();

        object Body(bool encryption = true, string? key = "rag-blob-internal", int def = 60, int max = 90, string[]? modes = null) => new
        {
            allowedHistoryModes = modes ?? ["none"], defaultHistoryMode = "none", retentionDefaultDays = def, retentionMaxDays = max,
            encryptionRequired = encryption, reviewSecondApprover = false, clientsWithoutHistoryOnly = false, blobMasterKeyName = key, version,
        };

        foreach (var refused in new[] { Body(encryption: false), Body(key: null), Body(def: 80, max: 60), Body(modes: ["none", "stored"]) })
        {
            using var response = await admin.PutAsJsonAsync("/api/data-protection/internal", refused, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Code(response)).ShouldBe(AdminErrorCodes.PolicyRule.Code);
        }

        using (var publicKey = await admin.PutAsJsonAsync("/api/data-protection/public", Body(encryption: false), Ct))
        {
            (await Code(publicKey)).ShouldBe(AdminErrorCodes.PolicyRule.Code); // public has no blob key
        }

        var saved = await Json(await admin.PutAsJsonAsync("/api/data-protection/internal", Body(), Ct));
        saved.GetProperty("retentionDefaultDays").GetInt32().ShouldBe(60);
        (await viewer.PutAsJsonAsync("/api/data-protection/internal", Body(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.GetAsync("/api/data-protection/secret", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Templates_are_listed_with_their_parameter_schema()
    {
        await using var factory = database.HeaderFactory();
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer);

        var list = await Json(await viewer.GetAsync("/api/templates", Ct));
        var first = list.EnumerateArray().First();
        var id = first.GetProperty("id").GetString();
        var version = first.GetProperty("version").GetInt32();

        var detail = await Json(await viewer.GetAsync($"/api/templates/{id}/versions/{version}", Ct));
        detail.GetProperty("paramsSchema").ValueKind.ShouldBe(JsonValueKind.Object);
        detail.GetProperty("stages").GetArrayLength().ShouldBeGreaterThan(0);
        (await viewer.GetAsync($"/api/templates/{id}/versions/999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await viewer.GetAsync($"/api/templates/{id}/versions/x", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OpenApi_and_scalar_are_for_platform_admins()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var department = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin);

        var document = await Json(await admin.GetAsync("/openapi/v1.json", Ct));
        document.GetProperty("paths").TryGetProperty("/api/settings/network", out _).ShouldBeTrue();
        using (var scalar = await admin.GetAsync("/scalar/v1", Ct))
        {
            scalar.IsSuccessStatusCode.ShouldBeTrue();
        }

        (await department.GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
