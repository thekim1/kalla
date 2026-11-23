using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Återkoppling (admin design §8.1, chat design §14.4): ratings of the caller's departments' assistants without content,
/// the period's totals and import health, and Markera som hanterad (FeedbackComments, audited).
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class FeedbackEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid Assistant, Guid Client, Guid Down, Guid Up, Guid Document);

    /// <summary>
    /// One department's assistant (published version 3) offered to an Open WebUI client: a thumbs-down on an answered
    /// message citing a document (reason <c>incorrect</c>) and a thumbs-up on a not-found message; daily totals; the
    /// client's import state.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var code = "F" + Random.Shared.Next(100_000, 999_999);
        var now = DateTimeOffset.UtcNow;
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Återkoppling " + code, CostCenterCode = code, SyncedAt = now };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "fb-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = now,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "fb-" + code.ToLowerInvariant(), Name = "Bygglov " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile.Value, UpdatedAt = now,
        };
        var pipeline = new Pipeline { Name = "fb-" + code, TemplateId = "url-list", UpdatedAt = now };
        var source = new Source { CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = now };
        var url = $"https://www.umea.se/{code}/altan";
        var document = new Document
        {
            SourceId = source.Id, Classification = Classification.Internal, IdentityKind = "url", CanonicalIdentity = url, Url = url, Title = "Altan och uteplats",
            IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(url)),
        };
        var client = new Client { Name = "Open WebUI " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
        var assistant = new Assistant { Slug = "fb-" + code.ToLowerInvariant(), Name = "Bygglovsassistenten " + code, DepartmentId = department.Id, Classification = Classification.Internal, UpdatedAt = now };
        db.AddRange(department, collection, pipeline, source, document, client, assistant);
        await db.SaveChangesAsync(Ct);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 3, State = AssistantVersionStates.Published, GatewayModel = "fors/chat-eu", CollectionLinks = [CollectionLink.Whole(collection.Id)],
            PublishedAt = now, PublishedBy = "seed", CreatedAt = now, UpdatedAt = now,
        };
        db.AddRange(version, new AssistantAudience { AssistantId = assistant.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id });
        var conversation = new Conversation
        {
            Classification = Classification.Internal, AssistantId = assistant.Id, ClientId = client.Id, SubjectKind = "owui-user", SubjectId = "u-" + code,
            RetentionDays = 90, CreatedAt = now, LastMessageAt = now, ExpiresAt = now.AddDays(90),
        };
        Message Answer(int seq, string outcome, string coverage) => new()
        {
            ConversationId = conversation.Id, Classification = Classification.Internal, Seq = seq, Role = "assistant", Outcome = outcome, Coverage = coverage,
            Language = "sv", AssistantVersionId = version.Id, ExpiresAt = now.AddDays(90), CreatedAt = now,
        };
        var answered = Answer(2, "answered", "ok");
        var notFound = Answer(4, "not-found", "low");
        db.AddRange(conversation, answered, notFound);
        db.Add(new MessageSource
        {
            MessageId = answered.Id, Rank = 1, Classification = Classification.Internal, DocumentId = document.Id, ChunkId = Guid.CreateVersion7(), CollectionId = collection.Id,
            Number = 1, Used = true,
        });
        var down = new Feedback
        {
            MessageId = answered.Id, ConversationId = conversation.Id, Classification = Classification.Internal, ClientId = client.Id, Source = "owui-import",
            ExternalId = "owui-1", Rating = -1, ReasonCategory = "incorrect", CreatedAt = now, UpdatedAt = now,
        };
        var up = new Feedback
        {
            MessageId = notFound.Id, ConversationId = conversation.Id, Classification = Classification.Internal, ClientId = client.Id, Source = "owui-import",
            ExternalId = "owui-2", Rating = 1, CreatedAt = now.AddSeconds(1), UpdatedAt = now,
        };
        db.AddRange(down, up);
        db.Add(new AssistantDailyTotal
        {
            Day = DateOnly.FromDateTime(now.UtcDateTime), AssistantId = assistant.Id, ClientId = client.Id, DepartmentId = department.Id, Classification = Classification.Internal,
            Conversations = 1, Messages = 2, NotFound = 1, ThumbsUp = 1, ThumbsDown = 1,
        });
        db.Add(new FeedbackImportState { ClientId = client.Id, WatermarkUpdatedAt = 1, LastRunAt = now, LastOutcome = "ok", Unmapped = 2 });
        await db.SaveChangesAsync(Ct);
        return new Seed(code, assistant.Id, client.Id, down.Id, up.Id, document.Id);
    }

    [Fact]
    public async Task Lists_ratings_without_content_scoped_to_the_department_with_totals_and_import_health()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);

        var page = await viewer.GetFromJsonAsync<JsonElement>($"/api/feedback?assistantId={seed.Assistant}", Ct);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).ShouldBe([seed.Up, seed.Down]); // newest first
        var down = items[1];
        (down.GetProperty("rating").GetInt32(), down.GetProperty("reasonCategory").GetString(), down.GetProperty("outcome").GetString(), down.GetProperty("coverage").GetString())
            .ShouldBe((-1, "incorrect", "answered", "ok"));
        (down.GetProperty("assistantVersion").GetInt32(), down.GetProperty("hasComment").GetBoolean(), down.GetProperty("reviewState").GetString()).ShouldBe((3, false, "open"));
        down.GetProperty("clientName").GetString().ShouldBe("Open WebUI " + seed.Code);
        var cited = down.GetProperty("citedDocuments")[0];
        (cited.GetProperty("id").GetGuid(), cited.GetProperty("title").GetString()).ShouldBe((seed.Document, "Altan och uteplats"));
        items[0].GetProperty("citedDocuments").GetArrayLength().ShouldBe(0);
        // No content: no question, answer, title or comment field at all.
        var fields = down.EnumerateObject().Select(p => p.Name).ToList();
        fields.ShouldNotContain("comment");
        fields.ShouldNotContain("question");

        var filtered = await viewer.GetFromJsonAsync<JsonElement>($"/api/feedback?assistantId={seed.Assistant}&rating=-1&outcome=answered", Ct);
        filtered.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([seed.Down]);
        var paged = await viewer.GetFromJsonAsync<JsonElement>($"/api/feedback?assistantId={seed.Assistant}&limit=1", Ct);
        paged.GetProperty("next").GetGuid().ShouldBe(seed.Up);
        var second = await viewer.GetFromJsonAsync<JsonElement>($"/api/feedback?assistantId={seed.Assistant}&limit=1&after={seed.Up}", Ct);
        second.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(seed.Down);

        var summary = await viewer.GetFromJsonAsync<JsonElement>($"/api/feedback/summary?assistantId={seed.Assistant}", Ct);
        var totals = summary.GetProperty("totals");
        (totals.GetProperty("answers").GetInt32(), totals.GetProperty("notFound").GetInt32(), totals.GetProperty("thumbsUp").GetInt32(), totals.GetProperty("thumbsDown").GetInt32())
            .ShouldBe((2, 1, 1, 1));
        var import = summary.GetProperty("imports").EnumerateArray().Single(i => i.GetProperty("clientId").GetGuid() == seed.Client);
        (import.GetProperty("lastOutcome").GetString(), import.GetProperty("unmapped").GetInt32()).ShouldBe(("ok", 2));

        var foreign = await stranger.GetFromJsonAsync<JsonElement>($"/api/feedback?assistantId={seed.Assistant}", Ct);
        foreign.GetProperty("items").GetArrayLength().ShouldBe(0);
        var foreignSummary = await stranger.GetFromJsonAsync<JsonElement>("/api/feedback/summary", Ct);
        foreignSummary.GetProperty("imports").EnumerateArray().Select(i => i.GetProperty("clientId").GetGuid()).ShouldNotContain(seed.Client);
    }

    [Theory]
    [InlineData(RagRoles.Editor, HttpStatusCode.Forbidden)]
    [InlineData(RagRoles.PipelineEditor, HttpStatusCode.Forbidden)]
    [InlineData(RagRoles.SkillEditor, HttpStatusCode.Forbidden)]
    [InlineData(RagRoles.Viewer, HttpStatusCode.OK)]
    [InlineData(RagRoles.Reviewer, HttpStatusCode.OK)]
    public async Task The_list_and_totals_are_the_feedback_pages_roles_only(string role, HttpStatusCode expected)
    {
        // Admin design §3.3: Feedback is the platform and department admins', the reviewers' and the viewers'.
        var seed = await SeedAsync();
        using var user = await AdminDatabaseFixture.UserAsync(host.Factory, role, seed.Code);
        using (var list = await user.GetAsync($"/api/feedback?assistantId={seed.Assistant}", Ct))
        {
            list.StatusCode.ShouldBe(expected);
        }

        using var summary = await user.GetAsync($"/api/feedback/summary?assistantId={seed.Assistant}", Ct);
        summary.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Marking_an_item_handled_is_feedback_comments_and_audited()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);
        var path = $"/api/feedback/{seed.Down}/review-state";

        (await viewer.PutAsJsonAsync(path, new { state = "reviewed" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.PutAsJsonAsync(path, new { state = "reviewed" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync(path, new { state = "closed" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (var marked = await admin.PutAsJsonAsync(path, new { state = "reviewed" }, Ct))
        {
            marked.StatusCode.ShouldBe(HttpStatusCode.OK, await marked.Content.ReadAsStringAsync(Ct));
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Feedback.AsNoTracking().Where(f => f.Id == seed.Down).Select(f => f.ReviewState).SingleAsync(Ct)).ShouldBe("reviewed");
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "feedback" && a.EntityId == seed.Down.ToString()).SingleAsync(Ct);
        audit.Action.ShouldBe("review_state");
        var reviewed = await admin.GetFromJsonAsync<JsonElement>($"/api/feedback?assistantId={seed.Assistant}&state=reviewed", Ct);
        reviewed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([seed.Down]);
    }
}
