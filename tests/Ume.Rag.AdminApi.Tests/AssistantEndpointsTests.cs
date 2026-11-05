extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Assistants (admin design §4.5, §4.6; P1a item 14d): create with a draft, draft saves with the V-rules as warnings, publish
/// with V34 and the rules as refusals and V8 at the FakeGateway, a second version superseding the first, audiences of kind
/// client with V7, and the level change re-checks.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class AssistantEndpointsTests(AdminDatabaseFixture database)
{
    private const string EuKey = "fake-eu-key-assistants-0123456789";
    private const string ExternalKey = "fake-external-key-assistants-0123";
    private const string Model = "ume/chat-eu";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_draft_save_by_ids_keeps_the_topics_of_links_it_already_has()
    {
        var kept = Guid.NewGuid();
        var added = Guid.NewGuid();
        var draft = new AssistantVersion { CollectionLinks = [new CollectionLink(kept, ["bygglov"]), CollectionLink.Whole(Guid.NewGuid())] };

        new DraftRequest { CollectionIds = [added, kept, added] }.ApplyTo(draft);

        draft.CollectionLinks.ShouldBe([CollectionLink.Whole(added), new CollectionLink(kept, ["bygglov"])]);
    }

    private static WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:Keys:Eu", EuKey);
        web.UseSetting("FakeGateway:Keys:External", ExternalKey);
    });

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Keys:Eu", EuKey);
        web.UseSetting("Gateway:Keys:External", ExternalKey);
        web.ConfigureTestServices(services =>
            services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
    });

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private sealed record Seed(string Code, Guid Department, Guid Collection, Guid InternalClient, Guid PublicClient);

    private async Task<Seed> SeedAsync()
    {
        var code = "A" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Asst " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "asst-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        if (!await db.GatewayModels.AnyAsync(m => m.Name == Model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = Model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        }

        var collection = new Collection
        {
            Slug = "asst-" + code.ToLowerInvariant(), Name = "Bygglov " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile.Value, PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var owui = new Client { Name = "Open WebUI " + code, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        var widget = new Client { Name = "Webb " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Public, UpdatedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, collection, owui, widget);
        await db.SaveChangesAsync(Ct);
        return new Seed(code, department.Id, collection.Id, owui.Id, widget.Id);
    }

    private static IEnumerable<string> Codes(JsonElement warnings) => warnings.EnumerateArray().Select(w => w.GetProperty("code").GetString()!);

    [Fact]
    public async Task An_assistant_is_drafted_published_offered_to_a_client_and_republished()
    {
        var seed = await SeedAsync();
        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "Y" + seed.Code);

        using var created = await admin.PostAsJsonAsync("/api/assistants", new { name = "Bygglovshjälpen " + seed.Code, departmentId = seed.Department, classification = "internal" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var assistant = await Json(created);
        var id = assistant.GetProperty("id").GetGuid();
        assistant.GetProperty("publishedVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        assistant.GetProperty("draft").GetProperty("number").GetInt32().ShouldBe(1);
        (await stranger.GetAsync($"/api/assistants/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The draft saves with problems as warnings; publish refuses with the first one.
        var draftVersion = assistant.GetProperty("draft").GetProperty("version").GetString();
        using (var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
               {
                   gatewayModel = Model, systemPrompt = "Svara kort.", collectionIds = new[] { seed.Collection }, querySteps = new { retrieval = new { topK = 99 } }, version = draftVersion,
               }, Ct))
        {
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            var body = await Json(draft);
            Codes(body.GetProperty("warnings")).ShouldBe(["assistant_contact_missing", "query_steps_invalid"], ignoreOrder: true);
            draftVersion = body.GetProperty("version").GetString();
        }

        var version = (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct)).GetProperty("version").GetString();
        using (var refused = await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version, draftVersion }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Json(refused);
            problem.GetProperty("code").GetString().ShouldBe("assistant_contact_missing");
            problem.GetProperty("problems").GetArrayLength().ShouldBe(2);
        }

        using (var fields = await admin.PutAsJsonAsync($"/api/assistants/{id}", new
               {
                   name = "Bygglovshjälpen " + seed.Code, classification = "internal", description = "Svarar om bygglov.", contactName = "Bygglov", contactEmail = "bygglov@umea.se", version,
               }, Ct))
        {
            fields.StatusCode.ShouldBe(HttpStatusCode.OK, await fields.Content.ReadAsStringAsync(Ct));
            version = (await Json(fields)).GetProperty("version").GetString();
        }

        using (var fixedDraft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
               {
                   gatewayModel = Model, systemPrompt = "Svara kort.", collectionIds = new[] { seed.Collection }, querySteps = new { retrieval = new { topK = 10 } }, version = draftVersion,
               }, Ct))
        {
            var body = await Json(fixedDraft);
            body.GetProperty("warnings").GetArrayLength().ShouldBe(0);
            draftVersion = body.GetProperty("version").GetString();
        }

        using (var published = await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version, draftVersion }, Ct))
        {
            published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
            var body = await Json(published);
            body.GetProperty("publishedVersion").GetInt32().ShouldBe(1);
            body.GetProperty("draft").ValueKind.ShouldBe(JsonValueKind.Null);
            version = body.GetProperty("version").GetString();
        }

        // Audiences: V7 refuses a client whose ceiling is below the level.
        using (var low = await admin.PutAsJsonAsync($"/api/assistants/{id}/audiences", new { clientIds = new[] { seed.PublicClient }, version }, Ct))
        {
            low.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(low)).GetProperty("code").GetString().ShouldBe("audience_above_ceiling");
        }

        using (var audience = await admin.PutAsJsonAsync($"/api/assistants/{id}/audiences", new { clientIds = new[] { seed.InternalClient }, version }, Ct))
        {
            audience.StatusCode.ShouldBe(HttpStatusCode.OK, await audience.Content.ReadAsStringAsync(Ct));
            var body = await Json(audience);
            body.GetProperty("audienceClientIds").EnumerateArray().Select(c => c.GetGuid()).ShouldBe([seed.InternalClient]);
            version = body.GetProperty("version").GetString();
        }

        // Raising the level to sensitive puts it above the audience client's ceiling (V7 re-checked).
        using (var raised = await admin.PutAsJsonAsync($"/api/assistants/{id}", new
               {
                   name = "Bygglovshjälpen " + seed.Code, classification = "sensitive", description = "Svarar om bygglov.", contactName = "Bygglov", contactEmail = "bygglov@umea.se", version,
               }, Ct))
        {
            raised.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(raised)).GetProperty("code").GetString().ShouldBe("audience_above_ceiling");
        }

        // A second version: the first edit after a publish copies the published version (no draft version yet).
        using (var second = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
               {
                   gatewayModel = Model, systemPrompt = "Svara kort och vänligt.", collectionIds = new[] { seed.Collection },
               }, Ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
            var body = await Json(second);
            body.GetProperty("number").GetInt32().ShouldBe(2);
            draftVersion = body.GetProperty("version").GetString();
        }

        version = (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct)).GetProperty("version").GetString();
        (await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version, draftVersion }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.AssistantVersions.AsNoTracking().Where(v => v.AssistantId == id).OrderBy(v => v.Number).Select(v => v.State).ToListAsync(Ct))
            .ShouldBe([AssistantVersionStates.Superseded, AssistantVersionStates.Published]);
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "assistant" && a.EntityId == id.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["create", "draft", "update", "draft", "publish", "audiences", "draft", "publish"]);
    }

    [Fact]
    public async Task General_knowledge_is_a_draft_field_and_a_public_assistant_keeps_it_off()
    {
        // P1b item 40: V17 accepts answer.generalKnowledge off/supplement/fallback/open; V36 keeps public assistants off
        // (refused on publish and on a level change, a warning on draft save).
        var seed = await SeedAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Collections.Where(c => c.Id == seed.Collection).ExecuteUpdateAsync(u => u.SetProperty(c => c.Classification, Classification.Public), Ct);
        }

        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        async Task<(Guid Id, string? Version, string? DraftVersion)> CreateAsync(string name, string level)
        {
            using var created = await admin.PostAsJsonAsync("/api/assistants", new
            {
                name = name + " " + seed.Code, departmentId = seed.Department, classification = level, description = "D", contactName = "K", contactEmail = "k@umea.se",
            }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            var body = await Json(created);
            return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetString(), body.GetProperty("draft").GetProperty("version").GetString());
        }

        async Task<(IReadOnlyList<string> Warnings, string? DraftVersion)> DraftAsync(Guid id, string generalKnowledge, string? draftVersion)
        {
            using var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
            {
                gatewayModel = Model, systemPrompt = "Svara kort.", collectionIds = new[] { seed.Collection }, querySteps = new { answer = new { generalKnowledge } },
                version = draftVersion,
            }, Ct);
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            var body = await Json(draft);
            body.GetProperty("querySteps").GetProperty("answer").GetProperty("generalKnowledge").GetString().ShouldBe(generalKnowledge);
            return ([.. Codes(body.GetProperty("warnings"))], body.GetProperty("version").GetString());
        }

        // An internal assistant: an unknown value is V17's warning; fallback is accepted and publishes.
        var (inner, innerVersion, innerDraft) = await CreateAsync("Intern", Classification.Internal);
        var (warnings, next) = await DraftAsync(inner, "sometimes", innerDraft);
        warnings.ShouldBe(["query_steps_invalid"]);
        (warnings, next) = await DraftAsync(inner, "fallback", next);
        warnings.ShouldBeEmpty();
        using (var published = await admin.PostAsJsonAsync($"/api/assistants/{inner}/publish", new { version = innerVersion, draftVersion = next }, Ct))
        {
            published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
            var body = await Json(published);
            innerVersion = body.GetProperty("version").GetString();
            body.GetProperty("generalKnowledge").GetString().ShouldBe("fallback"); // the badge's source (admin design §5.5.4)
        }

        var listed = (await admin.GetFromJsonAsync<JsonElement>("/api/assistants?pageSize=200", Ct)).GetProperty("items").EnumerateArray()
            .Single(a => a.GetProperty("id").GetGuid() == inner);
        listed.GetProperty("generalKnowledge").GetString().ShouldBe("fallback");

        // Lowering its level to public while the published version uses general knowledge is refused (V36, level change).
        using (var lowered = await admin.PutAsJsonAsync($"/api/assistants/{inner}", new
               {
                   name = "Intern " + seed.Code, classification = "public", description = "D", contactName = "K", contactEmail = "k@umea.se", version = innerVersion,
               }, Ct))
        {
            lowered.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(lowered)).GetProperty("code").GetString().ShouldBe("general_knowledge_not_allowed_for_level");
        }

        // A public assistant: supplement saves with the V36 warning, and publishing it is refused.
        var (open, openVersion, openDraft) = await CreateAsync("Publik", Classification.Public);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{open}", Ct)).GetProperty("generalKnowledge").ValueKind.ShouldBe(JsonValueKind.Null);
        (warnings, next) = await DraftAsync(open, "supplement", openDraft);
        warnings.ShouldBe(["general_knowledge_not_allowed_for_level"]);
        using (var refused = await admin.PostAsJsonAsync($"/api/assistants/{open}/publish", new { version = openVersion, draftVersion = next }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Json(refused);
            problem.GetProperty("code").GetString().ShouldBe("general_knowledge_not_allowed_for_level");
            problem.GetProperty("problems").GetArrayLength().ShouldBe(1);
        }

        // Off publishes.
        (warnings, next) = await DraftAsync(open, "off", next);
        warnings.ShouldBeEmpty();
        (await admin.PostAsJsonAsync($"/api/assistants/{open}/publish", new { version = openVersion, draftVersion = next }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Publish_refuses_a_model_the_gateway_does_not_serve_at_the_ceiling()
    {
        var seed = await SeedAsync();
        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            if (!await db.GatewayModels.AnyAsync(m => m.Name == "ume/chat-advanced", Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = "ume/chat-advanced", Residencies = ["External"], Source = GatewayModelSources.Catalog });
                await db.SaveChangesAsync(Ct);
            }
        }

        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Extern " + seed.Code, departmentId = seed.Department, classification = "internal", description = "D", contactName = "K", contactEmail = "k@umea.se",
        }, Ct);
        var assistant = await Json(created);
        var id = assistant.GetProperty("id").GetGuid();
        using var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
        {
            gatewayModel = "ume/chat-advanced", collectionIds = new[] { seed.Collection }, version = assistant.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct);
        var draftVersion = (await Json(draft)).GetProperty("version").GetString();

        // An internal assistant runs at ceiling Eu; the model is External only: V8's mirror check refuses before the gateway call.
        using var refused = await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version = assistant.GetProperty("version").GetString(), draftVersion }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(refused)).GetProperty("code").GetString().ShouldBe("model_residency");
    }

    [Fact]
    public async Task Versions_are_listed_restored_as_a_new_published_version_and_archive_and_retention_are_saved()
    {
        var seed = await SeedAsync();
        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Versioner " + seed.Code, departmentId = seed.Department, classification = "internal", description = "Svarar om bygglov.", contactName = "Bygglov",
            contactEmail = "bygglov@umea.se",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await Json(created)).GetProperty("id").GetGuid();
        var path = $"/api/assistants/{id}";

        async Task<string?> Version() => (await admin.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("version").GetString();

        async Task<string?> DraftAsync(string prompt)
        {
            var open = (await admin.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("draft");
            var version = open.ValueKind == JsonValueKind.Null ? null : open.GetProperty("version").GetString();
            using var draft = await admin.PutAsJsonAsync($"{path}/draft", new { gatewayModel = Model, systemPrompt = prompt, collectionIds = new[] { seed.Collection }, version }, Ct);
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            return (await Json(draft)).GetProperty("version").GetString();
        }

        async Task PublishAsync(string prompt)
        {
            var draftVersion = await DraftAsync(prompt);
            using var published = await admin.PostAsJsonAsync($"{path}/publish", new { version = await Version(), draftVersion }, Ct);
            published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
        }

        await PublishAsync("Första.");
        await PublishAsync("Andra.");
        var openDraft = await DraftAsync("Tredje, inte publicerad.");

        // Restore 1: a new version 4 with version 1's settings, published at once; 2 is superseded; the draft (3) is kept with an older base.
        (await viewer.PostAsJsonAsync($"{path}/versions/1/restore", new { version = await Version() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var restored = await editor.PostAsJsonAsync($"{path}/versions/1/restore", new { version = await Version() }, Ct))
        {
            restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync(Ct));
            var body = await Json(restored);
            body.GetProperty("publishedVersion").GetInt32().ShouldBe(4);
            (body.GetProperty("draft").GetProperty("number").GetInt32(), body.GetProperty("draft").GetProperty("baseChanged").GetBoolean()).ShouldBe((3, true));
        }

        var versions = (await admin.GetFromJsonAsync<JsonElement[]>($"{path}/versions", Ct))!;
        versions.Select(v => (v.GetProperty("number").GetInt32(), v.GetProperty("state").GetString())).ShouldBe(
            [(4, "published"), (3, "draft"), (2, "superseded"), (1, "superseded")]);
        versions[0].GetProperty("restoredFrom").GetInt32().ShouldBe(1);
        var four = await admin.GetFromJsonAsync<JsonElement>($"{path}/versions/4", Ct);
        (four.GetProperty("systemPrompt").GetString(), four.GetProperty("restoredFrom").GetInt32()).ShouldBe(("Första.", 1));

        // The kept draft asks for confirmation; the draft and the live version are not restorable; an unknown number is 404.
        using (var stale = await admin.PostAsJsonAsync($"{path}/publish", new { version = await Version(), draftVersion = openDraft }, Ct))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(stale)).GetProperty("code").GetString().ShouldBe("draft_base_changed");
        }

        foreach (var number in new[] { "3", "4" })
        {
            using var refused = await admin.PostAsJsonAsync($"{path}/versions/{number}/restore", new { version = await Version() }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, number);
            (await Json(refused)).GetProperty("code").GetString().ShouldBe("version_not_restorable");
        }

        (await admin.PostAsJsonAsync($"{path}/versions/x/restore", new { version = await Version() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"{path}/versions/9", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Archive and unarchive keep the published version.
        using (var archived = await editor.PostAsJsonAsync($"{path}/archive", new { version = await Version() }, Ct))
        {
            var body = await Json(archived);
            (body.GetProperty("state").GetString(), body.GetProperty("publishedVersion").GetInt32()).ShouldBe(("archived", 4));
        }

        using (var active = await editor.PostAsJsonAsync($"{path}/unarchive", new { version = await Version() }, Ct))
        {
            var body = await Json(active);
            (body.GetProperty("state").GetString(), body.GetProperty("publishedVersion").GetInt32()).ShouldBe(("active", 4));
        }

        // Retention (V10): within the internal level's policy (history none, at most 90 days), by Manage only.
        (await editor.PutAsJsonAsync($"{path}/retention", new { historyMode = "none", retentionDays = 30, version = await Version() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreach (var (mode, days) in new[] { ("none", 120), ("stored", 30) })
        {
            using var outside = await admin.PutAsJsonAsync($"{path}/retention", new { historyMode = mode, retentionDays = days, version = await Version() }, Ct);
            outside.StatusCode.ShouldBe(HttpStatusCode.Conflict, mode);
            (await Json(outside)).GetProperty("code").GetString().ShouldBe("retention_outside_policy");
        }

        using (var retention = await admin.PutAsJsonAsync($"{path}/retention", new { historyMode = "none", retentionDays = 30, version = await Version() }, Ct))
        {
            retention.StatusCode.ShouldBe(HttpStatusCode.OK, await retention.Content.ReadAsStringAsync(Ct));
            (await Json(retention)).GetProperty("retentionDays").GetInt32().ShouldBe(30);
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "assistant" && a.EntityId == id.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["create", "draft", "publish", "draft", "publish", "draft", "restore", "archive", "unarchive", "retention"]);
    }
}
