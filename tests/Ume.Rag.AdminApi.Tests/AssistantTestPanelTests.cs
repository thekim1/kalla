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
[Collection(AdminDatabaseGroup.Name)]
public sealed class AssistantTestPanelTests(AdminDatabaseFixture database)
{
    private const string EuKey = "sk-ume-eu-panel-0000000000000000";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Keys:Eu", EuKey);
        web.ConfigureTestServices(services =>
        {
            foreach (var name in new[] { GatewayChatClient.HttpClientName, GatewayQueryEmbedder.HttpClientName })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            }
        });
    });

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
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", SystemPrompt = "Svara kort.", CollectionIds = [collection.Id],
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
            web.UseSetting("FakeGateway:Keys:Eu", EuKey);
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
        }

        (await editor.PostAsJsonAsync(path, new { question = word, target = "Published" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await viewer.PostAsJsonAsync(path, new { question = word }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.PostAsJsonAsync(path, new { question = word }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await db.AuditLog.AsNoTracking().CountAsync(Ct)).ShouldBe(auditBefore);
        (await db.Conversations.AsNoTracking().CountAsync(c => c.AssistantId == assistant.Id, Ct)).ShouldBe(0);
    }
}
