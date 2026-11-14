extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Answering;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The test panel (admin design §4.9): the draft is answered through Ume.Rag.Answering's chat pipeline on FakeGateway,
/// over a collection that is not published; raw and delivered answers, candidates and the gateway call are shown;
/// nothing is stored.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class AssistantTestPanelTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "sk-ume-eu-panel-0000000000000000"; // rag-admin\'s credential key
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The rerank calls the panel sent (B-16), on the rerankers' gateway client.</summary>
    private int _rerankCalls;

    /// <summary>The bodies of the panel's chat calls to the gateway, in order (B-32: what the answer model was sent).</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _chatBodies = new();

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Key", AdminKey);
        web.ConfigureTestServices(services =>
        {
            services.UseGatewayCredentialCeiling();
            services.AddHttpClient(GatewayChatClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new Recording(gateway.Server.CreateHandler(), _chatBodies));
            services.AddHttpClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());

            services.AddHttpClient(Ume.Rag.Retrieval.ChatModelReranker.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new Counting(gateway.Server.CreateHandler(), () => Interlocked.Increment(ref _rerankCalls)));
        });
    });

    private sealed class Recording(HttpMessageHandler inner, System.Collections.Concurrent.ConcurrentQueue<string> bodies) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Counting(HttpMessageHandler inner, Action counted) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counted();
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_draft_is_answered_over_an_unpublished_collection_and_nothing_is_stored()
    {
        var code = "P" + Random.Shared.Next(100_000, 999_999);
        var word = "tp" + code.ToLowerInvariant() + "kompost";
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Panel " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "panel-" + code.ToLowerInvariant(), Name = "Kompost " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var assistant = new Assistant
        {
            Slug = "panel-" + code.ToLowerInvariant(), Name = "Kompostguiden " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var draft = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", SystemPrompt = "Svara kort.", CollectionLinks = [CollectionLink.Whole(collection.Id)],
            // B-16: set to rerank with the gateway on a platform without a rerank model (the usual setup): plain retrieval.
            QuerySteps = """{"retrieval": {"rerank": "gateway"}}""",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "panel-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, assistant, pipeline, source);
        await db.SaveChangesAsync(Ct);
        db.Add(draft);
        await db.SaveChangesAsync(Ct);
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.DraftVersionId, draft.Id), Ct);
        var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Kompostering",
            Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk($"Så komposterar du {word} hemma i Umeå.") { HeadingPath = "Avfall › Kompost" }], Ct);

        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        });
        await using var factory = Admin(gateway);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, "X" + code);
        var path = $"/api/assistants/{assistant.Id}/test";
        var auditBefore = await db.AuditLog.AsNoTracking().CountAsync(Ct);

        using (var response = await editor.PostAsJsonAsync(path, new { question = $"Hur gör jag med {word}?" }, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            (result.GetProperty("target").GetString(), result.GetProperty("versionNumber").GetInt32()).ShouldBe(("Draft", 1));
            // Rerank fully unconfigured (B-16): no rerank call and no rerank_unavailable (this fixture's 8-dimension profile
            // degrades the embedding, which is not the rerank's).
            result.GetProperty("retrieval").GetProperty("degraded").GetString().ShouldNotBe("rerank_unavailable");
            _rerankCalls.ShouldBe(0);
            var searched = result.GetProperty("retrieval").GetProperty("collections").EnumerateArray().ShouldHaveSingleItem();
            (searched.GetProperty("id").GetGuid(), searched.GetProperty("published").GetBoolean()).ShouldBe((collection.Id, false));
            var candidate = result.GetProperty("retrieval").GetProperty("candidates").EnumerateArray().ShouldHaveSingleItem();
            (candidate.GetProperty("documentId").GetGuid(), candidate.GetProperty("title").GetString()).ShouldBe((document.Id, "Kompostering"));
            var answer = result.GetProperty("answer");
            answer.GetProperty("outcome").GetString().ShouldBe("answered");
            answer.GetProperty("raw").GetString().ShouldNotBeNullOrWhiteSpace();
            answer.GetProperty("delivered").GetString().ShouldNotBeNullOrWhiteSpace();
            var call = result.GetProperty("gateway");
            (call.GetProperty("model").GetString(), call.GetProperty("residency").GetString()).ShouldBe(("ume/chat-eu", "Eu"));
            call.GetProperty("inputTokens").GetInt32().ShouldBeGreaterThan(0);
            // B-31: the model's limit is unknown here, so max_tokens was left out and the provider's default applied.
            call.GetProperty("maxTokens").ValueKind.ShouldBe(JsonValueKind.Null);
            (call.GetProperty("maxTokensSource").GetString(), call.GetProperty("finishReason").GetString()).ShouldBe(("provider", "stop"));
        }

        // With the model's limits on its mirror row: its output limit (less the prompt within the window) is sent.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO config."GatewayModels" ("Name", "Kind", "ModelKind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource", "ContextWindow", "MaxOutputTokens")
            VALUES ('ume/chat-eu', 'model', 'chat', ARRAY['Eu']::varchar(10)[], 'catalog', false, 'manual', 32768, 2000)
            ON CONFLICT ("Name") DO UPDATE SET "ContextWindow" = 32768, "MaxOutputTokens" = 2000
            """, Ct);
        try
        {
            using var limited = await editor.PostAsJsonAsync(path, new { question = $"Hur gör jag med {word}?" }, Ct);
            limited.StatusCode.ShouldBe(HttpStatusCode.OK, await limited.Content.ReadAsStringAsync(Ct));
            var call = (await limited.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("gateway");
            (call.GetProperty("maxTokens").GetInt32(), call.GetProperty("maxTokensSource").GetString()).ShouldBe((2000, "model"));
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"""UPDATE config."GatewayModels" SET "ContextWindow" = NULL, "MaxOutputTokens" = NULL WHERE "Name" = 'ume/chat-eu'""", Ct);
        }

        (await editor.PostAsJsonAsync(path, new { question = word, target = "Published" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await viewer.PostAsJsonAsync(path, new { question = word }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // #283: Edit or EditSkills; a skill editor runs the test panel too.
        using (var skills = await AdminDatabaseFixture.UserAsync(factory, RagRoles.SkillEditor, code))
        {
            (await skills.PostAsJsonAsync(path, new { question = $"Hur gör jag med {word}?" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        (await stranger.PostAsJsonAsync(path, new { question = word }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await db.AuditLog.AsNoTracking().CountAsync(Ct)).ShouldBe(auditBefore);
        (await db.Conversations.AsNoTracking().CountAsync(c => c.AssistantId == assistant.Id, Ct)).ShouldBe(0);

        // B-32: the platform's prompt texts apply to the test panel at once (chat §7.3): an edited identity line is what the model gets.
        using (var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin))
        {
            var chat = await (await platform.GetAsync("/api/settings/chat", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            var original = System.Text.Json.Nodes.JsonNode.Parse(chat.GetProperty("value").GetRawText())!.AsObject();
            var edited = original.DeepClone().AsObject();
            edited["prompts"] = new System.Text.Json.Nodes.JsonObject { ["identity"] = "Hej, här svarar {assistant} i testpanelen." };
            using var saved = await platform.PutAsJsonAsync("/api/settings/chat", new { value = edited, version = chat.GetProperty("version").GetString() }, Ct);
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            try
            {
                _chatBodies.Clear();
                using var answered = await editor.PostAsJsonAsync(path, new { question = $"Hur gör jag med {word}?" }, Ct);
                answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync(Ct));
                var system = _chatBodies.Select(b => JsonDocument.Parse(b).RootElement).Single(b => b.GetProperty("stream").ValueKind is JsonValueKind.False)
                    .GetProperty("messages")[0].GetProperty("content").GetString()!;
                system.ShouldStartWith($"Hej, här svarar {assistant.Name} i testpanelen.\n");
                system.ShouldContain(PromptTexts.Locked.Citation); // the locked lines stay
            }
            finally
            {
                var now = await (await platform.GetAsync("/api/settings/chat", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
                using var restored = await platform.PutAsJsonAsync("/api/settings/chat", new { value = original, version = now.GetProperty("version").GetString() }, Ct);
                restored.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        }

    }

    [Fact]
    public async Task A_weak_question_to_a_fallback_draft_shows_the_caveat_line()
    {
        // P1b item 40 (admin design §5.5.4): the panel runs the draft's general-knowledge level. Embeddings as the FakeGateway
        // makes them, so the vector side works and an unrelated question gets coverage Low.
        var code = "G" + Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin),
            (text, dims) => fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector(text, dims));
        var profile = await writer.AddEmbeddingProfileAsync(1024, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        await db.EmbeddingProfiles.Where(p => p.Id == profile).ExecuteUpdateAsync(u => u.SetProperty(p => p.GatewayModel, "bge-m3"), Ct);
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Panel " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "panel-" + code.ToLowerInvariant(), Name = "Kompost " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var assistant = new Assistant
        {
            Slug = "panel-" + code.ToLowerInvariant(), Name = "Kompostguiden " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var draft = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", SystemPrompt = "Svara kort.", CollectionLinks = [CollectionLink.Whole(collection.Id)],
            QuerySteps = """{"answer":{"generalKnowledge":"fallback"}}""", NotFound = """{"links":[{"title":"Kontaktcenter","url":"https://www.umea.se/kontakt"}]}""",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "panel-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, assistant, pipeline, source);
        await db.SaveChangesAsync(Ct);
        db.Add(draft);
        await db.SaveChangesAsync(Ct);
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.DraftVersionId, draft.Id), Ct);
        await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Kompostering",
            Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk("Så komposterar du matavfall hemma i Umeå. Kompostbehållaren ska vara isolerad.") { HeadingPath = "Avfall › Kompost" }], Ct);

        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        });
        await using var factory = Admin(gateway);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);

        using var weak = await editor.PostAsJsonAsync($"/api/assistants/{assistant.Id}/test", new { question = "Hur långt är det till månen i kilometer?" }, Ct);
        weak.StatusCode.ShouldBe(HttpStatusCode.OK, await weak.Content.ReadAsStringAsync(Ct));
        var result = await weak.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var answer = result.GetProperty("answer");
        answer.GetProperty("outcome").GetString().ShouldBe("answered-general", result.GetProperty("retrieval").GetRawText());
        var delivered = answer.GetProperty("delivered").GetString()!;
        delivered.ShouldStartWith(FixedTexts.GeneralCaveatSv + "\n\n");
        delivered.ShouldEndWith("\n\n- [Kontaktcenter](https://www.umea.se/kontakt)");
        answer.GetProperty("raw").GetString()!.ShouldNotContain("sources"); // the fake echoes the user message: only the question was sent
        answer.GetProperty("citations").GetArrayLength().ShouldBe(0);
        result.GetProperty("gateway").GetProperty("inputTokens").GetInt32().ShouldBeGreaterThan(0);
    }
}
