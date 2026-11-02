using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The setup checklist "Kom igång" (admin design §5.4, §5.5.2) computed from state, and the overview listing the
/// collections whose checklist is not done (§5.5.1).
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class SetupEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid Fresh, Guid Halfway, Guid Ready, Guid Assistant, Guid RunningRun);

    /// <summary>
    /// Three collections of one department: <c>Fresh</c> (nothing yet); <c>Halfway</c> (a source with a finished and a
    /// running run and two documents of which one is deleted, two active questions and a retired one, an evaluation job queued, a draft assistant); <c>Ready</c>
    /// (published, linked by a published assistant with a client audience).
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var code = "K" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Kom igång " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "setup-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        Collection Make(string name, DateTimeOffset? published = null) => new()
        {
            Slug = $"setup-{name}-{code.ToLowerInvariant()}", Name = $"{name} {code}", DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile.Value, PublishedAt = published, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var fresh = Make("Ny");
        var halfway = Make("Halvvägs");
        var ready = Make("Klar", DateTimeOffset.UtcNow);
        var pipeline = new Pipeline { Name = "setup-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = halfway.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var removed = new Source
        {
            CollectionId = fresh.Id, Type = SourceTypes.UrlList, Name = "Borttagen", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow, DeletedAt = DateTimeOffset.UtcNow,
        };
        var now = DateTimeOffset.UtcNow;
        var set = new EvaluationSet { CollectionId = halfway.Id, Classification = Classification.Internal, CreatedAt = now };
        var client = new Client { Name = "Open WebUI " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
        var draftOnly = new Assistant { Slug = "setup-a-" + code.ToLowerInvariant(), Name = "Utkast " + code, DepartmentId = department.Id, Classification = Classification.Internal, UpdatedAt = now };
        var live = new Assistant { Slug = "setup-b-" + code.ToLowerInvariant(), Name = "Klar " + code, DepartmentId = department.Id, Classification = Classification.Internal, UpdatedAt = now };
        var elsewhere = new Department { Id = Guid.CreateVersion7(), Name = "Annan " + code, CostCenterCode = "Y" + code, SyncedAt = now };
        var foreign = new Assistant
        {
            Slug = "setup-c-" + code.ToLowerInvariant(), Name = "Annans " + code, DepartmentId = elsewhere.Id, Classification = Classification.Internal, UpdatedAt = now,
        };
        db.AddRange(department, elsewhere, fresh, halfway, ready, pipeline, source, removed, set, client, draftOnly, live, foreign);
        var running = new Run { SourceId = source.Id, Classification = Classification.Internal, Kind = "sync", Trigger = "schedule", State = RunStates.Running, StartedAt = now };
        db.AddRange(
            new Run { SourceId = removed.Id, Classification = Classification.Internal, Kind = "sync", Trigger = "manual", State = RunStates.Succeeded, StartedAt = now },
            new Run { SourceId = source.Id, Classification = Classification.Internal, Kind = "sync", Trigger = "manual", State = RunStates.Succeeded, StartedAt = now.AddHours(-1) },
            running);
        Document Page(string path, DateTimeOffset? deleted = null)
        {
            var url = $"https://www.umea.se/{code}/{path}";
            return new Document
            {
                SourceId = source.Id, Classification = Classification.Internal, IdentityKind = "url", CanonicalIdentity = url, Url = url,
                IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(url)), DeletedAt = deleted,
            };
        }

        db.AddRange(Page("altan"), Page("borttagen", now));
        db.AddRange(
            new EvaluationQuestion { SetId = set.Id, Classification = Classification.Internal, Layer = EvaluationLayers.Human, Question = "Fråga ett?" },
            new EvaluationQuestion { SetId = set.Id, Classification = Classification.Internal, Layer = EvaluationLayers.Generated, Question = "Fråga två?" },
            new EvaluationQuestion
            {
                SetId = set.Id, Classification = Classification.Internal, Layer = EvaluationLayers.Human, Question = "Borttagen?", State = EvaluationQuestionStates.Retired,
                RetiredAt = now,
            });
        db.Jobs.Add(new Job
        {
            Class = JobClasses.Eval, DepartmentId = department.Id, Classification = Classification.Internal, DedupKey = JobDedupKeys.Eval(halfway.Id),
            Payload = $$"""{"kind":"on-demand","collectionId":"{{halfway.Id}}"}""", EnqueuedAt = now, State = JobStates.Queued,
        });
        await db.SaveChangesAsync(Ct);
        var draft = new AssistantVersion
        {
            AssistantId = draftOnly.Id, Number = 1, GatewayModel = "ume/chat-eu", CollectionLinks = [CollectionLink.Whole(halfway.Id)], CreatedAt = now, UpdatedAt = now,
        };
        var published = new AssistantVersion
        {
            AssistantId = live.Id, Number = 1, State = AssistantVersionStates.Published, GatewayModel = "ume/chat-eu", CollectionLinks = [CollectionLink.Whole(ready.Id)],
            PublishedAt = now, PublishedBy = "seed", CreatedAt = now, UpdatedAt = now,
        };
        // Another department's published, available assistant linking Fresh (a shared collection): not this checklist's.
        var foreignVersion = new AssistantVersion
        {
            AssistantId = foreign.Id, Number = 1, State = AssistantVersionStates.Published, GatewayModel = "ume/chat-eu", CollectionLinks = [CollectionLink.Whole(fresh.Id)],
            PublishedAt = now, PublishedBy = "seed", CreatedAt = now, UpdatedAt = now,
        };
        db.AddRange(draft, published, foreignVersion, new AssistantAudience { AssistantId = live.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id },
            new AssistantAudience { AssistantId = foreign.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id });
        await db.SaveChangesAsync(Ct);
        draftOnly.DraftVersionId = draft.Id;
        live.PublishedVersionId = published.Id;
        foreign.PublishedVersionId = foreignVersion.Id;
        await db.SaveChangesAsync(Ct);
        return new Seed(code, fresh.Id, halfway.Id, ready.Id, live.Id, running.Id);
    }

    [Fact]
    public async Task The_checklist_is_computed_from_state_and_the_overview_lists_only_unfinished_collections()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);

        var fresh = await editor.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Fresh}/setup", Ct);
        (fresh.GetProperty("sources").GetInt32(), fresh.GetProperty("firstSyncDone").GetBoolean(), fresh.GetProperty("done").GetBoolean())
            .ShouldBe((0, false, false)); // the deleted source and its run don't count
        // …nor another department's assistant linking it.
        (fresh.GetProperty("assistantId").ValueKind, fresh.GetProperty("available").GetBoolean()).ShouldBe((JsonValueKind.Null, false));

        var halfway = await editor.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Halfway}/setup", Ct);
        halfway.GetProperty("sources").GetInt32().ShouldBe(1);
        halfway.GetProperty("firstSyncDone").GetBoolean().ShouldBeTrue();
        halfway.GetProperty("syncRunning").GetBoolean().ShouldBeTrue();
        halfway.GetProperty("testQuestions").GetInt32().ShouldBe(2);
        (halfway.GetProperty("evaluated").GetBoolean(), halfway.GetProperty("evaluating").GetBoolean()).ShouldBe((false, true));
        halfway.GetProperty("published").GetBoolean().ShouldBeFalse();
        halfway.GetProperty("assistantId").ValueKind.ShouldBe(JsonValueKind.String);
        (halfway.GetProperty("assistantPublished").GetBoolean(), halfway.GetProperty("available").GetBoolean()).ShouldBe((false, false));

        var ready = await editor.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Ready}/setup", Ct);
        ready.GetProperty("assistantId").GetGuid().ShouldBe(seed.Assistant);
        (ready.GetProperty("published").GetBoolean(), ready.GetProperty("available").GetBoolean(), ready.GetProperty("done").GetBoolean()).ShouldBe((true, true, true));

        var overview = await editor.GetFromJsonAsync<JsonElement>("/api/overview", Ct);
        overview.GetProperty("hasCollections").GetBoolean().ShouldBeTrue();
        var listed = overview.GetProperty("setup").EnumerateArray().Select(s => s.GetProperty("collectionId").GetGuid()).ToList();
        listed.ShouldContain(seed.Fresh);
        listed.ShouldContain(seed.Halfway);
        listed.ShouldNotContain(seed.Ready);

        (await stranger.GetAsync($"/api/collections/{seed.Halfway}/setup", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var other = await stranger.GetFromJsonAsync<JsonElement>("/api/overview", Ct);
        other.GetProperty("setup").EnumerateArray().Select(s => s.GetProperty("collectionId").GetGuid()).ShouldNotContain(seed.Halfway);
    }

    [Fact]
    public async Task The_source_list_carries_each_source_s_live_document_count_and_latest_run()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);

        var source = (await viewer.GetFromJsonAsync<JsonElement[]>($"/api/collections/{seed.Halfway}/sources", Ct))!.ShouldHaveSingleItem();
        source.GetProperty("documents").GetInt32().ShouldBe(1);
        var lastRun = source.GetProperty("lastRun");
        (lastRun.GetProperty("id").GetGuid(), lastRun.GetProperty("state").GetString()).ShouldBe((seed.RunningRun, RunStates.Running));
        lastRun.GetProperty("finishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
