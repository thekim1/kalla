using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Evaluation (admin design §4.5, §4.7; P1a item 14d): human questions through <c>edit-eval-question</c>, on-demand runs with
/// variants, the set's overview and a run's results; and member groups by hand (admin design §4.10).
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class EvaluationEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private sealed record Seed(string Code, Guid Department, Guid Collection, Guid Document, Guid Generated);

    /// <summary>A department with a collection, a source with one live document, and an evaluation set holding one generated question.</summary>
    private async Task<Seed> SeedAsync(bool withSet = true)
    {
        var code = "V" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Eval " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "eval-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "eval-" + code.ToLowerInvariant(), Name = "Utvärdering " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Value, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "eval-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source { CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Public, PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow };
        var url = $"https://www.umea.se/{code}";
        var document = new Document
        {
            SourceId = source.Id, Classification = Classification.Public, IdentityKind = "url", CanonicalIdentity = url, IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(url)),
        };
        db.AddRange(department, collection, pipeline, source, document);
        var generated = Guid.CreateVersion7();
        if (withSet)
        {
            var set = new EvaluationSet { CollectionId = collection.Id, Classification = Classification.Public, CreatedAt = DateTimeOffset.UtcNow };
            db.EvaluationSets.Add(set);
            db.EvaluationQuestions.Add(new EvaluationQuestion
            {
                Id = generated, SetId = set.Id, Classification = Classification.Public, Layer = EvaluationLayers.Generated, Question = "Vad kostar bygglov?",
            });
        }

        await db.SaveChangesAsync(Ct);
        return new Seed(code, department.Id, collection.Id, document.Id, generated);
    }

    [Fact]
    public async Task Human_questions_are_edit_commands_and_runs_are_enqueued_once_with_their_variants()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);
        var path = $"/api/collections/{seed.Collection}/evaluation";

        using (var created = await admin.PostAsJsonAsync($"{path}/questions",
                   new { question = "Hur ansöker jag om bygglov?", expectedAnswer = "Via e-tjänsten.", expectedDocumentIds = new[] { seed.Document } }, Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync(Ct));
        }

        (await admin.PostAsJsonAsync($"{path}/questions", new { question = "Fel dokument", expectedDocumentIds = new[] { Guid.CreateVersion7() } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (var generated = await admin.PutAsJsonAsync($"{path}/questions/{seed.Generated}", new { question = "Ändrad" }, Ct))
        {
            generated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(generated)).GetProperty("code").GetString().ShouldBe("question_not_human");
        }

        (await stranger.PostAsJsonAsync($"{path}/questions", new { question = "Annan förvaltning" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var db = TestPostgres.Context(database.Connection(null));
        var command = await db.Jobs.AsNoTracking().Where(j => j.Class == JobClasses.Command && j.DepartmentId == seed.Department).SingleAsync(Ct);
        using (var payload = JsonDocument.Parse(command.Payload))
        {
            (payload.RootElement.GetProperty("kind").GetString(), payload.RootElement.GetProperty("action").GetString(), payload.RootElement.GetProperty("collectionId").GetGuid())
                .ShouldBe(("edit-eval-question", "create", seed.Collection));
            payload.RootElement.GetProperty("expectedDocumentIds")[0].GetGuid().ShouldBe(seed.Document);
            Ume.Rag.Pipelines.Schemas.ContractSchemas.Validator(typeof(Ume.Rag.Pipelines.Jobs.EditEvalQuestionCommand)).Validate(payload.RootElement).ShouldBeEmpty();
        }

        // Runs: one eval on-demand job with the three variants; a second request while it is queued answers it.
        using (var run = await admin.PostAsJsonAsync($"{path}/run", new { variants = new[] { "Hybrid", "Vector", "HybridNoDecompounder" } }, Ct))
        {
            run.StatusCode.ShouldBe(HttpStatusCode.Accepted, await run.Content.ReadAsStringAsync(Ct));
            (await Json(run)).GetProperty("alreadyQueued").GetBoolean().ShouldBeFalse();
        }

        using (var again = await admin.PostAsJsonAsync($"{path}/run", new { }, Ct))
        {
            (await Json(again)).GetProperty("alreadyQueued").GetBoolean().ShouldBeTrue();
        }

        var eval = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"eval:{seed.Collection}", Ct);
        using (var payload = JsonDocument.Parse(eval.Payload))
        {
            payload.RootElement.GetProperty("variants").EnumerateArray().Select(v => v.GetString()).ShouldBe(["hybrid", "vector", "hybrid-no-decompounder"]);
        }

        // The worker's run and results are read back with the compared run's rank.
        var set = await db.EvaluationSets.AsNoTracking().SingleAsync(s => s.CollectionId == seed.Collection, Ct);
        var first = new EvaluationRun { SetId = set.Id, Classification = "public", Kind = "on-demand", StartedAt = DateTimeOffset.UtcNow.AddHours(-1), RecallAtK = 0.5m };
        var second = new EvaluationRun { SetId = set.Id, Classification = "public", Kind = "on-demand", StartedAt = DateTimeOffset.UtcNow, RecallAtK = 1m, ComparedRunId = first.Id };
        db.EvaluationRuns.AddRange(first, second);
        await db.SaveChangesAsync(Ct);
        db.EvaluationResults.AddRange(
            new EvaluationResult { RunId = first.Id, QuestionId = seed.Generated, Classification = "public", ExpectedRank = 4, Hit = true },
            new EvaluationResult { RunId = second.Id, QuestionId = seed.Generated, Classification = "public", ExpectedRank = 1, Hit = true });
        await db.SaveChangesAsync(Ct);

        var overview = await admin.GetFromJsonAsync<JsonElement>(path, Ct);
        overview.GetProperty("hasSet").GetBoolean().ShouldBeTrue();
        overview.GetProperty("latestRuns")[0].GetProperty("id").GetGuid().ShouldBe(second.Id);
        var result = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/evaluation-runs/{second.Id}/results", Ct))!.ShouldHaveSingleItem();
        (result.GetProperty("rank").GetInt32(), result.GetProperty("previousRank").GetInt32()).ShouldBe((1, 4));
        (await admin.GetFromJsonAsync<JsonElement>($"{path}/questions?layer=generated", Ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await stranger.GetAsync($"/api/evaluation-runs/{second.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_run_needs_a_set_and_member_groups_are_set_by_platform_admins()
    {
        var seed = await SeedAsync(withSet: false);
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using (var run = await admin.PostAsJsonAsync($"/api/collections/{seed.Collection}/evaluation/run", new { }, Ct))
        {
            run.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(run)).GetProperty("code").GetString().ShouldBe("no_evaluation_set");
        }

        (await admin.PutAsJsonAsync($"/api/organisation/departments/{seed.Department}/member-groups", new { memberGroups = new[] { "g" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var set = await platform.PutAsJsonAsync($"/api/organisation/departments/{seed.Department}/member-groups",
            new { memberGroups = new[] { " SG-Bygg ", "SG-Bygg", "SG-Admin" } }, Ct);
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        (await Json(set)).GetProperty("memberGroups").EnumerateArray().Select(g => g.GetString()).ShouldBe(["SG-Admin", "SG-Bygg"]);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == seed.Department, Ct)).MemberGroups.ShouldBe(["SG-Admin", "SG-Bygg"]);
        (await db.AuditLog.AsNoTracking().CountAsync(a => a.EntityType == "department" && a.EntityId == seed.Department.ToString() && a.Action == "member-groups", Ct)).ShouldBe(1);
    }
}
