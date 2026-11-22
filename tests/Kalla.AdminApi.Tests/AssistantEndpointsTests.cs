extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Assistants (admin design §4.5, §4.6; P1a item 14d): create with a draft, draft saves with the V-rules as warnings, publish
/// with V34 and the rules as refusals and V8 at the FakeGateway, a second version superseding the first, audiences of kind
/// client with V7, and the level change re-checks.
/// </summary>
[Collection(AdminDatabaseGroup6.Name)]
public sealed class AssistantEndpointsTests(AdminDatabaseFixture database, AssistantEndpointsTests.Host host) : IClassFixture<AssistantEndpointsTests.Host>
{
    private const string AdminKey = "fake-eu-key-assistants-0123456789"; // kalla-admin\'s credential key
    private const string Model = "fors/chat-eu";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_draft_save_takes_one_link_per_collection_with_its_topics_trimmed_and_de_duplicated()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var draft = new AssistantVersion { CollectionLinks = [new CollectionLink(first, ["gammal"])] };

        new DraftRequest
        {
            CollectionLinks =
            [
                new CollectionLinkRequest { CollectionId = second, Tags = [" bygglov ", "bygglov", "", "altan"] },
                new CollectionLinkRequest { CollectionId = first },
                new CollectionLinkRequest { CollectionId = second, Tags = ["ignored"] },
            ],
        }.ApplyTo(draft);

        draft.CollectionLinks.ShouldBe([new CollectionLink(second, ["bygglov", "altan"]), CollectionLink.Whole(first)]);
    }

    private static WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
    });

    /// <summary>The class's admin host with kalla-admin's credential key, on a FakeGateway of its own.</summary>
    public sealed class Host(AdminDatabaseFixture database) : AdminHost(database)
    {
        private readonly WebApplicationFactory<FakeGatewayProgram> _gateway = Gateway();

        protected override WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> host) => host.WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", AdminKey);
            web.ConfigureTestServices(services =>
                services.UseGatewayCredentialCeiling().AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _gateway.Server.CreateHandler()));
        });

        protected override ValueTask CleanupAsync() => _gateway.DisposeAsync();
    }

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
    public async Task An_assistant_created_from_a_template_starts_with_a_copy_that_a_later_template_change_leaves_alone()
    {
        // B-54 (admin design §5 step 9): the template's instructions, not-found texts and query steps are copied into the first
        // draft, and no link is kept.
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var made = await platform.PostAsJsonAsync("/api/assistant-templates", new
        {
            name = "Intern policy " + seed.Code, systemPrompt = "Svara bara utifrån dokumenten och ange dokumentets ägare.",
            notFound = new { sv = "Det står inte i policyerna.", links = new[] { new { title = "HR", url = "https://intranat.umea.se/hr" } } },
            querySteps = new { answer = new { maxOutputTokens = 900 } },
        }, Ct);
        made.StatusCode.ShouldBe(HttpStatusCode.Created, await made.Content.ReadAsStringAsync(Ct));
        var template = await Json(made);
        var templateId = template.GetProperty("id").GetGuid();

        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Policyhjälpen " + seed.Code, departmentId = seed.Department, classification = "internal", templateId,
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await Json(created)).GetProperty("id").GetGuid();

        Task<JsonElement> DraftAsync(Guid assistant) => admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{assistant}/draft", Ct);
        var draft = await DraftAsync(id);
        draft.GetProperty("systemPrompt").GetString().ShouldBe("Svara bara utifrån dokumenten och ange dokumentets ägare.");
        draft.GetProperty("notFound").GetProperty("sv").GetString().ShouldBe("Det står inte i policyerna.");
        draft.GetProperty("notFound").GetProperty("links")[0].GetProperty("url").GetString().ShouldBe("https://intranat.umea.se/hr");
        draft.GetProperty("querySteps").GetProperty("answer").GetProperty("maxOutputTokens").GetInt32().ShouldBe(900);

        // The template changes and is then switched off: the assistant keeps its copy, and no new assistant starts from it.
        using (var changed = await platform.PutAsJsonAsync($"/api/assistant-templates/{templateId}", new
               {
                   name = "Intern policy " + seed.Code, systemPrompt = "Ny text.", isActive = false, version = template.GetProperty("version").GetString(),
               }, Ct))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        }

        var kept = await DraftAsync(id);
        (kept.GetProperty("systemPrompt").GetString(), kept.GetProperty("notFound").GetProperty("sv").GetString())
            .ShouldBe(("Svara bara utifrån dokumenten och ange dokumentets ägare.", "Det står inte i policyerna."));
        foreach (var unavailable in new[] { templateId, Guid.NewGuid() })
        {
            using var refused = await admin.PostAsJsonAsync("/api/assistants", new
            {
                name = "Policyhjälpen 2 " + seed.Code, departmentId = seed.Department, classification = "internal", templateId = unavailable,
            }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(refused)).GetProperty("errorCodes").GetProperty("templateId")[0].GetProperty("code").GetString().ShouldBe("not_found");
        }

        // Without a template the draft starts from Settings.Chat.defaultSystemPrompt, as in P1.
        using var plain = await admin.PostAsJsonAsync("/api/assistants", new { name = "Utan mall " + seed.Code, departmentId = seed.Department, classification = "internal" }, Ct);
        plain.StatusCode.ShouldBe(HttpStatusCode.Created);
        var plainDraft = await DraftAsync((await Json(plain)).GetProperty("id").GetGuid());
        await using var db = TestPostgres.Context(database.Connection(null));
        plainDraft.GetProperty("systemPrompt").GetString().ShouldBe(ChatDefaults.SystemPromptFrom(await db.Settings.AsNoTracking().Select(s => s.Chat).FirstOrDefaultAsync(Ct)));
        plainDraft.GetProperty("notFound").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_assistant_is_drafted_published_offered_to_a_client_and_republished()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
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
                   gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, querySteps = new { retrieval = new { topK = 99 } }, version = draftVersion,
               }, Ct))
        {
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            var body = await Json(draft);
            Codes(body.GetProperty("warnings")).ShouldBe(["assistant_contact_missing", "query_steps_invalid", "gateway_key_required"], ignoreOrder: true);
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
                   gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, querySteps = new { retrieval = new { topK = 10 } }, version = draftVersion,
               }, Ct))
        {
            var body = await Json(fixedDraft);
            Codes(body.GetProperty("warnings")).ShouldBe([AdminErrorCodes.GatewayKeyRequired.Code]); // keyless: V39 warns, publish goes ahead (no act-as-key)
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
                   gatewayModel = Model, systemPrompt = "Svara kort och vänligt.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } },
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
    public async Task Publish_refuses_query_steps_the_runtime_would_replace_by_its_default()
    {
        // V17 (admin design §4.6): the integer fields are integers exactly as AssistantChatSettings.Parse reads them (a
        // fraction, a string or 10.0 would silently become the default there), condense.enabled is a boolean, and the answer's
        // two overrides are bounded by the model's limits (Decisions "Answer length from the model"): contextTokens 4,096 to the
        // model's window (1,048,576 when unknown), maxOutputTokens 64 to its output limit (1,000,000 when unknown). The steps go
        // as raw JSON text so 10.0 reaches the database as written.
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Typer " + seed.Code, departmentId = seed.Department, classification = Classification.Internal, description = "D", contactName = "K", contactEmail = "k@umea.se",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var assistant = await Json(created);
        var id = assistant.GetProperty("id").GetGuid();
        var draftVersion = assistant.GetProperty("draft").GetProperty("version").GetString();

        async Task<HttpResponseMessage> PublishAsync(string querySteps)
        {
            using (var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
                   {
                       gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } },
                       querySteps = JsonNode.Parse(querySteps), version = draftVersion,
                   }, Ct))
            {
                draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
                draftVersion = (await Json(draft)).GetProperty("version").GetString();
            }

            var version = (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct)).GetProperty("version").GetString();
            return await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version, draftVersion }, Ct);
        }

        foreach (var (steps, field) in new[]
                 {
                     ("""{"retrieval":{"topK":10.5}}""", "retrieval.topK"),
                     ("""{"retrieval":{"topK":"10"}}""", "retrieval.topK"),
                     ("""{"retrieval":{"topK":10.0}}""", "retrieval.topK"),
                     ("""{"condense":{"enabled":"false"}}""", "condense.enabled"),
                     ("""{"answer":{"contextTokens":1000}}""", "answer.contextTokens"),
                     ("""{"answer":{"contextTokens":1048577}}""", "answer.contextTokens"),
                     ("""{"answer":{"maxOutputTokens":63}}""", "answer.maxOutputTokens"),
                     ("""{"answer":{"maxOutputTokens":1000001}}""", "answer.maxOutputTokens"),
                 })
        {
            using var refused = await PublishAsync(steps);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, steps);
            var problem = await Json(refused);
            problem.GetProperty("code").GetString().ShouldBe("query_steps_invalid", steps);
            problem.GetProperty("params").GetProperty("field").GetString().ShouldBe(field, steps);
        }

        // No fixed 4,096 output ceiling any more: an unknown limit allows a large override.
        using (var large = await PublishAsync("""{"answer":{"contextTokens":200000,"maxOutputTokens":32000}}"""))
        {
            large.StatusCode.ShouldBe(HttpStatusCode.OK, await large.Content.ReadAsStringAsync(Ct));
        }

        // With the model's limits on its mirror row, they are the upper bounds.
        await using var db = TestPostgres.Context(database.Connection(null));
        await db.Database.ExecuteSqlAsync($"""UPDATE config."GatewayModels" SET "ContextWindow" = 32768, "MaxOutputTokens" = 8192 WHERE "Name" = {Model}""", Ct);
        try
        {
            foreach (var (steps, field) in new[]
                     {
                         ("""{"answer":{"maxOutputTokens":8193}}""", "answer.maxOutputTokens"),
                         ("""{"answer":{"contextTokens":32769}}""", "answer.contextTokens"),
                     })
            {
                using var refused = await PublishAsync(steps);
                refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, steps);
                (await Json(refused)).GetProperty("params").GetProperty("field").GetString().ShouldBe(field, steps);
            }

            using var published = await PublishAsync("""{"answer":{"contextTokens":32768,"maxOutputTokens":8192},"condense":{"enabled":false}}""");
            published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"""UPDATE config."GatewayModels" SET "ContextWindow" = NULL, "MaxOutputTokens" = NULL WHERE "Name" = {Model}""", Ct);
        }
    }

    [Fact]
    public void V17_bounds_the_output_override_by_the_platform_ceiling_too()
    {
        // chat §6.3: lowering Settings.Chat.MaxAnswerTokens below an override is a warning on the draft (refused at publish).
        var model = new GatewayModel { Name = "m", Residencies = ["Eu"], MaxOutputTokens = 16_384 };
        static string[] Fields(IEnumerable<RuleProblem> problems) => [.. problems.Select(p => p.Params!["field"])];
        Fields(AssistantRules.QuerySteps("""{"answer":{"maxOutputTokens":9000}}""", model, 8000)).ShouldBe(["answer.maxOutputTokens"]);
        Fields(AssistantRules.QuerySteps("""{"answer":{"maxOutputTokens":8000}}""", model, 8000)).ShouldBeEmpty();
        Fields(AssistantRules.QuerySteps("""{"answer":{"maxOutputTokens":9000}}""", model, null)).ShouldBeEmpty();
        Fields(AssistantRules.QuerySteps("""{"answer":{"maxOutputTokens":20000}}""", null, 30000)).ShouldBeEmpty(); // model unknown
    }

    [Fact]
    public async Task A_new_assistants_first_draft_starts_from_the_default_instructions()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        await using var db = TestPostgres.Context(database.Connection(null));
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var saved = await db.Settings.AsNoTracking().Select(x => x.Chat).SingleAsync(Ct);
        async Task<string> FirstDraftAsync(string name)
        {
            using var created = await admin.PostAsJsonAsync("/api/assistants", new { name = $"{name} {seed.Code}", departmentId = seed.Department, classification = "internal" }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            var id = (await Json(created)).GetProperty("id").GetGuid();
            return (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}/draft", Ct)).GetProperty("systemPrompt").GetString()!;
        }

        async Task SetChatAsync(System.Text.Json.Nodes.JsonObject chat) =>
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Chat, chat.ToJsonString()), Ct);

        try
        {
            var chat = System.Text.Json.Nodes.JsonNode.Parse(saved) as System.Text.Json.Nodes.JsonObject ?? [];

            // No key yet (a row from before item 39): the code's citizen-service instructions.
            chat.Remove(ChatDefaults.SystemPromptKey);
            await SetChatAsync(chat);
            (await FirstDraftAsync("Standard")).ShouldBe(ChatDefaults.SystemPrompt);

            // The platform's text, as edited on the Settings page; an emptied one stays empty.
            chat[ChatDefaults.SystemPromptKey] = "Svara som kommunens kontaktcenter.";
            await SetChatAsync(chat);
            (await FirstDraftAsync("Redigerad")).ShouldBe("Svara som kommunens kontaktcenter.");
            chat[ChatDefaults.SystemPromptKey] = "";
            await SetChatAsync(chat);
            (await FirstDraftAsync("Tom")).ShouldBe("");
        }
        finally
        {
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Chat, saved), Ct);
        }
    }

    [Fact]
    public async Task General_knowledge_is_a_draft_field_and_a_public_assistant_needs_a_confirmation()
    {
        // P1b item 40: V17 accepts answer.generalKnowledge off/supplement/fallback/open; V36 keeps public assistants off
        // (refused on publish and on a level change, a warning on draft save).
        var seed = await SeedAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Collections.Where(c => c.Id == seed.Collection).ExecuteUpdateAsync(u => u.SetProperty(c => c.Classification, Classification.Public), Ct);
        }

        var factory = host.Factory;
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
                gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, querySteps = new { answer = new { generalKnowledge } },
                version = draftVersion,
            }, Ct);
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            var body = await Json(draft);
            body.GetProperty("querySteps").GetProperty("answer").GetProperty("generalKnowledge").GetString().ShouldBe(generalKnowledge);
            return ([.. Codes(body.GetProperty("warnings")).Where(c => c != AdminErrorCodes.GatewayKeyRequired.Code)], body.GetProperty("version").GetString()); // keyless: V39 warns
        }

        async Task<(IReadOnlyList<string> Warnings, string? DraftVersion)> StepsAsync(Guid id, object querySteps, string? draftVersion)
        {
            using var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
            {
                gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, querySteps,
                version = draftVersion,
            }, Ct);
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            var body = await Json(draft);
            return ([.. Codes(body.GetProperty("warnings")).Where(c => c != AdminErrorCodes.GatewayKeyRequired.Code)], body.GetProperty("version").GetString()); // keyless: V39 warns
        }

        // An internal assistant: an unknown value is V17's warning; fallback is accepted and publishes.
        var (inner, innerVersion, innerDraft) = await CreateAsync("Intern", Classification.Internal);
        var (warnings, next) = await DraftAsync(inner, "sometimes", innerDraft);
        warnings.ShouldBe(["query_steps_invalid"]);

        // retrieval.rerank (B-12, B-16) is off, chat or gateway; anything else is V17's warning too. A reranker the platform has
        // no model for is no warning: rerank is optional, and the assistant page says why the choice is disabled.
        (warnings, next) = await StepsAsync(inner, new { retrieval = new { rerank = "yes" } }, next);
        warnings.ShouldBe(["query_steps_invalid"]);
        (warnings, next) = await StepsAsync(inner, new { retrieval = new { rerank = true } }, next);
        warnings.ShouldBe(["query_steps_invalid"]);
        foreach (var value in new[] { "off", "chat", "gateway" })
        {
            (warnings, next) = await StepsAsync(inner, new { retrieval = new { rerank = value } }, next);
            warnings.ShouldBeEmpty();
        }

        // The platform's rerankers (B-16): none here, the usual setup, so the page disables the choice.
        (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{inner}", Ct)).GetProperty("rerankers").GetArrayLength().ShouldBe(0);
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

        async Task<string?> AuditConfirmedAsync(Guid id, string action)
        {
            await using var db = TestPostgres.Context(database.Connection(null));
            var details = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "assistant" && a.EntityId == id.ToString() && a.Action == action)
                .OrderByDescending(a => a.Id).Select(a => a.Details).FirstAsync(Ct);
            using var doc = JsonDocument.Parse(details!);
            return doc.RootElement.GetProperty("after").TryGetProperty("generalKnowledgeConfirmed", out var flag) && flag.ValueKind == JsonValueKind.True ? "true" : null;
        }

        // V36 is a confirmed warning (#445). Lowering the level to public while the published version uses general knowledge
        // answers 409 until the request confirms it; the confirmed change is audited.
        object Lower(bool confirm) => new
        {
            name = "Intern " + seed.Code, classification = "public", description = "D", contactName = "K", contactEmail = "k@umea.se", version = innerVersion,
            confirmGeneralKnowledge = confirm,
        };
        using (var lowered = await admin.PutAsJsonAsync($"/api/assistants/{inner}", Lower(false), Ct))
        {
            lowered.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(lowered)).GetProperty("code").GetString().ShouldBe("general_knowledge_public_unconfirmed");
        }

        using (var lowered = await admin.PutAsJsonAsync($"/api/assistants/{inner}", Lower(true), Ct))
        {
            lowered.StatusCode.ShouldBe(HttpStatusCode.OK, await lowered.Content.ReadAsStringAsync(Ct));
            var body = await Json(lowered);
            body.GetProperty("classification").GetString().ShouldBe("public");
            body.GetProperty("generalKnowledge").GetString().ShouldBe("fallback");
        }

        (await AuditConfirmedAsync(inner, "update")).ShouldBe("true");

        // A public assistant: supplement saves with the V36 warning; publish answers 409 until confirmed.
        var (open, openVersion, openDraft) = await CreateAsync("Publik", Classification.Public);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{open}", Ct)).GetProperty("generalKnowledge").ValueKind.ShouldBe(JsonValueKind.Null);
        (warnings, next) = await DraftAsync(open, "supplement", openDraft);
        warnings.ShouldBe(["general_knowledge_public_unconfirmed"]);
        using (var refused = await admin.PostAsJsonAsync($"/api/assistants/{open}/publish", new { version = openVersion, draftVersion = next }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Json(refused);
            problem.GetProperty("code").GetString().ShouldBe("general_knowledge_public_unconfirmed");
            problem.GetProperty("problems").GetArrayLength().ShouldBe(1);
        }

        using (var confirmed = await admin.PostAsJsonAsync($"/api/assistants/{open}/publish", new { version = openVersion, draftVersion = next, confirmGeneralKnowledge = true }, Ct))
        {
            confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
            var body = await Json(confirmed);
            openVersion = body.GetProperty("version").GetString();
            body.GetProperty("generalKnowledge").GetString().ShouldBe("supplement");
        }

        (await AuditConfirmedAsync(open, "publish")).ShouldBe("true");

        // Off publishes without a confirmation, and the audit records none.
        (warnings, next) = await DraftAsync(open, "off", null); // the first edit after a publish starts a new draft
        warnings.ShouldBeEmpty();
        using (var off = await admin.PostAsJsonAsync($"/api/assistants/{open}/publish", new { version = openVersion, draftVersion = next }, Ct))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.OK, await off.Content.ReadAsStringAsync(Ct));
            openVersion = (await Json(off)).GetProperty("version").GetString();
        }

        (await AuditConfirmedAsync(open, "publish")).ShouldBeNull();

        // Restoring version 1 (supplement) answers 409 until confirmed, like publish.
        using (var refused = await admin.PostAsJsonAsync($"/api/assistants/{open}/versions/1/restore", new { version = openVersion }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(refused)).GetProperty("code").GetString().ShouldBe("general_knowledge_public_unconfirmed");
        }

        using (var restored = await admin.PostAsJsonAsync($"/api/assistants/{open}/versions/1/restore", new { version = openVersion, confirmGeneralKnowledge = true }, Ct))
        {
            restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync(Ct));
            (await Json(restored)).GetProperty("generalKnowledge").GetString().ShouldBe("supplement");
        }

        (await AuditConfirmedAsync(open, "restore")).ShouldBe("true");
    }

    [Fact]
    public async Task Publish_refuses_a_model_the_gateway_does_not_serve_at_the_ceiling()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            if (!await db.GatewayModels.AnyAsync(m => m.Name == "fors/chat-advanced", Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = "fors/chat-advanced", Residencies = ["External"], Source = GatewayModelSources.Catalog });
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
            gatewayModel = "fors/chat-advanced", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, version = assistant.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct);
        var draftVersion = (await Json(draft)).GetProperty("version").GetString();

        // An internal assistant runs at ceiling Eu; the model is External only: V8's mirror check refuses before the gateway call.
        using var refused = await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version = assistant.GetProperty("version").GetString(), draftVersion }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(refused)).GetProperty("code").GetString().ShouldBe("model_residency");
    }

    [Fact]
    public async Task Publish_refuses_an_embedding_model_as_the_answer_model()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        var embedding = "embed-" + seed.Code.ToLowerInvariant();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            // Within the ceiling, so only V34's chat-row check can refuse it (#363 point 1).
            db.GatewayModels.Add(new GatewayModel { Name = embedding, ModelKind = GatewayModelModelKinds.Embedding, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
            await db.SaveChangesAsync(Ct);
        }

        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Inbäddning " + seed.Code, departmentId = seed.Department, classification = "internal", description = "D", contactName = "K", contactEmail = "k@umea.se",
        }, Ct);
        var assistant = await Json(created);
        var id = assistant.GetProperty("id").GetGuid();
        using var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
        {
            gatewayModel = embedding, collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, version = assistant.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct);
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
        var draftVersion = (await Json(draft)).GetProperty("version").GetString();

        using var refused = await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new { version = assistant.GetProperty("version").GetString(), draftVersion }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));
        var body = await Json(refused);
        body.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.VersionLinkMissing.Code);
        body.GetRawText().ShouldContain(embedding);
    }

    [Fact]
    public async Task Versions_are_listed_restored_as_a_new_published_version_and_archive_and_retention_are_saved()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
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
            using var draft = await admin.PutAsJsonAsync($"{path}/draft", new { gatewayModel = Model, systemPrompt = prompt, collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } }, version }, Ct);
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

    [Fact]
    public async Task A_deleted_assistant_answers_404_leaves_the_list_and_its_knowledge_base_frees_its_name_and_keeps_its_statistics()
    {
        // B-51 (#878; Decisions "Deleting knowledge bases, sources and assistants").
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        var name = "Raderas " + seed.Code;
        async Task<JsonElement> CreateAsync()
        {
            using var created = await admin.PostAsJsonAsync("/api/assistants", new
            {
                name, departmentId = seed.Department, classification = "internal", description = "D", contactName = "K", contactEmail = "k@umea.se",
            }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            return await Json(created);
        }

        var assistant = await CreateAsync();
        var id = assistant.GetProperty("id").GetGuid();
        var path = $"/api/assistants/{id}";
        using (var draft = await admin.PutAsJsonAsync($"{path}/draft", new
        {
            gatewayModel = Model, collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } },
            version = assistant.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct))
        {
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
        }

        var usedBy = async () => (await admin.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct)).GetProperty("usedBy")
            .EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();
        (await usedBy()).ShouldContain(id);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.AssistantDailyTotals.Add(new AssistantDailyTotal
            {
                Day = DateOnly.FromDateTime(DateTime.UtcNow), AssistantId = id, ClientId = Guid.NewGuid(), DepartmentId = seed.Department, Classification = "internal",
                Conversations = 2, Messages = 5,
            });
            await db.SaveChangesAsync(Ct);
        }

        var version = (await admin.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("version").GetString();
        (await editor.DeleteAsync($"{path}?version={version}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // Manage
        (await admin.DeleteAsync($"{path}?version=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // a stale version
        (await admin.DeleteAsync($"{path}?version={version}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Gone from every admin route and list, and it no longer keeps its knowledge base in use.
        (await admin.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"{path}?version={version}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants?q={Uri.EscapeDataString(name)}", Ct)).GetProperty("total").GetInt32().ShouldBe(0);
        (await usedBy()).ShouldNotContain(id);

        // Its name is free at once; its statistics stay their own time; the draft went with it.
        var again = await CreateAsync();
        again.GetProperty("slug").GetString().ShouldBe(assistant.GetProperty("slug").GetString());
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.AssistantDailyTotals.AsNoTracking().CountAsync(t => t.AssistantId == id, Ct)).ShouldBe(1);
            var row = await db.Assistants.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == id, Ct);
            (row.DeletedAt.HasValue, row.DraftVersionId).ShouldBe((true, (Guid?)null));
            (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "assistant" && a.EntityId == id.ToString()).OrderBy(a => a.Id).Select(a => a.Action)
                .ToListAsync(Ct)).Last().ShouldBe("delete");
        }
    }

    [Fact]
    public async Task The_list_shows_the_shown_versions_knowledge_bases_and_model_the_publication_and_the_questions_of_7_stockholm_days()
    {
        var seed = await SeedAsync();
        // 23:30 UTC on 28 March 2026 is already 29 March in Stockholm (CET): the 7 days are 23–29 March.
        var now = new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero);
        var today = new DateOnly(2026, 3, 29);
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
            web.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(now))));
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        Guid published, draftOnly, empty, draftCollection;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var original = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.Collection, Ct);
            Collection CollectionOf(string name) => new()
            {
                Slug = $"{name.ToLowerInvariant()}-{seed.Code.ToLowerInvariant()}", Name = $"{name} {seed.Code}", DepartmentId = seed.Department,
                Classification = Classification.Internal, EmbeddingProfileId = original.EmbeddingProfileId, UpdatedAt = now,
            };
            var other = CollectionOf("Detaljplaner");
            var drafted = CollectionOf("Skolor");
            Assistant AssistantOf(string name) => new()
            {
                Slug = $"{name.ToLowerInvariant()}-{seed.Code.ToLowerInvariant()}", Name = $"{name} {seed.Code}", DepartmentId = seed.Department,
                Classification = Classification.Internal, UpdatedAt = now,
            };
            var live = AssistantOf("Publicerad");
            var draft = AssistantOf("Utkast");
            var none = AssistantOf("Tom");
            db.AddRange(other, drafted, live, draft, none);
            await db.SaveChangesAsync(Ct);

            AssistantVersion VersionOf(Guid assistant, int number, string state, string model, params Guid[] collections) => new()
            {
                AssistantId = assistant, Number = number, State = state, GatewayModel = model, CollectionLinks = [.. collections.Select(CollectionLink.Whole)],
                PublishedAt = state == AssistantVersionStates.Draft ? null : now.AddDays(-number), PublishedBy = state == AssistantVersionStates.Draft ? null : "test",
                CreatedAt = now, UpdatedAt = now,
            };
            var v1 = VersionOf(live.Id, 1, AssistantVersionStates.Superseded, "old-model", drafted.Id);
            var v2 = VersionOf(live.Id, 2, AssistantVersionStates.Published, Model, other.Id, seed.Collection);
            var v3 = VersionOf(live.Id, 3, AssistantVersionStates.Draft, "draft-model", drafted.Id);
            var d1 = VersionOf(draft.Id, 1, AssistantVersionStates.Draft, "draft-model", drafted.Id);
            db.AssistantVersions.AddRange(v1, v2, v3, d1);
            await db.SaveChangesAsync(Ct);
            (live.PublishedVersionId, live.DraftVersionId, draft.DraftVersionId) = (v2.Id, v3.Id, d1.Id);

            AssistantDailyTotal TotalOf(Guid assistant, DateOnly day, Guid client, int messages) => new()
            {
                Day = day, AssistantId = assistant, ClientId = client, DepartmentId = seed.Department, Classification = Classification.Internal, Messages = messages,
            };
            db.AssistantDailyTotals.AddRange(
                TotalOf(live.Id, today.AddDays(-7), seed.InternalClient, 1000), // 22 March: inside the window of the UTC date, outside Stockholm's
                TotalOf(live.Id, today.AddDays(-6), seed.InternalClient, 7),
                TotalOf(live.Id, today, seed.InternalClient, 11),
                TotalOf(live.Id, today, seed.PublicClient, 2),
                TotalOf(live.Id, today.AddDays(1), seed.InternalClient, 100)); // after today
            await db.SaveChangesAsync(Ct);
            (published, draftOnly, empty, draftCollection) = (live.Id, draft.Id, none.Id, drafted.Id);
        }

        var items = (await admin.GetFromJsonAsync<JsonElement>("/api/assistants?pageSize=200", Ct)).GetProperty("items").EnumerateArray()
            .ToDictionary(a => a.GetProperty("id").GetGuid());

        // The published version's links (in their order) and model, not the draft's.
        var listed = items[published];
        listed.GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe([$"Detaljplaner {seed.Code}", $"Bygglov {seed.Code}"]);
        listed.GetProperty("collections")[1].GetProperty("id").GetGuid().ShouldBe(seed.Collection);
        listed.GetProperty("model").GetString().ShouldBe(Model);
        listed.GetProperty("publishedVersion").GetInt32().ShouldBe(2);
        listed.GetProperty("publishedAt").GetDateTimeOffset().ShouldBe(now.AddDays(-2));
        listed.GetProperty("questions7d").GetInt32().ShouldBe(20);

        // Before the first publish: the draft's links and model, no publication, no questions.
        var unpublished = items[draftOnly];
        unpublished.GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([draftCollection]);
        unpublished.GetProperty("model").GetString().ShouldBe("draft-model");
        unpublished.GetProperty("publishedVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        unpublished.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        unpublished.GetProperty("questions7d").GetInt32().ShouldBe(0);

        // No version at all.
        var unversioned = items[empty];
        unversioned.GetProperty("collections").GetArrayLength().ShouldBe(0);
        unversioned.GetProperty("model").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
