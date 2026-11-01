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
/// The retrieval-only test panel (admin design §4.5, §4.9): an unpublished collection is searched with its settings and
/// demote patterns, hits are hydrated with title, heading path, a 600-character text and the per-list scores; scope and
/// policy as the collection's; nothing is audited.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class TestRetrievalTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unpublished_collection_is_searched_with_its_scores_and_nothing_is_stored()
    {
        var code = "R" + Random.Shared.Next(100_000, 999_999);
        var word = "tr" + code.ToLowerInvariant() + "kompost";
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Test " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "test-" + code.ToLowerInvariant(), Name = "Prov " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "test-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Public, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, pipeline, source);
        await db.SaveChangesAsync(Ct);
        var longText = $"Så sorterar du {word} i Umeå. " + new string('x', 700);
        var first = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Title = "Kompostering", Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk(longText) { HeadingPath = "Avfall › Kompost" }], Ct);
        var second = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, IdentityKind = "upload", DisplayName = "kompost.pdf",
            MediaType = "application/pdf",
        }, [new FixtureChunk($"Broschyr om {word}.")], Ct);

        await using var factory = database.HeaderFactory();
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, "X" + code);
        var path = $"/api/collections/{collection.Id}/test-retrieval";
        var auditBefore = await db.AuditLog.AsNoTracking().CountAsync(Ct);

        using (var response = await editor.PostAsJsonAsync(path, new { question = word, mode = "Keyword" }, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            result.GetProperty("collection").GetProperty("published").GetBoolean().ShouldBeFalse();
            var candidates = result.GetProperty("candidates").EnumerateArray().ToList();
            candidates.Select(c => c.GetProperty("documentId").GetGuid()).ShouldBe([first.Id, second.Id], ignoreOrder: true);
            var page = candidates.Single(c => c.GetProperty("documentId").GetGuid() == first.Id);
            (page.GetProperty("title").GetString(), page.GetProperty("url").GetString(), page.GetProperty("headingPath").GetString())
                .ShouldBe(("Kompostering", $"https://www.umea.se/{code}/kompost", "Avfall › Kompost"));
            (page.GetProperty("text").GetString()!.Length, page.GetProperty("truncated").GetBoolean()).ShouldBe((600, true));
            page.GetProperty("keywordRank").GetInt32().ShouldBeGreaterThan(0);
            var file = candidates.Single(c => c.GetProperty("documentId").GetGuid() == second.Id);
            (file.GetProperty("fileName").GetString(), file.GetProperty("truncated").GetBoolean()).ShouldBe(("kompost.pdf", false));
            candidates.Select(c => c.GetProperty("rank").GetInt32()).ShouldBe([1, 2]);
        }

        // Hybrid with the gateway unreachable: the keyword side still answers, marked degraded (knowledge design §5.5).
        await using (var unreachable = factory.WithWebHostBuilder(web => web.UseSetting("Gateway:BaseUrl", "http://fake-gateway.invalid/")))
        {
            using var other = await AdminDatabaseFixture.UserAsync(unreachable, RagRoles.Editor, code);
            using var hybrid = await other.PostAsJsonAsync(path, new { question = word }, Ct);
            hybrid.StatusCode.ShouldBe(HttpStatusCode.OK, await hybrid.Content.ReadAsStringAsync(Ct));
            var result = await hybrid.Content.ReadFromJsonAsync<JsonElement>(Ct);
            result.GetProperty("degraded").GetString().ShouldBe("embedding_unavailable");
            result.GetProperty("candidates").GetArrayLength().ShouldBe(2);
        }

        // The editor's department only; a viewer may not run it; an empty question is a field error.
        (await stranger.PostAsJsonAsync(path, new { question = word }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await viewer.PostAsJsonAsync(path, new { question = word }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // #283: Edit or EditPipelines; a pipeline editor runs the retrieval test too.
        using (var pipelines = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PipelineEditor, code))
        {
            (await pipelines.PostAsJsonAsync(path, new { question = word, mode = "Keyword" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        (await editor.PostAsJsonAsync(path, new { question = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await db.AuditLog.AsNoTracking().CountAsync(Ct)).ShouldBe(auditBefore);
    }
}
