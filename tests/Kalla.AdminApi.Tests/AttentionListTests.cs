using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.Pipelines.Definitions;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Home's <i>Needs attention</i> (admin design §5.5.1, V32; B-59): each kind of <c>GET /api/overview</c>'s attention list is
/// raised by its cause and gone when the cause is; a department admin sees only the items of their departments, and the
/// platform's own items (feedback import, the syncs, the platform model settings) only platform admins.
/// </summary>
[Collection(AdminDatabaseGroup6.Name)]
public sealed class AttentionListTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Department Department, Collection Collection, Source Source, Pipeline Pipeline, Guid Profile);

    private async Task<Seed> SeedAsync(RagDbContext db, string classification = Classification.Internal)
    {
        var code = "A" + Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Attention " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "att-" + code.ToLowerInvariant(), Name = "Kunskap " + code, DepartmentId = department.Id, Classification = classification, EmbeddingProfileId = profile,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "att-" + code, TemplateId = "url-list", DepartmentId = department.Id, UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor " + code, Classification = classification, PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, pipeline, source);
        await db.SaveChangesAsync(Ct);
        return new Seed(code, department, collection, source, pipeline, profile);
    }

    private WebApplicationFactory<Program> Factory() => database.HeaderFactory();

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client, string kind, Guid? objectId = null)
    {
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview", Ct);
        return
        [
            .. overview.GetProperty("attention").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == kind
                && (objectId is null || (i.GetProperty("objectId").ValueKind == JsonValueKind.String && i.GetProperty("objectId").GetGuid() == objectId))),
        ];
    }

    private static Run NewRun(Source source, string state, DateTimeOffset startedAt) => new()
    {
        SourceId = source.Id, Classification = source.Classification, Kind = RunKinds.Sync, Trigger = RunTriggers.Manual, State = state, StartedAt = startedAt,
        FinishedAt = state == RunStates.Running ? null : startedAt.AddMinutes(5),
    };

    [Fact]
    public async Task A_run_waiting_for_the_deletion_decision_is_raised_for_its_department_only_and_gone_once_decided()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var run = NewRun(seed.Source, RunStates.AwaitingDeletionConfirmation, DateTimeOffset.UtcNow.AddHours(-1));
        run.ProposedDeletions = 212;
        db.Runs.Add(run);
        await db.SaveChangesAsync(Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var other = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);

        var item = (await ItemsAsync(admin, AttentionKinds.DeletionWaiting, seed.Source.Id)).ShouldHaveSingleItem();
        (item.GetProperty("objectType").GetString(), item.GetProperty("objectName").GetString()).ShouldBe((AttentionObjects.Source, seed.Source.Name));
        (item.GetProperty("details").GetProperty("proposed").GetString(), item.GetProperty("details").GetProperty("runId").GetString()).ShouldBe(("212", run.Id.ToString()));
        item.GetProperty("departmentId").GetGuid().ShouldBe(seed.Department.Id);
        (await ItemsAsync(other, AttentionKinds.DeletionWaiting, seed.Source.Id)).ShouldBeEmpty();

        await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.State, RunStates.Succeeded), Ct);
        (await ItemsAsync(admin, AttentionKinds.DeletionWaiting, seed.Source.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Three_failed_runs_in_a_row_and_a_degraded_latest_run_are_raised_and_gone_after_a_good_run()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        db.Runs.AddRange(Enumerable.Range(0, 3).Select(i => NewRun(seed.Source, RunStates.Failed, start.AddHours(i))));
        await db.SaveChangesAsync(Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        (await ItemsAsync(admin, AttentionKinds.SourceFailing, seed.Source.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("failed").GetString().ShouldBe("3");
        db.Runs.Add(NewRun(seed.Source, RunStates.Succeeded, start.AddHours(4)));
        await db.SaveChangesAsync(Ct);
        (await ItemsAsync(admin, AttentionKinds.SourceFailing, seed.Source.Id)).ShouldBeEmpty();

        var degraded = NewRun(seed.Source, RunStates.Degraded, start.AddHours(5));
        db.Runs.Add(degraded);
        await db.SaveChangesAsync(Ct);
        (await ItemsAsync(admin, AttentionKinds.RunDegraded, seed.Source.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("runId").GetString()
            .ShouldBe(degraded.Id.ToString());
        db.Runs.Add(NewRun(seed.Source, RunStates.Succeeded, start.AddHours(6)));
        await db.SaveChangesAsync(Ct);
        (await ItemsAsync(admin, AttentionKinds.RunDegraded, seed.Source.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Three_incomplete_traversals_are_raised_and_a_complete_one_ends_it()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var start = DateTimeOffset.UtcNow.AddDays(-3);
        async Task CrawlAsync(int hour, bool complete)
        {
            var run = NewRun(seed.Source, RunStates.Succeeded, start.AddHours(hour));
            db.Runs.Add(run);
            await db.SaveChangesAsync(Ct);
            db.CrawlRuns.Add(new CrawlRun { RunId = run.Id, SourceId = seed.Source.Id, Classification = seed.Source.Classification, Complete = complete });
            await db.SaveChangesAsync(Ct);
        }

        await CrawlAsync(0, false);
        await CrawlAsync(1, false);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        (await ItemsAsync(admin, AttentionKinds.SourceIncomplete, seed.Source.Id)).ShouldBeEmpty(); // two are not yet three
        await CrawlAsync(2, false);
        (await ItemsAsync(admin, AttentionKinds.SourceIncomplete, seed.Source.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("runs").GetString().ShouldBe("3");

        // Settings.Crawl.incompleteRunsAlert, read as the worker reads it (#978 review): 4 needs a fourth; 0 means the default.
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings());
            await db.SaveChangesAsync(Ct);
        }

        var crawl = await db.Settings.AsNoTracking().Select(x => x.Crawl).FirstAsync(Ct);
        try
        {
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Crawl, "{\"incompleteRunsAlert\": 4}"), Ct);
            (await ItemsAsync(admin, AttentionKinds.SourceIncomplete, seed.Source.Id)).ShouldBeEmpty();
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Crawl, "{\"incompleteRunsAlert\": 0}"), Ct);
            (await ItemsAsync(admin, AttentionKinds.SourceIncomplete, seed.Source.Id)).ShouldHaveSingleItem();
        }
        finally
        {
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Crawl, crawl), Ct);
        }

        await CrawlAsync(3, true);
        (await ItemsAsync(admin, AttentionKinds.SourceIncomplete, seed.Source.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Flagged_documents_in_a_public_knowledge_base_are_counted_and_gone_once_cleared()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db, Classification.Public);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = seed.Collection.Id, EmbeddingProfileId = seed.Profile, SourceId = seed.Source.Id, Classification = Classification.Public, Title = "Kontakt",
            Url = $"https://www.umea.se/{seed.Code}/kontakt",
        }, [new FixtureChunk("Ring 090-16 10 00.")], Ct);
        await db.ChunkSets.Where(c => c.DocumentId == document.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.PiiFlagged, true), Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        (await ItemsAsync(admin, AttentionKinds.PersonalDataPublic, seed.Collection.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("documents").GetString()
            .ShouldBe("1");
        await db.Documents.Where(d => d.Id == document.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.PiiClearedAt, DateTimeOffset.UtcNow), Ct);
        (await ItemsAsync(admin, AttentionKinds.PersonalDataPublic, seed.Collection.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_worse_report_and_a_nightly_recall_below_its_median_are_raised_and_gone_after_a_good_night()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var set = new EvaluationSet { CollectionId = seed.Collection.Id, Classification = Classification.Internal, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(set);
        await db.SaveChangesAsync(Ct);
        var start = DateTimeOffset.UtcNow.AddDays(-5);
        EvaluationRun Night(int day, decimal recall, Guid? compared) => new()
        {
            SetId = set.Id, Classification = Classification.Internal, Kind = EvaluationRunKinds.Nightly, QuestionCount = 100, Found = (int)(recall * 100), RecallAtK = recall,
            Mrr = recall, ComparedRunId = compared, StartedAt = start.AddDays(day), FinishedAt = start.AddDays(day).AddMinutes(1),
        };
        Guid? previous = null;
        foreach (var day in Enumerable.Range(0, 3))
        {
            var night = Night(day, 0.9m, previous);
            db.EvaluationRuns.Add(night);
            await db.SaveChangesAsync(Ct);
            previous = night.Id;
        }

        var worse = Night(3, 0.84m, previous);
        db.EvaluationRuns.Add(worse);
        await db.SaveChangesAsync(Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        var report = (await ItemsAsync(admin, AttentionKinds.EvaluationWorse, seed.Collection.Id)).ShouldHaveSingleItem().GetProperty("details");
        (report.GetProperty("points").GetString(), report.GetProperty("runKind").GetString()).ShouldBe(("6.0", EvaluationRunKinds.Nightly));
        (await ItemsAsync(admin, AttentionKinds.RecallBelowMedian, seed.Collection.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("median").GetString()
            .ShouldBe("0.9");

        db.EvaluationRuns.Add(Night(4, 0.9m, worse.Id));
        await db.SaveChangesAsync(Ct);
        (await ItemsAsync(admin, AttentionKinds.EvaluationWorse, seed.Collection.Id)).ShouldBeEmpty();
        (await ItemsAsync(admin, AttentionKinds.RecallBelowMedian, seed.Collection.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unusable_key_a_lost_model_and_a_lost_residency_are_raised_for_an_assistant_and_gone_when_the_key_is_fit()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var keyId = Guid.CreateVersion7();
        var assistant = new Assistant
        {
            Slug = "att-" + seed.Code.ToLowerInvariant(), Name = "Guiden " + seed.Code, DepartmentId = seed.Department.Id, Classification = Classification.Internal, GatewayKeyId = keyId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(assistant);
        await db.SaveChangesAsync(Ct);
        var draft = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "fors/chat-eu", SystemPrompt = "Svara kort.", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(draft);
        await db.SaveChangesAsync(Ct);
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.DraftVersionId, draft.Id), Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        async Task<string?> ReasonAsync() =>
            (await ItemsAsync(admin, AttentionKinds.GatewayKey, assistant.Id)).SingleOrDefault() is { ValueKind: JsonValueKind.Object } item
                ? item.GetProperty("details").GetProperty("reason").GetString()
                : null;

        (await ReasonAsync()).ShouldBe("unknown"); // not in the mirror
        db.GatewayKeys.Add(new GatewayKey
        {
            Id = keyId, Name = "Nyckel " + seed.Code, TeamId = Guid.CreateVersion7(), DepartmentId = seed.Department.Id, Status = "Disabled", OwnerActive = true, Usable = true,
            PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        (await ReasonAsync()).ShouldBe("not_active"); // disabled
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.Status, "Active").SetProperty(k => k.AllowedModels, new[] { "fors/other" }), Ct);
        (await ReasonAsync()).ShouldBe("model_lost");
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.AllowedModels, Array.Empty<string>())
            .SetProperty(k => k.AllowedResidencies, new[] { "External" }), Ct);
        (await ReasonAsync()).ShouldBe("residency_lost");
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.AllowedResidencies, Array.Empty<string>()), Ct);
        (await ReasonAsync()).ShouldBeNull();

        // V39's other reasons (#978 review): the owner inactive, not usable by Källa, another department's key.
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.OwnerActive, false), Ct);
        (await ReasonAsync()).ShouldBe("owner_inactive");
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.OwnerActive, true).SetProperty(k => k.Usable, false), Ct);
        (await ReasonAsync()).ShouldBe("not_usable");
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.Usable, true).SetProperty(k => k.DepartmentId, Guid.CreateVersion7()), Ct);
        (await ReasonAsync()).ShouldBe("other_department");
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.DepartmentId, seed.Department.Id), Ct);

        // The audience's ceiling, as V39: an External-only key is fine for a public assistant until a client that stores
        // history at the internal level is in its audience (the ceiling is then Eu).
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Classification, Classification.Public), Ct);
        await db.GatewayKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.AllowedResidencies, new[] { "External" }), Ct);
        (await ReasonAsync()).ShouldBeNull();
        var client = new Client
        {
            Name = "Handläggning " + seed.Code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, StoresHistory = true, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(client);
        await db.SaveChangesAsync(Ct);
        db.Add(new AssistantAudience { AssistantId = assistant.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id });
        await db.SaveChangesAsync(Ct);
        (await ReasonAsync()).ShouldBe("residency_lost");

        // A knowledge base's key: the same item on the knowledge base.
        var collectionKey = Guid.CreateVersion7();
        await db.Collections.Where(c => c.Id == seed.Collection.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.GatewayKeyId, collectionKey), Ct);
        var onCollection = (await ItemsAsync(admin, AttentionKinds.GatewayKey, seed.Collection.Id)).ShouldHaveSingleItem();
        (onCollection.GetProperty("objectType").GetString(), onCollection.GetProperty("details").GetProperty("reason").GetString())
            .ShouldBe((AttentionObjects.Collection, "unknown"));
        await db.Collections.Where(c => c.Id == seed.Collection.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.GatewayKeyId, (Guid?)null), Ct);
        (await ItemsAsync(admin, AttentionKinds.GatewayKey, seed.Collection.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_published_template_version_no_live_worker_runs_is_raised_for_its_scope_and_gone_once_a_worker_supports_it()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var template = TemplateCatalog.BuiltIn.All.Where(t => t.Template.SourceType == SourceTypes.UrlList).MaxBy(t => t.Template.Version)!.Template;
        var definition = JsonSerializer.Serialize(new { schema = PipelineDefinition.CurrentSchema, template = template.Name, templateVersion = template.Version, @params = new { } });
        async Task PublishAsync(Pipeline pipeline)
        {
            var version = new PipelineVersion
            {
                PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow, Definition = definition,
            };
            db.Add(version);
            await db.SaveChangesAsync(Ct);
            await db.Pipelines.Where(p => p.Id == pipeline.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.PublishedVersionId, version.Id), Ct);
        }

        await PublishAsync(seed.Pipeline);
        // A platform pipeline (no department) used by a second source of the same knowledge base: platform admins only.
        var platformPipeline = new Pipeline { Name = "att-platform-" + seed.Code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        db.Add(platformPipeline);
        await db.SaveChangesAsync(Ct);
        db.Add(new Source
        {
            CollectionId = seed.Collection.Id, Type = SourceTypes.UrlList, Name = "Plattform " + seed.Code, Classification = seed.Collection.Classification,
            PipelineId = platformPipeline.Id, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        await PublishAsync(platformPipeline);

        // A live worker that runs none of the template's stage versions (without any live worker the kind is not raised at all).
        var classes = template.Stages.Keys.SelectMany(s => Stages.Find(s)!.HostClasses).Distinct().ToArray();
        await db.WorkerInstances.Where(w => w.BuildVersion == "attention").ExecuteDeleteAsync(Ct);
        var instance = new WorkerInstance
        {
            InstanceId = Guid.CreateVersion7(), BuildVersion = "attention", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), LastHeartbeatAt = DateTimeOffset.UtcNow, Classes = classes,
        };
        db.WorkerInstances.Add(instance);
        await db.SaveChangesAsync(Ct);
        try
        {
            await using var factory = Factory();
            using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
            using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

            (await ItemsAsync(admin, AttentionKinds.TemplateUnsupported, seed.Pipeline.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("templateVersion")
                .GetString().ShouldBe(template.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            (await ItemsAsync(admin, AttentionKinds.TemplateUnsupported, platformPipeline.Id)).ShouldBeEmpty();
            (await ItemsAsync(platform, AttentionKinds.TemplateUnsupported, platformPipeline.Id)).ShouldHaveSingleItem();

            db.WorkerNodeSupport.AddRange(template.Stages.Select(s => new WorkerNodeSupport { InstanceId = instance.InstanceId, NodeType = s.Key, TypeVersion = s.Value }));
            await db.SaveChangesAsync(Ct);
            (await ItemsAsync(admin, AttentionKinds.TemplateUnsupported, seed.Pipeline.Id)).ShouldBeEmpty();
            (await ItemsAsync(platform, AttentionKinds.TemplateUnsupported, platformPipeline.Id)).ShouldBeEmpty();
        }
        finally
        {
            await db.WorkerInstances.Where(w => w.InstanceId == instance.InstanceId).ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task A_worse_test_run_and_a_nightly_answer_run_below_its_median_are_raised_for_the_assistant_and_gone_when_better()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var assistant = new Assistant
        {
            Slug = "att-" + seed.Code.ToLowerInvariant(), Name = "Guiden " + seed.Code, DepartmentId = seed.Department.Id, Classification = Classification.Internal,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(assistant);
        await db.SaveChangesAsync(Ct);
        EvaluationRun Tests(int version, int asExpected, Guid? compared) => new()
        {
            SetId = null, Classification = Classification.Internal, Kind = EvaluationRunKinds.AssistantTests, SubjectId = assistant.Id, SubjectVersion = version, QuestionCount = 3,
            Found = asExpected, ComparedRunId = compared, StartedAt = DateTimeOffset.UtcNow.AddHours(-version), FinishedAt = DateTimeOffset.UtcNow.AddHours(-version).AddMinutes(1),
        };
        var before = Tests(1, 3, null);
        db.EvaluationRuns.Add(before);
        await db.SaveChangesAsync(Ct);
        var after = Tests(2, 2, before.Id);
        db.EvaluationRuns.Add(after);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 2, GatewayModel = "fors/chat-eu", SystemPrompt = "Svara kort.", State = AssistantVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow,
            PublishedBy = "anna@umea.se", TestRunId = after.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);
        await db.SaveChangesAsync(Ct);
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.PublishedVersionId, version.Id), Ct);

        var start = DateTimeOffset.UtcNow.AddDays(-4);
        EvaluationRun Night(int day, decimal faithfulness) => new()
        {
            SetId = null, Classification = Classification.Internal, Kind = EvaluationRunKinds.Answers, SubjectId = assistant.Id, SubjectVersion = 2, QuestionCount = 10, Found = 10,
            Faithfulness = faithfulness, CitationPrecision = 0.95m, StartedAt = start.AddDays(day), FinishedAt = start.AddDays(day).AddMinutes(10),
        };
        var nights = new[] { Night(0, 0.9m), Night(1, 0.9m), Night(2, 0.92m), Night(3, 0.82m) };
        db.EvaluationRuns.AddRange(nights);
        // An unfinished run (a refused key) is never the latest.
        db.EvaluationRuns.Add(new EvaluationRun
        {
            SetId = null, Classification = Classification.Internal, Kind = EvaluationRunKinds.Answers, SubjectId = assistant.Id, SubjectVersion = 2, QuestionCount = 10,
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var other = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);

        var tests = (await ItemsAsync(admin, AttentionKinds.AssistantTestsWorse, assistant.Id)).ShouldHaveSingleItem().GetProperty("details");
        (tests.GetProperty("asExpected").GetString(), tests.GetProperty("before").GetString()).ShouldBe(("2", "3"));
        var answers = (await ItemsAsync(admin, AttentionKinds.AnswersBelowMedian, assistant.Id)).ShouldHaveSingleItem().GetProperty("details");
        (answers.GetProperty("metric").GetString(), answers.GetProperty("median").GetString(), answers.GetProperty("points").GetString()).ShouldBe(("faithfulness", "0.9", "8.0"));
        (await ItemsAsync(other, AttentionKinds.AnswersBelowMedian, assistant.Id)).ShouldBeEmpty();

        await db.EvaluationRuns.Where(r => r.Id == after.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Found, 3), Ct);
        await db.EvaluationRuns.Where(r => r.Id == nights[3].Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Faithfulness, 0.88m), Ct);
        (await ItemsAsync(admin, AttentionKinds.AssistantTestsWorse, assistant.Id)).ShouldBeEmpty();
        (await ItemsAsync(admin, AttentionKinds.AnswersBelowMedian, assistant.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_document_cited_in_three_downvoted_answers_in_seven_days_is_raised_for_the_owning_department_only_and_gone_when_fewer()
    {
        // §5.5.1, §8.8 (Decisions "Downvotes per document"): summed over every assistant that cited it, the owning department's
        // item; a department the knowledge base is shared with does not get it. Days well inside and outside the window.
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var shared = new Department { Id = Guid.CreateVersion7(), Name = "Delad " + seed.Code, CostCenterCode = "S" + seed.Code, SyncedAt = DateTimeOffset.UtcNow };
        Document Page(string slug, DateTimeOffset? deleted = null)
        {
            var url = $"https://www.umea.se/{seed.Code}/{slug}";
            return new Document
            {
                SourceId = seed.Source.Id, Classification = Classification.Internal, IdentityKind = "url", CanonicalIdentity = url, Url = url, Title = "Sida " + slug,
                IdentityHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)), DeletedAt = deleted,
            };
        }

        var (cited, fewer, deleted) = (Page("altan"), Page("plank"), Page("garage", DateTimeOffset.UtcNow));
        db.AddRange(shared, cited, fewer, deleted);
        await db.Collections.Where(c => c.Id == seed.Collection.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.SharedWith, CollectionSharingLevels.Municipality), Ct);
        var today = StockholmTime.DayOf(DateTimeOffset.UtcNow);
        var (own, foreign) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        DocumentDailyFeedback Row(Document document, int daysAgo, Guid assistant, Guid department, int down) => new()
        {
            Day = today.AddDays(-daysAgo), AssistantId = assistant, ClientId = Guid.CreateVersion7(), Classification = Classification.Internal,
            CollectionId = seed.Collection.Id, DocumentId = document.Id, DepartmentId = department, ThumbsDown = down, ThumbsUp = 1,
        };
        db.DocumentDailyFeedback.AddRange(
            Row(cited, 1, own, seed.Department.Id, 2),
            Row(cited, 3, foreign, shared.Id, 1), // the shared department's assistant counts too
            Row(fewer, 1, own, seed.Department.Id, 2),
            Row(fewer, 10, own, seed.Department.Id, 5), // outside the 7 days
            Row(deleted, 1, own, seed.Department.Id, 5)); // a deleted document needs no attention
        await db.SaveChangesAsync(Ct);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var sharedAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, shared.CostCenterCode);

        var item = (await ItemsAsync(admin, AttentionKinds.DocumentDownvoted, seed.Collection.Id)).ShouldHaveSingleItem();
        (item.GetProperty("objectType").GetString(), item.GetProperty("objectName").GetString(), item.GetProperty("departmentId").GetGuid())
            .ShouldBe((AttentionObjects.Collection, seed.Collection.Name, seed.Department.Id));
        var details = item.GetProperty("details");
        (details.GetProperty("documentId").GetString(), details.GetProperty("documentTitle").GetString(), details.GetProperty("downvotes").GetString(),
            details.GetProperty("days").GetString()).ShouldBe((cited.Id.ToString(), "Sida altan", "3", "7"));
        (await ItemsAsync(sharedAdmin, AttentionKinds.DocumentDownvoted, seed.Collection.Id)).ShouldBeEmpty();

        await db.DocumentDailyFeedback.Where(f => f.DocumentId == cited.Id && f.AssistantId == foreign).ExecuteUpdateAsync(u => u.SetProperty(f => f.ThumbsDown, 0), Ct);
        (await ItemsAsync(admin, AttentionKinds.DocumentDownvoted, seed.Collection.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_feedback_import_and_a_platform_model_out_of_the_ceiling_are_platform_admin_items()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var seed = await SeedAsync(db);
        var client = new Client
        {
            Name = "Open WebUI " + seed.Code, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(client);
        await db.SaveChangesAsync(Ct);
        db.FeedbackImportState.Add(new FeedbackImportState { ClientId = client.Id, FailedRuns = FeedbackImportState.AlertAfterFailedRuns, LastOutcome = "unreachable" });
        await db.SaveChangesAsync(Ct);
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings());
            await db.SaveChangesAsync(Ct);
        }

        var judge = await db.Settings.AsNoTracking().Select(s => s.EvaluationJudgeModel).FirstAsync(Ct);
        var lost = "fors/gone-" + seed.Code.ToLowerInvariant();
        await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(s => s.EvaluationJudgeModel, lost), Ct);
        try
        {
            await using var factory = Factory();
            using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
            using var department = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

            (await ItemsAsync(platform, AttentionKinds.FeedbackImportFailing, client.Id)).ShouldHaveSingleItem().GetProperty("details").GetProperty("failed").GetString().ShouldBe("4");
            (await ItemsAsync(platform, AttentionKinds.PlatformModel)).ShouldContain(i => i.GetProperty("objectName").GetString() == "Settings.EvaluationJudgeModel"
                && i.GetProperty("details").GetProperty("model").GetString() == lost);
            (await ItemsAsync(department, AttentionKinds.FeedbackImportFailing)).ShouldBeEmpty();
            (await ItemsAsync(department, AttentionKinds.PlatformModel)).ShouldBeEmpty();

            await db.FeedbackImportState.Where(f => f.ClientId == client.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.FailedRuns, 0), Ct);
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(s => s.EvaluationJudgeModel, judge), Ct);
            (await ItemsAsync(platform, AttentionKinds.FeedbackImportFailing, client.Id)).ShouldBeEmpty();
            (await ItemsAsync(platform, AttentionKinds.PlatformModel)).ShouldNotContain(i => i.GetProperty("details").GetProperty("model").GetString() == lost);
        }
        finally
        {
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(s => s.EvaluationJudgeModel, judge), Ct);
        }
    }

    [Fact]
    public void A_sync_is_raised_after_three_failures_in_a_row_and_the_median_takes_the_middle()
    {
        var at = DateTimeOffset.UtcNow;
        AttentionItem[] Items(int organisation, int models) =>
        [
            .. AttentionList.Syncs(new OrgSyncStatus(true, at, at, organisation, null, null, null, null), new GatewayModelSyncStatus(true, at, at, models, null, null, null)),
        ];
        Items(2, 0).ShouldBeEmpty();
        Items(3, 0).ShouldHaveSingleItem().ObjectName.ShouldBe("organisation");
        Items(0, 5).ShouldHaveSingleItem().Details["failures"].ShouldBe("5");
        AttentionList.Syncs(new OrgSyncStatus(false, null, null, 9, null, null, null, null), new GatewayModelSyncStatus(false, null, null, 9, null, null, null)).ShouldBeEmpty();
        AttentionList.Median([0.9m, 0.8m, 0.85m]).ShouldBe(0.85m);
        AttentionList.Median([0.9m, 0.8m]).ShouldBe(0.85m);
    }
}
