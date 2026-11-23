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
/// Admin §11.2, assistant tests (B-57, admin design §7.4, V33): the test questions' CRUD and V33; a publish starts a run;
/// a question with each check (expected document, expected phrase, the judge on the expected behaviour) passes and fails as
/// specified; a run compares with the previous version's; results are readable only within the assistant's scope. The
/// answers come from the FakeGateway's echo ("Testsvar på …. Se [1]."), the judge's verdicts from its script per call.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class AssistantTestQuestionTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "sk-ume-eu-tests-00000000000000000";
    private const string Model = "fors/chat-eu";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The judge's verdicts in call order: the first run's three questions, then the second run's.
    private static readonly string[] Verdicts =
    [
        """{"verdict": "pass", "note": "Hänvisar till kompostsidan."}""",
        """{"verdict": "pass", "note": "Svarar på frågan."}""",
        """{"verdict": "fail", "note": "Nämner inte kontaktcenter."}""",
        """{"verdict": "pass", "note": "Hänvisar till kompostsidan."}""",
        """{"verdict": "pass", "note": "Svarar på frågan."}""",
        """{"verdict": "pass", "note": "Nämner kontaktcenter."}""",
    ];

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Key", AdminKey);
        web.ConfigureTestServices(services =>
        {
            services.UseGatewayCredentialCeiling();
            services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            services.AddHttpClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
        });
    });

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> FinishedAsync(HttpClient user, Guid assistant, int count)
    {
        for (var i = 0; i < 300; i++)
        {
            var runs = await user.GetFromJsonAsync<JsonElement>($"/api/assistants/{assistant}/test-runs", Ct);
            if (runs.GetArrayLength() >= count && runs[0].GetProperty("finishedAt").ValueKind != JsonValueKind.Null)
            {
                return runs[0];
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("The test-question run did not finish.");
    }

    [Fact]
    public async Task A_draft_linking_another_departments_unshared_knowledge_base_searches_nothing_of_it_and_shows_none_of_its_titles()
    {
        // #983: a draft can hold a link B's department may not make (a copy before the fix, or a share withdrawn since). The test
        // panel, the test runs and the nightly answer run search only what V11 allows; V33 refuses such a document as expected,
        // and a question that already names one shows no title.
        var codeA = "A" + Random.Shared.Next(100_000, 999_999);
        var codeB = "B" + Random.Shared.Next(100_000, 999_999);
        var word = "x" + codeA.ToLowerInvariant() + "hemlig";
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var a = new Department { Id = Guid.CreateVersion7(), Name = "Avdelning " + codeA, CostCenterCode = codeA, SyncedAt = DateTimeOffset.UtcNow };
        var b = new Department { Id = Guid.CreateVersion7(), Name = "Avdelning " + codeB, CostCenterCode = codeB, SyncedAt = DateTimeOffset.UtcNow };
        var secret = new Collection
        {
            Slug = "hemlig-" + codeA.ToLowerInvariant(), Name = "Hemlig " + codeA, DepartmentId = a.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, // SharedWith: the department only
        };
        var pipeline = new Pipeline { Name = "hemlig-" + codeA, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = secret.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(a, b, secret, pipeline, source);
        if (!await db.GatewayModels.AnyAsync(m => m.Name == Model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = Model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        }

        await db.SaveChangesAsync(Ct);
        var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = secret.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Hemlig rutin " + codeA,
            Url = $"https://www.umea.se/{codeA}/hemlig",
        }, [new FixtureChunk($"Den hemliga rutinen för {word} gäller bara avdelning A.")], Ct);

        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        });
        await using var factory = Admin(gateway);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, codeB);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, codeB);

        using var created = await editor.PostAsJsonAsync("/api/assistants", new
        {
            name = "Guiden " + codeB, departmentId = b.Id, classification = "internal", description = "Om rutiner.", contactName = "Anna", contactEmail = "anna@umea.se",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var createdBody = await Json(created);
        var id = createdBody.GetProperty("id").GetGuid();
        // V11 only warns on a draft, so the unshared link is saved.
        using (var draft = await editor.PutAsJsonAsync($"/api/assistants/{id}/draft", new
               {
                   gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = secret.Id, tags = Array.Empty<string>() } },
                   version = createdBody.GetProperty("draft").GetProperty("version").GetString(),
               }, Ct))
        {
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
        }

        // The panel searches nothing of it and doesn't name it.
        using (var tested = await editor.PostAsJsonAsync($"/api/assistants/{id}/test", new { question = $"Vad gäller för {word}?", target = "Draft" }, Ct))
        {
            tested.StatusCode.ShouldBe(HttpStatusCode.OK, await tested.Content.ReadAsStringAsync(Ct));
            var body = await Json(tested);
            body.GetProperty("retrieval").GetProperty("candidates").GetArrayLength().ShouldBe(0);
            body.GetProperty("retrieval").GetProperty("collections").GetArrayLength().ShouldBe(0);
            body.GetRawText().ShouldNotContain("hemliga rutinen");
            body.GetRawText().ShouldNotContain("Hemlig rutin");
        }

        // V33 refuses its document as expected; a question that already names it (a copy before the fix) shows no title.
        using (var refused = await editor.PostAsJsonAsync($"/api/assistants/{id}/test-questions", new
               {
                   question = $"Vad gäller för {word}?", expectedBehaviour = "Svarar.", expectedDocumentId = document.Id,
               }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        db.AssistantTestQuestions.Add(new AssistantTestQuestion
        {
            AssistantId = id, Ordinal = 0, Question = $"Vad gäller för {word}?", ExpectedBehaviour = "Svarar.", ExpectedDocumentId = document.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        var expected = (await viewer.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}/test-questions", Ct))[0].GetProperty("expectedDocument");
        (expected.GetProperty("title").ValueKind, expected.GetProperty("unlinked").GetBoolean()).ShouldBe((JsonValueKind.Null, true));
    }

    [Fact]
    public async Task A_publish_runs_the_questions_each_check_passes_or_fails_and_the_next_run_compares_with_the_previous_version()
    {
        var code = "Q" + Random.Shared.Next(100_000, 999_999);
        var word = "tq" + code.ToLowerInvariant() + "kompost";
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Test " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        Collection NewCollection(string name) => new()
        {
            Slug = name.ToLowerInvariant() + "-" + code.ToLowerInvariant(), Name = name + " " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var collection = NewCollection("Kompost");
        var other = NewCollection("Annat");
        var pipeline = new Pipeline { Name = "tq-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        Source NewSource(Collection c) => new()
        {
            CollectionId = c.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var source = NewSource(collection);
        var otherSource = NewSource(other);
        db.AddRange(department, collection, other, pipeline, source, otherSource);
        if (!await db.GatewayModels.AnyAsync(m => m.Name == Model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = Model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        }

        await db.SaveChangesAsync(Ct);
        var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Kompostering",
            Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk($"Så komposterar du {word} hemma i Umeå.") { HeadingPath = "Avfall › Kompost" }], Ct);
        var elsewhere = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = other.Id, EmbeddingProfileId = profile, SourceId = otherSource.Id, Classification = Classification.Internal, Title = "Annat",
            Url = $"https://www.umea.se/{code}/annat",
        }, [new FixtureChunk("Något helt annat.")], Ct);

        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
            for (var i = 0; i < Verdicts.Length; i++)
            {
                web.UseSetting($"FakeGateway:ChatScripts:assistant-test-judge:{i}", Verdicts[i]);
            }
        });
        await using var factory = Admin(gateway);
        // The judge model (V32); restored afterwards, since the class's database is shared.
        var before = await db.Settings.AsNoTracking().Select(x => x.EvaluationJudgeModel).FirstOrDefaultAsync(Ct);
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings());
            await db.SaveChangesAsync(Ct);
        }

        await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationJudgeModel, Model), Ct);
        try
        {
            await RunAsync();
        }
        finally
        {
            await db.Settings.ExecuteUpdateAsync(x => x.SetProperty(v => v.EvaluationJudgeModel, before), Ct);
        }

        async Task RunAsync()
        {

            using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
            using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, code);
            using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + code);

            using var created = await editor.PostAsJsonAsync("/api/assistants", new
            {
                name = "Kompostguiden " + code, departmentId = department.Id, classification = "internal", description = "Om kompost.", contactName = "Anna",
                contactEmail = "anna@umea.se",
            }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            var createdBody = await Json(created);
            var id = createdBody.GetProperty("id").GetGuid();
            var path = $"/api/assistants/{id}";
            var draftVersion = createdBody.GetProperty("draft").GetProperty("version").GetString();
            using (var draft = await editor.PutAsJsonAsync($"{path}/draft", new
                   {
                       gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = collection.Id, tags = Array.Empty<string>() } }, version = draftVersion,
                   }, Ct))
            {
                draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            }

            // V33: the expected document must be in a linked knowledge base; the texts are required.
            using (var unlinked = await editor.PostAsJsonAsync($"{path}/test-questions", new
                   {
                       question = $"Hur gör jag med {word}?", expectedBehaviour = "Svarar.", expectedDocumentId = elsewhere.Id,
                   }, Ct))
            {
                unlinked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await Json(unlinked)).GetProperty("errorCodes").GetProperty("expectedDocumentId")[0].GetProperty("code").GetString()
                    .ShouldBe(AdminErrorCodes.TestQuestionDocumentUnlinked.Code);
            }

            (await editor.PostAsJsonAsync($"{path}/test-questions", new { question = word, expectedBehaviour = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await viewer.PostAsJsonAsync($"{path}/test-questions", new { question = word, expectedBehaviour = "Svarar." }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            // Three questions: every check passes; the phrase is missing; the judge fails (then passes in the next run).
            async Task<Guid> AddAsync(object body)
            {
                using var response = await editor.PostAsJsonAsync($"{path}/test-questions", body, Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
                return (await Json(response)).GetProperty("id").GetGuid();
            }

            var allPass = await AddAsync(new { question = $"Hur gör jag med {word}?", expectedBehaviour = "Hänvisar till kompostsidan.", expectedDocumentId = document.Id, expectedPhrase = "TESTSVAR" });
            var noPhrase = await AddAsync(new { question = $"Vad kostar {word}?", expectedBehaviour = "Svarar på frågan.", expectedPhrase = "avgiften är" });
            var judged = await AddAsync(new { question = $"Vem hjälper till med {word}?", expectedBehaviour = "Hänvisar till kontaktcenter." });
            var listed = await viewer.GetFromJsonAsync<JsonElement>($"{path}/test-questions", Ct);
            listed.EnumerateArray().Select(q => q.GetProperty("id").GetGuid()).ShouldBe([allPass, noPhrase, judged]);
            listed[0].GetProperty("expectedDocument").GetProperty("title").GetString().ShouldBe("Kompostering");
            (await stranger.GetAsync($"{path}/test-questions", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

            // The publish starts a run on version 1.
            var version = (await editor.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("version").GetString();
            var draftNow = (await editor.GetFromJsonAsync<JsonElement>($"{path}/draft", Ct)).GetProperty("version").GetString();
            using (var published = await editor.PostAsJsonAsync($"{path}/publish", new { version, draftVersion = draftNow }, Ct))
            {
                published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
            }

            var first = await FinishedAsync(viewer, id, 1);
            (first.GetProperty("version").GetInt32(), first.GetProperty("questions").GetInt32(), first.GetProperty("asExpected").GetInt32()).ShouldBe((1, 3, 1));
            first.GetProperty("comparedRunId").ValueKind.ShouldBe(JsonValueKind.Null);
            var report = await viewer.GetFromJsonAsync<JsonElement>($"{path}/test-runs/{first.GetProperty("id").GetGuid()}", Ct);
            var results = report.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("questionId").GetGuid());
            (results[allPass].GetProperty("checks").GetBoolean(), results[allPass].GetProperty("verdict").GetString(), results[allPass].GetProperty("asExpected").GetBoolean())
                .ShouldBe((true, "pass", true));
            results[allPass].GetProperty("answer").GetString()!.ShouldContain("Testsvar");
            (results[noPhrase].GetProperty("checks").GetBoolean(), results[noPhrase].GetProperty("asExpected").GetBoolean()).ShouldBe((false, false));
            (results[judged].GetProperty("checks").ValueKind, results[judged].GetProperty("verdict").GetString(), results[judged].GetProperty("judgeNote").GetString())
                .ShouldBe((JsonValueKind.Null, "fail", "Nämner inte kontaktcenter."));
            (await db.AssistantVersions.AsNoTracking().SingleAsync(v => v.AssistantId == id && v.Number == 1, Ct)).TestRunId.ShouldBe(first.GetProperty("id").GetGuid());

            // Results only within the assistant's scope.
            (await stranger.GetAsync($"{path}/test-runs", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await stranger.GetAsync($"{path}/test-runs/{first.GetProperty("id").GetGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await viewer.PostAsJsonAsync($"{path}/test-runs", new { target = "Published" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            // Version 2: its run compares with version 1's; the judged question is better now.
            using (var draft = await editor.PutAsJsonAsync($"{path}/draft", new
                   {
                       gatewayModel = Model, systemPrompt = "Svara kort och hänvisa till kontaktcenter.", collectionLinks = new[] { new { collectionId = collection.Id, tags = Array.Empty<string>() } },
                   }, Ct))
            {
                draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            }

            version = (await editor.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("version").GetString();
            draftNow = (await editor.GetFromJsonAsync<JsonElement>($"{path}/draft", Ct)).GetProperty("version").GetString();
            (await editor.PostAsJsonAsync($"{path}/publish", new { version, draftVersion = draftNow }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var second = await FinishedAsync(viewer, id, 2);
            (second.GetProperty("version").GetInt32(), second.GetProperty("asExpected").GetInt32(), second.GetProperty("better").GetInt32(), second.GetProperty("worse").GetInt32())
                .ShouldBe((2, 2, 1, 0));
            (second.GetProperty("comparedRunId").GetGuid(), second.GetProperty("comparedVersion").GetInt32(), second.GetProperty("comparedAsExpected").GetInt32())
                .ShouldBe((first.GetProperty("id").GetGuid(), 1, 1));
            var changed = (await viewer.GetFromJsonAsync<JsonElement>($"{path}/test-runs/{second.GetProperty("id").GetGuid()}", Ct)).GetProperty("results").EnumerateArray()
                .Single(r => r.GetProperty("questionId").GetGuid() == judged);
            changed.GetProperty("change").GetString().ShouldBe("better");

            // One run per assistant at a time; an assistant without questions gets none.
            await using (var running = TestPostgres.Context(database.Connection(null)))
            {
                running.EvaluationRuns.Add(new EvaluationRun
                {
                    SetId = null, Classification = Classification.Internal, Kind = EvaluationRunKinds.AssistantTests, SubjectId = id, SubjectVersion = 2, QuestionCount = 3,
                    StartedAt = DateTimeOffset.UtcNow,
                });
                await running.SaveChangesAsync(Ct);
            }

            using (var busy = await editor.PostAsJsonAsync($"{path}/test-runs", new { target = "Published" }, Ct))
            {
                busy.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                (await Json(busy)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.TestRunRunning.Code);
            }

            using var empty = await editor.PostAsJsonAsync("/api/assistants", new { name = "Tom " + code, departmentId = department.Id, classification = "internal" }, Ct);
            var emptyBody = await Json(empty);
            var emptyId = emptyBody.GetProperty("id").GetGuid();
            await editor.PutAsJsonAsync($"/api/assistants/{emptyId}/draft", new
            {
                gatewayModel = Model, systemPrompt = "x", version = emptyBody.GetProperty("draft").GetProperty("version").GetString(),
            }, Ct);
            using (var none = await editor.PostAsJsonAsync($"/api/assistants/{emptyId}/test-runs", new { target = "Draft" }, Ct))
            {
                none.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                (await Json(none)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.NoTestQuestions.Code);
            }

            (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "assistanttest").Select(a => a.Action).ToListAsync(Ct)).ShouldContain("create");
        }
    }

    [Fact]
    public async Task At_most_thirty_questions_an_edit_keeps_its_version_and_a_delete_removes_one()
    {
        var code = "R" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Test " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        db.Add(department);
        await db.SaveChangesAsync(Ct);
        await using var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web => web.UseEnvironment("Development"));
        await using var factory = Admin(gateway);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        using var created = await editor.PostAsJsonAsync("/api/assistants", new { name = "Trettio " + code, departmentId = department.Id, classification = "internal" }, Ct);
        var path = $"/api/assistants/{(await Json(created)).GetProperty("id").GetGuid()}/test-questions";

        JsonElement firstQuestion = default;
        for (var i = 0; i < AssistantTestQuestion.MaxPerAssistant; i++)
        {
            using var added = await editor.PostAsJsonAsync(path, new { question = $"Fråga {i}?", expectedBehaviour = "Svarar." }, Ct);
            added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Ct));
            if (i == 0)
            {
                firstQuestion = await Json(added);
            }
        }

        using (var refused = await editor.PostAsJsonAsync(path, new { question = "En till?", expectedBehaviour = "Svarar." }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(refused)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.TestQuestionInvalid.Code);
        }

        var qid = firstQuestion.GetProperty("id").GetGuid();
        (await editor.PutAsJsonAsync($"{path}/{qid}", new { question = "Ny?", expectedBehaviour = "Svarar.", version = "1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using (var edited = await editor.PutAsJsonAsync($"{path}/{qid}", new
               {
                   question = "  Ändrad fråga?  ", expectedBehaviour = "Svarar kort.", expectedPhrase = " kompost ", version = firstQuestion.GetProperty("version").GetString(),
               }, Ct))
        {
            edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Ct));
            var body = await Json(edited);
            (body.GetProperty("question").GetString(), body.GetProperty("expectedPhrase").GetString()).ShouldBe(("Ändrad fråga?", "kompost"));
            (await editor.DeleteAsync($"{path}/{qid}?version={body.GetProperty("version").GetString()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await editor.GetFromJsonAsync<JsonElement>(path, Ct)).GetArrayLength().ShouldBe(AssistantTestQuestion.MaxPerAssistant - 1);
    }

    [Theory]
    [InlineData("Avgiften är 500 kronor.", "AVGIFTEN ÄR", true)]
    [InlineData("Avgiften  är\n500 kronor.", "avgiften är 500", true)]
    [InlineData("Ange ﬁlen.", "filen", true)] // NFKC folds the ligature
    [InlineData("Ingen avgift.", "avgiften är", false)]
    public void The_phrase_check_ignores_case_spacing_and_unicode_forms(string answer, string phrase, bool expected) =>
        AssistantTestChecks.ContainsPhrase(answer, phrase).ShouldBe(expected);

    [Theory]
    [InlineData("""{"verdict": "PASS", "note": "Bra."}""", "pass", "Bra.")]
    [InlineData("""Svar: {"verdict":"fail","note":"Saknar kontakt."} tack""", "fail", "Saknar kontakt.")]
    [InlineData("""{"verdict": "kanske"}""", "unsure", "The judge's answer could not be read.")]
    [InlineData("inte json", "unsure", "The judge's answer could not be read.")]
    public void The_judge_answer_is_read_and_anything_else_is_unsure(string text, string verdict, string note) =>
        AssistantTestJudge.Parse(text).ShouldBe((verdict, note));

    [Fact]
    public void A_question_is_as_expected_only_when_every_check_it_has_passes()
    {
        AssistantTestChecks.AsExpected(null, null, TestVerdicts.Pass).ShouldBeTrue();
        AssistantTestChecks.AsExpected(true, null, TestVerdicts.Pass).ShouldBeTrue();
        AssistantTestChecks.AsExpected(false, null, TestVerdicts.Pass).ShouldBeFalse();
        AssistantTestChecks.AsExpected(true, true, TestVerdicts.Unsure).ShouldBeFalse();
        AssistantTestChecks.AsExpected(null, false, TestVerdicts.Pass).ShouldBeFalse();
    }
}
