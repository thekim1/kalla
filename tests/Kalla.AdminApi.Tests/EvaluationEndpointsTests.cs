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
/// Evaluation (admin design §4.5, §4.7; P1a item 14d): human questions through <c>edit-eval-question</c>, on-demand runs with
/// variants, the set's overview and a run's results; and member groups by hand (admin design §4.10).
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class EvaluationEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
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
        var factory = host.Factory;
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

        // The worker writes the created question; a second edit of it while the first waits is refused.
        await using (var seedDb = TestPostgres.Context(database.Connection(null)))
        {
            var setId = await seedDb.EvaluationSets.Where(x => x.CollectionId == seed.Collection).Select(x => x.Id).SingleAsync(Ct);
            var human = Guid.CreateVersion7();
            seedDb.EvaluationQuestions.Add(new EvaluationQuestion { Id = human, SetId = setId, Classification = "public", Layer = EvaluationLayers.Human, Question = "Mänsklig fråga?" });
            await seedDb.SaveChangesAsync(Ct);
            (await admin.PutAsJsonAsync($"{path}/questions/{human}", new { question = "Första ändringen?" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            using var pending = await admin.PutAsJsonAsync($"{path}/questions/{human}", new { question = "Andra ändringen?" }, Ct);
            pending.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(pending)).GetProperty("code").GetString().ShouldBe("question_edit_pending");
            await seedDb.Database.ExecuteSqlAsync($"""UPDATE content."Jobs" SET "State" = 'succeeded' WHERE "Payload"::jsonb ->> 'questionId' = {human.ToString("D")}""", Ct);
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        var command = await db.Jobs.AsNoTracking().Where(j => j.Class == JobClasses.Command && j.DepartmentId == seed.Department).OrderBy(j => j.EnqueuedAt).FirstAsync(Ct);
        using (var payload = JsonDocument.Parse(command.Payload))
        {
            (payload.RootElement.GetProperty("kind").GetString(), payload.RootElement.GetProperty("action").GetString(), payload.RootElement.GetProperty("collectionId").GetGuid())
                .ShouldBe(("edit-eval-question", "create", seed.Collection));
            payload.RootElement.GetProperty("expectedDocumentIds")[0].GetGuid().ShouldBe(seed.Document);
            Kalla.Pipelines.Schemas.ContractSchemas.Validator(typeof(Kalla.Pipelines.Jobs.EditEvalQuestionCommand)).Validate(payload.RootElement).ShouldBeEmpty();
        }

        // Runs: one eval on-demand job with the three variants (the contract's names; the enum names are read too); a second
        // request while it is queued answers it.
        using (var run = await admin.PostAsJsonAsync($"{path}/run", new { variants = new[] { "hybrid", "Vector", "hybrid-no-decompounder" } }, Ct))
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
        // The compared run's recall, for the Quality dot's "worse" (Home's rule, #1049); none for a run compared with nothing.
        overview.GetProperty("latestRuns")[0].GetProperty("comparedRecallAtK").GetDecimal().ShouldBe(0.5m);
        overview.GetProperty("latestRuns")[1].GetProperty("comparedRecallAtK").ValueKind.ShouldBe(JsonValueKind.Null);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/evaluation-runs/{second.Id}", Ct)).GetProperty("comparedRecallAtK").GetDecimal().ShouldBe(0.5m);
        var result = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/evaluation-runs/{second.Id}/results", Ct))!.ShouldHaveSingleItem();
        (result.GetProperty("rank").GetInt32(), result.GetProperty("previousRank").GetInt32()).ShouldBe((1, 4));
        (await admin.GetFromJsonAsync<JsonElement>($"{path}/questions?layer=generated", Ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await stranger.GetAsync($"/api/evaluation-runs/{second.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_trend_lists_the_latest_finished_runs_oldest_first_with_recall_and_mrr_per_layer_human_first()
    {
        // §7.1 (B-69): per layer from the results, as the worker scores (answerable questions only); no candidate or unfinished run.
        var seed = await SeedAsync();
        await using var db = TestPostgres.Context(database.Connection(null));
        var set = await db.EvaluationSets.AsNoTracking().SingleAsync(s => s.CollectionId == seed.Collection, Ct);
        Guid human = Guid.CreateVersion7(), unanswerable = Guid.CreateVersion7();
        db.EvaluationQuestions.AddRange(
            new EvaluationQuestion { Id = human, SetId = set.Id, Classification = Classification.Public, Layer = EvaluationLayers.Human, Question = "Hur söker jag bygglov?" },
            new EvaluationQuestion { Id = unanswerable, SetId = set.Id, Classification = Classification.Public, Layer = EvaluationLayers.Human, Question = "Vad heter månen?" });
        var now = DateTimeOffset.UtcNow;
        EvaluationRun Run(int hoursAgo, bool finished = true, bool candidate = false) => new()
        {
            SetId = set.Id, Classification = "public", Kind = "nightly", StartedAt = now.AddHours(-hoursAgo), FinishedAt = finished ? now.AddHours(-hoursAgo).AddMinutes(5) : null,
            Candidate = candidate, RecallAtK = 0.5m, Mrr = 0.25m, QuestionCount = 2,
        };
        var (older, newer, cand, running) = (Run(48), Run(24), Run(12, candidate: true), Run(1, finished: false));
        db.EvaluationRuns.AddRange(older, newer, cand, running);
        await db.SaveChangesAsync(Ct);
        EvaluationResult Result(EvaluationRun run, Guid question, int? rank, bool? hit) =>
            new() { RunId = run.Id, QuestionId = question, Classification = "public", ExpectedRank = rank, Hit = hit };
        db.EvaluationResults.AddRange(
            Result(older, human, null, false), Result(older, seed.Generated, 4, true), Result(older, unanswerable, null, null),
            Result(newer, human, 2, true), Result(newer, seed.Generated, 1, true), Result(newer, unanswerable, null, null));
        await db.SaveChangesAsync(Ct);
        using var viewer = await AdminDatabaseFixture.UserAsync(database.HeaderFactory(), RagRoles.Viewer, seed.Code);

        var runs = (await viewer.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/evaluation/trend", Ct)).GetProperty("runs").EnumerateArray().ToList();
        runs.Select(r => r.GetProperty("id").GetGuid()).ShouldBe([older.Id, newer.Id]);
        var layers = runs[1].GetProperty("layers").EnumerateArray()
            .Select(l => (l.GetProperty("layer").GetString(), l.GetProperty("questions").GetInt32(), l.GetProperty("recallAtK").GetDecimal(), l.GetProperty("mrr").GetDecimal())).ToList();
        layers.ShouldBe([("human", 1, 1m, 0.5m), ("generated", 1, 1m, 1m)]);
        runs[0].GetProperty("layers")[0].GetProperty("recallAtK").GetDecimal().ShouldBe(0m);
        runs[0].GetProperty("layers")[1].GetProperty("mrr").GetDecimal().ShouldBe(0.25m);
        (await viewer.GetAsync($"/api/collections/{Guid.CreateVersion7()}/evaluation/trend", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_run_needs_a_set()
    {
        var seed = await SeedAsync(withSet: false);
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using (var run = await admin.PostAsJsonAsync($"/api/collections/{seed.Collection}/evaluation/run", new { }, Ct))
        {
            run.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(run)).GetProperty("code").GetString().ShouldBe("no_evaluation_set");
        }
    }

    [Fact]
    public async Task Generation_is_forecast_then_enqueued_once_and_needs_the_evaluation_model()
    {
        var seed = await SeedAsync();
        await using var db = TestPostgres.Context(database.Connection(null));
        var collection = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.Collection, Ct);
        var pipeline = await db.Sources.AsNoTracking().Where(s => s.CollectionId == seed.Collection).Select(s => s.PipelineId).SingleAsync(Ct);
        var version = new PipelineVersion { PipelineId = pipeline, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
        db.Add(version);
        await db.SaveChangesAsync(Ct);
        var chunkSet = new ChunkSet
        {
            DocumentId = seed.Document, Classification = Classification.Public, CollectionId = seed.Collection, PipelineVersionId = version.Id,
            EmbeddingProfileId = collection.EmbeddingProfileId, ContentHash = RandomNumberGenerator.GetBytes(32), InputHash = RandomNumberGenerator.GetBytes(32),
            State = ChunkSetStates.Live, CreatedAt = DateTimeOffset.UtcNow, LiveAt = DateTimeOffset.UtcNow,
        };
        db.Add(chunkSet);
        await db.SaveChangesAsync(Ct);
        await db.Documents.Where(d => d.Id == seed.Document).ExecuteUpdateAsync(u => u.SetProperty(d => d.LiveChunkSetId, chunkSet.Id), Ct);
        // Two eligible chunks of 300 tokens; one too short.
        foreach (var (ordinal, tokens) in new[] { (0, 300), (1, 300), (2, 10) })
        {
            db.Add(new Chunk
            {
                ChunkSetId = chunkSet.Id, DocumentId = seed.Document, CollectionId = seed.Collection, Classification = Classification.Public, Ordinal = ordinal,
                Text = "Bygglov " + ordinal, SearchText = "bygglov", TextHash = RandomNumberGenerator.GetBytes(32), TokenCount = tokens,
            });
        }

        await db.SaveChangesAsync(Ct);
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        var path = $"/api/collections/{seed.Collection}/evaluation/generate";

        // N = min(100, 3 × 1 document, 2 eligible) = 2, one generated question active: 1 needed, ⌈1.6⌉ = 2 candidates, 4 calls,
        // 2 × (2 × 300 + 400) input + 2 × 120 output tokens. The collection has a generated question, so it is a top-up.
        var forecast = await viewer.GetFromJsonAsync<JsonElement>(path, Ct);
        (forecast.GetProperty("kind").GetString(), forecast.GetProperty("liveDocuments").GetInt32(), forecast.GetProperty("eligibleChunks").GetInt32(),
                forecast.GetProperty("activeQuestions").GetInt32(), forecast.GetProperty("target").GetInt32(), forecast.GetProperty("needed").GetInt32(),
                forecast.GetProperty("modelCalls").GetInt32(), forecast.GetProperty("estimatedTokens").GetInt64())
            .ShouldBe(("top-up-set", 1, 2, 1, 2, 1, 4, 2240L));
        (await viewer.PostAsJsonAsync(path, new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The platform settings row is a singleton other tests may share: put back what was there.
        var settings = await db.Settings.AsNoTracking().SingleOrDefaultAsync(Ct);
        if (settings is null)
        {
            db.Settings.Add(new PlatformSettings());
            await db.SaveChangesAsync(Ct);
        }

        var model = "eval-" + seed.Code.ToLowerInvariant();
        try
        {
            if (settings?.EvaluationModel is not null)
            {
                await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.EvaluationModel, (string?)null), Ct);
            }

            using (var unset = await admin.PostAsJsonAsync(path, new { }, Ct))
            {
                unset.StatusCode.ShouldBe(HttpStatusCode.Conflict, await unset.Content.ReadAsStringAsync(Ct));
                (await Json(unset)).GetProperty("code").GetString().ShouldBe("evaluation_model_unset");
            }

            db.GatewayModels.Add(new GatewayModel { Name = model, Residencies = ["OnPrem"], InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m });
            await db.SaveChangesAsync(Ct);
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.EvaluationModel, model).SetProperty(x => x.EvaluationJudgeModel, (string?)null), Ct);

            using (var started = await admin.PostAsJsonAsync(path, new { }, Ct))
            {
                started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
                var body = await Json(started);
                body.GetProperty("alreadyQueued").GetBoolean().ShouldBeFalse();
                // 2000 input tokens at 1 USD and 240 output tokens at 2 USD per million.
                body.GetProperty("forecast").GetProperty("estimatedCostUsd").GetDecimal().ShouldBe(0.0025m);
            }

            using (var again = await admin.PostAsJsonAsync(path, new { }, Ct))
            {
                (await Json(again)).GetProperty("alreadyQueued").GetBoolean().ShouldBeTrue();
            }

            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"eval-generate:{seed.Collection}", Ct);
            using var payload = JsonDocument.Parse(job.Payload);
            payload.RootElement.GetProperty("kind").GetString().ShouldBe("top-up-set");
            Kalla.Pipelines.Schemas.ContractSchemas.Validator(typeof(Kalla.Pipelines.Jobs.EvalTopUpSetPayload)).Validate(payload.RootElement).ShouldBeEmpty();
            (await db.AuditLog.AsNoTracking().CountAsync(a => a.Action == "evaluation-generate" && a.EntityId == seed.Collection.ToString(), Ct)).ShouldBe(2);
        }
        finally
        {
            if (settings is null)
            {
                await db.Settings.ExecuteDeleteAsync(Ct);
            }
            else
            {
                await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.EvaluationModel, settings.EvaluationModel)
                    .SetProperty(x => x.EvaluationJudgeModel, settings.EvaluationJudgeModel), Ct);
            }
        }
    }

    [Fact]
    public async Task Candidate_settings_are_checked_and_compared_and_runs_carry_the_threshold_proposal()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        var path = $"/api/collections/{seed.Collection}/evaluation/run";

        using (var invalid = await admin.PostAsJsonAsync(path, new { candidateSettings = new { candidates = 5 } }, Ct))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(invalid)).GetProperty("errorCodes").GetProperty("settings.candidates")[0].GetProperty("code").GetString().ShouldBe("retrieval_settings_invalid");
        }

        using (var run = await admin.PostAsJsonAsync(path, new { candidateSettings = new { vectorWeight = 2, recency = new { halfLifeDays = 365, weight = 0.2, kinds = new[] { "pdf" } } } }, Ct))
        {
            run.StatusCode.ShouldBe(HttpStatusCode.Accepted, await run.Content.ReadAsStringAsync(Ct));
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"eval:{seed.Collection}", Ct);
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            var candidate = payload.RootElement.GetProperty("candidateSettings");
            (candidate.GetProperty("vectorWeight").GetDouble(), candidate.GetProperty("recency").GetProperty("halfLifeDays").GetInt32()).ShouldBe((2d, 365));
            Kalla.Pipelines.Schemas.ContractSchemas.Validator(typeof(Kalla.Pipelines.Jobs.EvalOnDemandPayload)).Validate(payload.RootElement).ShouldBeEmpty();
        }

        var set = await db.EvaluationSets.AsNoTracking().SingleAsync(s => s.CollectionId == seed.Collection, Ct);
        var proposal = """{"answerable": 8, "unanswerable": 4, "falseNotFound": 1, "unanswerableNotFound": 3, "coverageMaxDistance": {"value": 0.42, "misclassified": 1}}""";
        var run2 = new EvaluationRun { SetId = set.Id, Classification = "public", Kind = "on-demand", StartedAt = DateTimeOffset.UtcNow, Candidate = true, ThresholdProposal = proposal };
        db.EvaluationRuns.Add(run2);
        await db.SaveChangesAsync(Ct);
        var dto = await admin.GetFromJsonAsync<JsonElement>($"/api/evaluation-runs/{run2.Id}", Ct);
        dto.GetProperty("candidate").GetBoolean().ShouldBeTrue();
        var max = dto.GetProperty("thresholdProposal").GetProperty("coverageMaxDistance");
        (max.GetProperty("value").GetDouble(), max.GetProperty("misclassified").GetInt32()).ShouldBe((0.42, 1));
        var latest = (await admin.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/evaluation", Ct)).GetProperty("latestRuns")[0];
        latest.GetProperty("thresholdProposal").GetProperty("answerable").GetInt32().ShouldBe(8);
    }

    /// <summary>
    /// A question whose expected document was deleted (processing design §13.3, admin design §7.1, §7.2; #893): the human one is
    /// <c>stale</c> without <c>RetiredAt</c>, the generated one retired; each run carries its job result's <c>documentGone</c>
    /// (eval publish-report/nightly top-level, on-demand <c>runs[]</c>), and null once the job is gone.
    /// </summary>
    [Fact]
    public async Task Stale_human_questions_and_each_runs_document_gone_count_are_shown()
    {
        var seed = await SeedAsync();
        using var admin = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.DepartmentAdmin, seed.Code);
        var path = $"/api/collections/{seed.Collection}/evaluation";
        await using var db = TestPostgres.Context(database.Connection(null));
        var set = await db.EvaluationSets.AsNoTracking().SingleAsync(s => s.CollectionId == seed.Collection, Ct);
        var human = new EvaluationQuestion
        {
            SetId = set.Id, Classification = "public", Layer = EvaluationLayers.Human, Question = "Var finns blanketten?", State = EvaluationQuestionStates.Stale,
            ExpectedDocumentIds = [seed.Document],
        };
        db.EvaluationQuestions.Add(human);
        await db.EvaluationQuestions.Where(q => q.Id == seed.Generated)
            .ExecuteUpdateAsync(u => u.SetProperty(q => q.State, EvaluationQuestionStates.Stale).SetProperty(q => q.RetiredAt, DateTimeOffset.UtcNow), Ct);

        var now = DateTimeOffset.UtcNow;
        var nightly = new EvaluationRun { SetId = set.Id, Classification = "public", Kind = "nightly", StartedAt = now.AddMinutes(-30), FinishedAt = now.AddMinutes(-29) };
        var onDemand = new EvaluationRun { SetId = set.Id, Classification = "public", Kind = "on-demand", StartedAt = now.AddMinutes(-10), FinishedAt = now.AddMinutes(-9) };
        var old = new EvaluationRun { SetId = set.Id, Classification = "public", Kind = "nightly", StartedAt = now.AddDays(-9), FinishedAt = now.AddDays(-9) };
        db.EvaluationRuns.AddRange(nightly, onDemand, old);
        Job Succeeded(object result, DateTimeOffset completed) => new()
        {
            Class = JobClasses.Eval, DepartmentId = seed.Department, Classification = "public", State = JobStates.Succeeded, EnqueuedAt = completed, AvailableAt = completed,
            CompletedAt = completed, Payload = $$"""{"kind": "nightly", "collectionId": "{{seed.Collection}}"}""", Result = Kalla.Pipelines.Json.ContractJson.Serialize(result),
        };
        db.Jobs.AddRange(
            Succeeded(new Kalla.Pipelines.Jobs.EvalRunResult { EvaluationRunId = nightly.Id, QuestionCount = 4, Found = 3, DocumentGone = 2 }, nightly.FinishedAt.Value),
            Succeeded(new Kalla.Pipelines.Jobs.EvalOnDemandResult
            {
                Runs = [new Kalla.Pipelines.Jobs.EvaluationRunRef { Variant = Kalla.Pipelines.Jobs.EvaluationVariant.Hybrid, EvaluationRunId = onDemand.Id, QuestionCount = 5, Found = 4, DocumentGone = 1 }],
            }, onDemand.FinishedAt.Value));
        await db.SaveChangesAsync(Ct);

        // The questions table: the human stale question is not retired, the generated one is.
        var stale = (await admin.GetFromJsonAsync<JsonElement>($"{path}/questions?state=stale", Ct)).GetProperty("items").EnumerateArray()
            .ToDictionary(q => q.GetProperty("layer").GetString()!);
        (stale["human"].GetProperty("id").GetGuid(), stale["human"].GetProperty("retiredAt").ValueKind).ShouldBe((human.Id, JsonValueKind.Null));
        stale["generated"].GetProperty("retiredAt").ValueKind.ShouldBe(JsonValueKind.String);
        var counts = (await admin.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("questions").EnumerateArray()
            .Select(c => (c.GetProperty("layer").GetString(), c.GetProperty("state").GetString(), c.GetProperty("count").GetInt32())).ToList();
        counts.ShouldContain(("human", "stale", 1));

        // The report card's count: from each run's job result; a run past job retention has none.
        var runs = (await admin.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("latestRuns").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("documentGone"));
        runs[nightly.Id].GetInt32().ShouldBe(2);
        runs[onDemand.Id].GetInt32().ShouldBe(1);
        runs[old.Id].ValueKind.ShouldBe(JsonValueKind.Null);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/evaluation-runs/{onDemand.Id}", Ct)).GetProperty("documentGone").GetInt32().ShouldBe(1);
    }
}
