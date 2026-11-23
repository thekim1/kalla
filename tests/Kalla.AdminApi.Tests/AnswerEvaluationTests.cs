extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Answering;
using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The nightly answer run (B-58, processing design §14.8, Decisions "Nightly answer run"): on FakeGateway, two published
/// assistants answer their knowledge bases' evaluation questions and each answer is judged (the default judge: every claim
/// supported, every cited source supporting); with <c>Settings.EvaluationAnswerQuestions</c> at 0 no assistant gets a run, at
/// 5 a run has at most 5 questions (the human layer first); an assistant with no published version or no questions gets none;
/// a key with a spent budget leaves its assistant's run unfinished while the other's finishes; one run per assistant and
/// night, the next night's compares with it; the Statistics tab's read shows the last finished run within the assistant's scope.
/// </summary>
[Collection(AdminDatabaseGroup5.Name)]
public sealed class AnswerEvaluationTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "sk-ume-answer-eval-0000000000000000";
    private const string Model = "fors/chat-eu";
    private static readonly Guid GoodKey = new("019a0000-0000-7000-8000-00000000e0a1");
    private static readonly Guid SpentKey = new("019a0000-0000-7000-8000-00000000e0a2");
    private static readonly Guid TeamId = new("019a0000-0000-7000-8000-000000001002");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        foreach (var (n, id) in new[] { (7, GoodKey), (8, SpentKey) })
        {
            web.UseSetting($"FakeGateway:AssignedKeys:{n}:Id", id.ToString());
            web.UseSetting($"FakeGateway:AssignedKeys:{n}:Name", $"Answers {n}");
            web.UseSetting($"FakeGateway:AssignedKeys:{n}:TeamId", TeamId.ToString());
        }

        web.UseSetting("FakeGateway:AssignedKeys:8:BudgetExceeded", "Key");
    });

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Key", AdminKey);
        web.ConfigureTestServices(services =>
        {
            services.UseGatewayCredentialCeiling();
            foreach (var name in new[] { GatewayApi.HttpClientName, GatewayChatClient.HttpClientName, GatewayQueryEmbedder.HttpClientName, GatewayContextReader.HttpClientName })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            }
        });
    });

    [Fact]
    public async Task Each_published_assistant_with_questions_gets_one_judged_run_a_night_within_the_setting_and_a_refused_budget_leaves_it_unfinished()
    {
        var code = "N" + Random.Shared.Next(100_000, 999_999);
        var word = "ae" + code.ToLowerInvariant() + "kompost";
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Answers " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        db.Add(department);
        foreach (var key in new[] { GoodKey, SpentKey })
        {
            await db.GatewayKeys.Where(k => k.Id == key).ExecuteDeleteAsync(Ct);
            db.GatewayKeys.Add(new GatewayKey
            {
                Id = key, Name = "Answers " + code, TeamId = TeamId, DepartmentId = department.Id, Status = "Active", OwnerActive = true, Usable = true,
                PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
            });
        }

        if (!await db.GatewayModels.AnyAsync(m => m.Name == Model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = Model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        }

        Collection NewCollection(string name) => new()
        {
            Slug = name.ToLowerInvariant() + "-" + code.ToLowerInvariant(), Name = name + " " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var withQuestions = NewCollection("Kompost");
        var withoutQuestions = NewCollection("Tom");
        var pipeline = new Pipeline { Name = "ae-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = withQuestions.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(withQuestions, withoutQuestions, pipeline, source);
        await db.SaveChangesAsync(Ct);
        var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = withQuestions.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Kompostering",
            Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk($"Så komposterar du {word} hemma i Umeå.") { HeadingPath = "Avfall › Kompost" }], Ct);

        // Seven questions on the knowledge base: three human, four generated (the newest generated are taken after the human ones).
        var set = new EvaluationSet { CollectionId = withQuestions.Id, Classification = Classification.Internal, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(set);
        var human = Enumerable.Range(1, 3).Select(i => Question(set, EvaluationLayers.Human, $"Hur gör jag med {word}, fråga {i}?", document.Id)).ToList();
        var generated = Enumerable.Range(1, 4).Select(i => Question(set, EvaluationLayers.Generated, $"Vad gäller för {word}, variant {i}?", document.Id)).ToList();
        var retired = Question(set, EvaluationLayers.Human, $"Gammal fråga om {word}?", document.Id);
        retired.State = EvaluationQuestionStates.Retired;
        retired.RetiredAt = DateTimeOffset.UtcNow;
        db.AddRange([.. human, .. generated, retired]);

        // Four assistants: two published on the knowledge base (one with a spent budget), one published on a knowledge base
        // without questions, one with only a draft.
        var good = await AssistantAsync(db, department, "God", GoodKey, withQuestions, published: true, code);
        var spent = await AssistantAsync(db, department, "Slut", SpentKey, withQuestions, published: true, code);
        var empty = await AssistantAsync(db, department, "Tom", GoodKey, withoutQuestions, published: true, code);
        var draftOnly = await AssistantAsync(db, department, "Utkast", GoodKey, withQuestions, published: false, code);
        // Archived: it keeps its published version, but no client reaches it (#977 review).
        var archived = await AssistantAsync(db, department, "Arkiv", GoodKey, withQuestions, published: true, code);
        await db.Assistants.Where(a => a.Id == archived.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.State, AssistantStates.Archived), Ct);
        HashSet<Guid> ours = [good.Id, spent.Id, empty.Id, draftOnly.Id, archived.Id];

        // The live uncited rate: 10 messages, 2 not found, so 8 answered, 2 of them uncited.
        db.AssistantDailyTotals.Add(new AssistantDailyTotal
        {
            Day = StockholmTime.Today(TimeProvider.System), AssistantId = good.Id, ClientId = Guid.CreateVersion7(), DepartmentId = department.Id,
            Classification = Classification.Internal, Conversations = 4, Messages = 10, NotFound = 2, UncitedAnswers = 2,
        });
        await db.SaveChangesAsync(Ct);

        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        (await factory.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        var service = factory.Services.GetRequiredService<AnswerEvaluationService>();
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings());
            await db.SaveChangesAsync(Ct);
        }

        var before = await db.Settings.AsNoTracking().Select(s => new { s.EvaluationJudgeModel, s.EvaluationModel, s.EvaluationAnswerQuestions }).FirstAsync(Ct);
        try
        {
            await RunAsync();
        }
        finally
        {
            await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationJudgeModel, before.EvaluationJudgeModel).SetProperty(v => v.EvaluationModel, before.EvaluationModel)
                .SetProperty(v => v.EvaluationAnswerQuestions, before.EvaluationAnswerQuestions), Ct);
        }

        async Task RunAsync()
        {
            IQueryable<EvaluationRun> Runs(Guid assistant) => db.EvaluationRuns.AsNoTracking().Where(r => r.Kind == EvaluationRunKinds.Answers && r.SubjectId == assistant);

            // Switched off: no run for anyone.
            await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationJudgeModel, Model).SetProperty(v => v.EvaluationAnswerQuestions, 0), Ct);
            (await service.RunNightAsync(Ct, ours.Contains)).ShouldBeEmpty();
            (await db.EvaluationRuns.AsNoTracking().CountAsync(r => r.Kind == EvaluationRunKinds.Answers && ours.Contains(r.SubjectId!.Value), Ct)).ShouldBe(0);

            // On, but without a judge model: nothing runs, so no answer is paid for and never rated (#977 review).
            await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationAnswerQuestions, 5).SetProperty(v => v.EvaluationJudgeModel, (string?)null)
                .SetProperty(v => v.EvaluationModel, (string?)null), Ct);
            (await service.RunNightAsync(Ct, ours.Contains)).ShouldBeEmpty();
            await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationJudgeModel, Model), Ct);

            // Five a night: the two published, active assistants with questions get a run; the others none.
            var started = await service.RunNightAsync(Ct, ours.Contains);
            started.Select(r => r.SubjectId!.Value).Order().ShouldBe(new[] { good.Id, spent.Id }.Order());
            (await Runs(empty.Id).CountAsync(Ct), await Runs(draftOnly.Id).CountAsync(Ct), await Runs(archived.Id).CountAsync(Ct)).ShouldBe((0, 0, 0));

            var first = await Runs(good.Id).SingleAsync(Ct);
            (first.SetId, first.SubjectVersion, first.QuestionCount, first.Found, first.ComparedRunId, first.PromptVersion)
                .ShouldBe((null, 1, 5, 5, null, AnswerJudge.PromptVersion));
            first.FinishedAt.ShouldNotBeNull();
            (first.Faithfulness, first.CitationPrecision).ShouldBe((1m, 1m));
            var results = await db.EvaluationResults.AsNoTracking().Where(r => r.RunId == first.Id).ToListAsync(Ct);
            results.Count.ShouldBe(5);
            // The human layer first, then the newest generated questions; never a retired one.
            results.Select(r => r.QuestionId).ShouldBe([.. human.Select(q => q.Id), generated[3].Id, generated[2].Id], ignoreOrder: true);
            results.ShouldAllBe(r => r.Hit == true && r.Faithfulness == 1m && r.CitationPrecision == 1m && r.Answer!.Contains("Testsvar") && r.Question!.Contains(word));

            // The spent budget: the run is there, unfinished, without figures; the next night starts a new one.
            var refused = await Runs(spent.Id).SingleAsync(Ct);
            (refused.FinishedAt, refused.Faithfulness, refused.Found).ShouldBe((null, null, null));
            (await db.EvaluationResults.AsNoTracking().CountAsync(r => r.RunId == refused.Id, Ct)).ShouldBe(0);

            // One run a night: the same night again starts nothing.
            (await service.RunNightAsync(Ct, ours.Contains)).ShouldBeEmpty();

            // The next night (tonight's runs moved to yesterday) compares with the last finished run.
            await db.EvaluationRuns.Where(r => r.Kind == EvaluationRunKinds.Answers && ours.Contains(r.SubjectId!.Value))
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.StartedAt, r => r.StartedAt.AddDays(-1)), Ct);
            (await service.RunNightAsync(Ct, ours.Contains)).Count.ShouldBe(2);
            var second = await Runs(good.Id).OrderByDescending(r => r.StartedAt).FirstAsync(Ct);
            (second.ComparedRunId, second.Wins, second.Losses).ShouldBe((first.Id, 0, 0));
            (await Runs(spent.Id).CountAsync(r => r.FinishedAt == null, Ct)).ShouldBe(2);

            // The Statistics tab's read: the last finished run and the one it compares with, the live rate; within the scope.
            using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
            using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + code);
            var shown = await viewer.GetFromJsonAsync<JsonElement>($"/api/assistants/{good.Id}/answer-evaluation", Ct);
            (shown.GetProperty("enabled").GetBoolean(), shown.GetProperty("questionsPerNight").GetInt32()).ShouldBe((true, 5));
            (shown.GetProperty("latest").GetProperty("id").GetGuid(), shown.GetProperty("previous").GetProperty("id").GetGuid()).ShouldBe((second.Id, first.Id));
            (shown.GetProperty("latest").GetProperty("questions").GetInt32(), shown.GetProperty("latest").GetProperty("faithfulness").GetDecimal()).ShouldBe((5, 1m));
            var live = shown.GetProperty("live");
            (live.GetProperty("answered").GetInt32(), live.GetProperty("uncited").GetInt32(), live.GetProperty("rate").GetDecimal()).ShouldBe((8, 2, 0.25m));
            var none = await viewer.GetFromJsonAsync<JsonElement>($"/api/assistants/{spent.Id}/answer-evaluation", Ct);
            none.GetProperty("latest").ValueKind.ShouldBe(JsonValueKind.Null); // an unfinished run is never shown
            (await stranger.GetAsync($"/api/assistants/{good.Id}/answer-evaluation", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

            // Switched off, the card keeps the last run's figures.
            await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationAnswerQuestions, 0), Ct);
            var off = await viewer.GetFromJsonAsync<JsonElement>($"/api/assistants/{good.Id}/answer-evaluation", Ct);
            (off.GetProperty("enabled").GetBoolean(), off.GetProperty("latest").GetProperty("id").GetGuid()).ShouldBe((false, second.Id));
        }
    }

    [Fact]
    public void The_judges_counts_give_faithfulness_and_citation_precision()
    {
        var judged = AnswerJudge.Parse("""Svar: {"claims": 4, "supported": 3, "sources": [{"number": 1, "supports": true}, {"number": 2, "supports": false}], "note": "Ett påstående saknar stöd."}""", [1, 2]);
        judged.ShouldBe(new AnswerJudge.Judgement(0.75m, 0.5m, "Ett påstående saknar stöd."));
        // No claims: no faithfulness; no cited source: no citation precision; more supported than claims is capped.
        AnswerJudge.Parse("""{"claims": 0, "supported": 0, "sources": []}""", [1]).ShouldBe(new AnswerJudge.Judgement(null, 0m, ""));
        AnswerJudge.Parse("""{"claims": 2, "supported": 5}""", []).ShouldBe(new AnswerJudge.Judgement(1m, null, ""));
        AnswerJudge.Parse("inte json", [1]).ShouldBe(new AnswerJudge.Judgement(null, null, "The judge's answer could not be read."));
        AnswerJudge.Mean([0.5m, null, 1m]).ShouldBe(0.75m);
        AnswerJudge.Mean([null]).ShouldBeNull();
    }

    [Fact]
    public void The_night_is_the_Stockholm_day_and_the_run_starts_at_four()
    {
        // 2026-11-23 01:30 Stockholm (UTC+1) is 00:30 UTC: the night began at 2026-11-22 23:00 UTC.
        AnswerEvaluationService.NightOf(new DateTimeOffset(2026, 11, 23, 0, 30, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 11, 22, 23, 0, 0, TimeSpan.Zero));
        RetentionService.NextRunAt(new DateTimeOffset(2026, 11, 23, 2, 0, 0, TimeSpan.Zero), AnswerEvaluationService.RunAt)
            .ShouldBe(new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero));
        RetentionService.NextRunAt(new DateTimeOffset(2026, 11, 23, 3, 30, 0, TimeSpan.Zero), AnswerEvaluationService.RunAt)
            .ShouldBe(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero));
    }

    private int _created;

    /// <summary>A question whose id is a minute later than the previous one's: "newest" goes by the time-ordered id.</summary>
    private EvaluationQuestion Question(EvaluationSet set, string layer, string text, Guid document) => new()
    {
        Id = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(++_created - 60)),
        SetId = set.Id, Classification = Classification.Internal, Layer = layer, Question = text, ExpectedDocumentIds = [document],
    };

    private static async Task<Assistant> AssistantAsync(Kalla.Infrastructure.Persistence.RagDbContext db, Department department, string name, Guid key, Collection collection,
        bool published, string code)
    {
        var assistant = new Assistant
        {
            Slug = name.ToLowerInvariant() + "-" + code.ToLowerInvariant(), Name = name + " " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            GatewayKeyId = key, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(assistant);
        await db.SaveChangesAsync(Ct);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = Model, SystemPrompt = "Svara kort.", CollectionLinks = [CollectionLink.Whole(collection.Id)],
            State = published ? AssistantVersionStates.Published : AssistantVersionStates.Draft, PublishedAt = published ? DateTimeOffset.UtcNow : null,
            PublishedBy = published ? "anna@umea.se" : null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);
        await db.SaveChangesAsync(Ct);
        if (published)
        {
            await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.PublishedVersionId, version.Id), Ct);
        }
        else
        {
            await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.DraftVersionId, version.Id), Ct);
        }

        // Updated past the tracker (its xmin is stale now), so the next SaveChanges must not touch it.
        db.Entry(assistant).State = EntityState.Detached;
        db.Entry(version).State = EntityState.Detached;
        assistant.PublishedVersionId = published ? version.Id : null;
        return assistant;
    }
}
