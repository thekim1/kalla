using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The setup checklist "Kom igång" (admin design §5.4, §5.5.2) computed from state, and Home (§5.5.1, "P1's Home"): the
/// collections whose checklist is not done, the published assistants' last 7 days (Live), the runs, queue and syncs (Health),
/// and the reminder before a wizard-stored client secret expires (§5.11).
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
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
        var profile = await ProfileAsync(db, code);

        Collection Make(string name, DateTimeOffset? published = null) => new()
        {
            Slug = $"setup-{name}-{code.ToLowerInvariant()}", Name = $"{name} {code}", DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, PublishedAt = published, UpdatedAt = DateTimeOffset.UtcNow,
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

    /// <summary>The shared database's first embedding profile, or a new default one (saved with the caller's next save).</summary>
    private static async Task<Guid> ProfileAsync(RagDbContext db, string code)
    {
        if (await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct) is { } existing)
        {
            return existing;
        }

        var created = new EmbeddingProfile
        {
            Name = "setup-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.EmbeddingProfiles.Add(created);
        return created.Id;
    }

    private sealed record LiveSeed(
        string Code, string OtherCode, Guid Busy, Guid Quiet, Guid Other, Guid Department, DateTimeOffset OldestQueued);

    /// <summary>
    /// Two departments. In the first: <c>Quiet</c> (published version 2; 5 questions over two clients within the 7 days,
    /// one not found, a thumb each way, 1.75 USD of which 0.25 estimated, and a day just outside the window), <c>Busy</c>
    /// (10 questions today), a draft-only and an archived published assistant (with totals of their own), and a collection
    /// whose source has runs of every state within 24 hours and one older; two queued jobs, a running and a finished one.
    /// In the other: a published assistant, a failed run and a queued job.
    /// </summary>
    private async Task<LiveSeed> SeedLiveAsync()
    {
        var code = "L" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime);
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Hem " + code, CostCenterCode = code, SyncedAt = now };
        var elsewhere = new Department { Id = Guid.CreateVersion7(), Name = "Hem annan " + code, CostCenterCode = "Y" + code, SyncedAt = now };
        var profile = await ProfileAsync(db, code);
        Collection Base(Department owner, string name) => new()
        {
            Slug = $"home-{name}-{code.ToLowerInvariant()}", Name = $"{name} {code}", DepartmentId = owner.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, UpdatedAt = now,
        };
        var own = Base(department, "egen");
        var theirs = Base(elsewhere, "annan");
        var pipeline = new Pipeline { Name = "home-" + code, TemplateId = "url-list", UpdatedAt = now };
        Source SourceOf(Collection collection) => new()
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = now,
        };
        var source = SourceOf(own);
        var otherSource = SourceOf(theirs);
        Assistant Make(Department owner, string name, string state = AssistantStates.Active) => new()
        {
            Slug = $"home-{name.ToLowerInvariant()}-{code.ToLowerInvariant()}", Name = $"{name} {code}", DepartmentId = owner.Id, Classification = Classification.Internal,
            State = state, UpdatedAt = now,
        };
        var quiet = Make(department, "Quiet");
        var busy = Make(department, "Busy");
        var draftOnly = Make(department, "Draft");
        var archived = Make(department, "Archived", AssistantStates.Archived);
        var other = Make(elsewhere, "Other");
        var web = new Client { Name = "Open WebUI " + code, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
        var console = new Client { Name = "Console " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
        db.AddRange(department, elsewhere, own, theirs, pipeline, source, otherSource, quiet, busy, draftOnly, archived, other, web, console);

        Run RunOf(Source of, string state, double hoursAgo) => new()
        {
            SourceId = of.Id, Classification = Classification.Internal, Kind = "sync", Trigger = "schedule", State = state, StartedAt = now.AddHours(-hoursAgo),
        };
        db.AddRange(
            RunOf(source, RunStates.Succeeded, 1), RunOf(source, RunStates.Degraded, 2), RunOf(source, RunStates.AwaitingDeletionConfirmation, 3),
            RunOf(source, RunStates.Failed, 4), RunOf(source, RunStates.Running, 0), RunOf(source, RunStates.Cancelled, 1),
            RunOf(source, RunStates.Failed, 25), // before the 24 hours
            RunOf(otherSource, RunStates.Failed, 1));

        Job JobOf(Department owner, string state, double hoursAgo) => new()
        {
            Class = JobClasses.Sync, DepartmentId = owner.Id, Classification = Classification.Internal, Payload = "{}", EnqueuedAt = now.AddHours(-hoursAgo),
            AvailableAt = now.AddHours(-hoursAgo), State = state,
        };
        var oldest = JobOf(department, JobStates.Queued, 2);
        db.AddRange(oldest, JobOf(department, JobStates.Queued, 0.1), JobOf(department, JobStates.Running, 5), JobOf(department, JobStates.Succeeded, 6),
            JobOf(elsewhere, JobStates.Queued, 7));

        AssistantDailyTotal Totals(Assistant a, Client c, int daysAgo, int messages, int notFound = 0, int up = 0, int down = 0) => new()
        {
            Day = today.AddDays(-daysAgo), AssistantId = a.Id, ClientId = c.Id, DepartmentId = a.DepartmentId, Classification = Classification.Internal,
            Conversations = 1, Messages = messages, NotFound = notFound, ThumbsUp = up, ThumbsDown = down,
        };
        AssistantDailyUsage Usage(Assistant a, Client c, int daysAgo, decimal cost, decimal estimated = 0m) => new()
        {
            Day = today.AddDays(-daysAgo), AssistantId = a.Id, ClientId = c.Id, DepartmentId = a.DepartmentId, Classification = Classification.Internal,
            Step = "chat-answer", Calls = 1, InputTokens = 10, OutputTokens = 5, CostUsd = cost, EstimatedCostUsd = estimated, EstimatedCalls = estimated > 0 ? 1 : 0,
        };
        db.AddRange(
            Totals(quiet, web, 0, 3, notFound: 1, up: 1), Totals(quiet, console, 6, 2, down: 1), Totals(quiet, web, 7, 100, notFound: 50),
            Totals(busy, web, 0, 10), Totals(draftOnly, web, 0, 40), Totals(archived, web, 0, 30), Totals(other, web, 0, 20));
        db.AddRange(
            Usage(quiet, web, 0, 0.5m, 0.25m), Usage(quiet, console, 6, 1m), Usage(quiet, web, 7, 50m), Usage(busy, web, 0, 0.1m), Usage(other, web, 0, 9m));
        await db.SaveChangesAsync(Ct);

        AssistantVersion Version(Assistant a, int number) => new()
        {
            AssistantId = a.Id, Number = number, State = AssistantVersionStates.Published, GatewayModel = "ume/chat-eu", CollectionLinks = [CollectionLink.Whole(own.Id)],
            PublishedAt = now, PublishedBy = "seed", CreatedAt = now, UpdatedAt = now,
        };
        var quietV1 = Version(quiet, 1);
        quietV1.State = AssistantVersionStates.Superseded;
        var quietV2 = Version(quiet, 2);
        var busyV1 = Version(busy, 1);
        var archivedV1 = Version(archived, 1);
        var otherV1 = Version(other, 1);
        var draft = Version(draftOnly, 1);
        draft.State = AssistantVersionStates.Draft;
        db.AddRange(quietV1, quietV2, busyV1, archivedV1, otherV1, draft);
        await db.SaveChangesAsync(Ct);
        quiet.PublishedVersionId = quietV2.Id;
        busy.PublishedVersionId = busyV1.Id;
        archived.PublishedVersionId = archivedV1.Id;
        other.PublishedVersionId = otherV1.Id;
        draftOnly.DraftVersionId = draft.Id;
        await db.SaveChangesAsync(Ct);
        return new LiveSeed(code, elsewhere.CostCenterCode, busy.Id, quiet.Id, other.Id, department.Id, oldest.EnqueuedAt);
    }

    private static (Guid Id, int Version, int Questions, int NotFound, int Up, int Down, decimal Cost) LiveRow(JsonElement l) =>
        (l.GetProperty("assistantId").GetGuid(), l.GetProperty("publishedVersion").GetInt32(), l.GetProperty("questions").GetInt32(), l.GetProperty("notFound").GetInt32(),
            l.GetProperty("thumbsUp").GetInt32(), l.GetProperty("thumbsDown").GetInt32(), l.GetProperty("costUsd").GetDecimal());

    [Fact]
    public async Task Live_sums_the_last_7_days_of_the_caller_s_published_active_assistants_most_questions_first()
    {
        var seed = await SeedLiveAsync();
        await using var factory = database.HeaderFactory();
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var otherAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OtherCode);

        var live = (await viewer.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("live");
        // The draft-only and the archived assistant are left out, as is the day before the 7 days; every client counts.
        live.EnumerateArray().Select(LiveRow).ShouldBe(
        [
            (seed.Busy, 1, 10, 0, 0, 0, 0.1m),
            (seed.Quiet, 2, 5, 1, 1, 1, 1.75m),
        ]);
        (live[0].GetProperty("name").GetString(), live[0].GetProperty("departmentId").GetGuid()).ShouldBe(($"Busy {seed.Code}", seed.Department));

        // Another department's admin sees only their own assistant.
        (await otherAdmin.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("live").EnumerateArray().Select(LiveRow)
            .ShouldBe([(seed.Other, 1, 20, 0, 0, 0, 9m)]);
    }

    [Fact]
    public async Task Health_counts_the_department_s_runs_of_24_hours_by_outcome_and_its_queued_jobs()
    {
        var seed = await SeedLiveAsync();
        await using var factory = database.HeaderFactory();
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);

        var health = (await viewer.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("health");
        // Only the caller's department; the cancelled run and the one 25 hours ago don't count, the held one is with remarks.
        var department = health.GetProperty("departments").EnumerateArray().ShouldHaveSingleItem();
        (department.GetProperty("departmentId").GetGuid(), department.GetProperty("departmentName").GetString()).ShouldBe((seed.Department, $"Hem {seed.Code}"));
        var runs = department.GetProperty("runs");
        (runs.GetProperty("succeeded").GetInt32(), runs.GetProperty("degraded").GetInt32(), runs.GetProperty("failed").GetInt32(), runs.GetProperty("running").GetInt32())
            .ShouldBe((1, 2, 1, 1));
        // The two queued jobs of the department; neither the running and finished ones nor the other department's.
        var queue = health.GetProperty("queue");
        queue.GetProperty("waiting").GetInt32().ShouldBe(2);
        queue.GetProperty("oldestQueuedAt").GetDateTimeOffset().ShouldBe(seed.OldestQueued, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Health_shows_each_gateway_sync_s_last_success_and_failures()
    {
        var seed = await SeedLiveAsync();
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://refusing-gateway/");
            web.UseSetting("Gateway:Key", "refused-key");
            web.ConfigureTestServices(services =>
            {
                services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RefusingGateway());
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RefusingGateway());
            });
        });
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);

        (await factory.Services.GetRequiredService<OrganisationSync>().RunAsync(null, Ct)).ShouldBeNull();

        var health = (await viewer.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("health");
        var organisation = health.GetProperty("organisationSync");
        (organisation.GetProperty("lastSuccessAt").ValueKind, organisation.GetProperty("consecutiveFailures").GetInt32()).ShouldBe((JsonValueKind.Null, 1));
        var models = health.GetProperty("modelSync");
        (models.GetProperty("lastSuccessAt").ValueKind, models.GetProperty("consecutiveFailures").GetInt32()).ShouldBe((JsonValueKind.Null, 0));
    }

    /// <summary>A gateway that refuses every call with <c>401 invalid_api_key</c>.</summary>
    private sealed class RefusingGateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"code":"invalid_api_key","message":"Invalid API key."}}""", Encoding.UTF8, "application/json"),
            });
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
    public async Task A_platform_admin_is_reminded_of_wizard_stored_client_secrets_that_expire_within_30_days()
    {
        var connection = database.Connection(null);
        var today = StockholmTime.DayOf(DateTimeOffset.UtcNow);
        await SetupHosts.ResetAsync(connection, completed: true);
        // Both secrets from the wizard: the deployment sets none, and it runs Open WebUI.
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Oidc:ClientSecret", "");
            web.UseSetting(OpenWebUiSignIn.EnabledKey, "true");
        });
        var protector = factory.Services.GetRequiredService<SettingsSecretProtector>();
        try
        {
            using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
            using var departmentAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "1000");

            // The 30th day is within, an expired one is listed too.
            await StoreSecretsAsync(connection, protector, today.AddDays(SetupEndpoints.SecretExpiryDays), today.AddDays(-3));
            (await SecretExpiryAsync(admin)).ShouldBe([(SetupTargets.RagAdmin, today.AddDays(30)), (SetupTargets.OpenWebUi, today.AddDays(-3))]);
            // Only for a platform admin.
            (await SecretExpiryAsync(departmentAdmin)).ShouldBeEmpty();

            // The 31st day is beyond.
            await StoreSecretsAsync(connection, protector, today.AddDays(31), today);
            (await SecretExpiryAsync(admin)).ShouldBe([(SetupTargets.OpenWebUi, today)]);

            // A deployment's secret wins, and its date is not known: the wizard's date no longer counts.
            await using var deployed = database.HeaderFactory().WithWebHostBuilder(web =>
            {
                web.UseSetting(OpenWebUiSignIn.EnabledKey, "true");
                web.UseSetting("OpenWebUi:Oidc:ClientSecret", "deployment-secret");
            });
            using var deployedAdmin = await AdminDatabaseFixture.UserAsync(deployed, RagRoles.PlatformAdmin);
            await StoreSecretsAsync(connection, protector, today.AddDays(5), today.AddDays(5));
            (await SecretExpiryAsync(deployedAdmin)).ShouldBeEmpty();

            // Open WebUI's secret counts only while the deployment runs Open WebUI.
            await using var withoutChat = database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Oidc:ClientSecret", ""));
            using var withoutChatAdmin = await AdminDatabaseFixture.UserAsync(withoutChat, RagRoles.PlatformAdmin);
            (await SecretExpiryAsync(withoutChatAdmin)).ShouldBe([(SetupTargets.RagAdmin, today.AddDays(5))]);
        }
        finally
        {
            await SetupHosts.ResetAsync(connection, completed: true);
        }
    }

    private static async Task<(string Which, DateOnly ExpiresOn)[]> SecretExpiryAsync(HttpClient client) =>
    [
        .. (await client.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("secretExpiry").EnumerateArray()
            .Select(e => (e.GetProperty("which").GetString()!, DateOnly.Parse(e.GetProperty("expiresOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture))),
    ];

    /// <summary>Stores a wizard client secret for rag-admin and for Open WebUI with the given expiry dates, as setup's <c>complete</c> stores them.</summary>
    private static async Task StoreSecretsAsync(string connection, SettingsSecretProtector protector, DateOnly adminExpires, DateOnly chatExpires)
    {
        var identity = new OidcWizardValues { ClientSecretExpiresOn = adminExpires }.ToJson();
        var chat = new OpenWebUiOidcValues { ClientSecretExpiresOn = chatExpires }.ToJson();
        var adminSecret = protector.Protect(StoredSecret.IdentityOidcClientSecret, "wizard-admin-secret");
        var chatSecret = protector.Protect(StoredSecret.OpenWebUiOidcClientSecret, "wizard-chat-secret");
        var setAt = DateTimeOffset.UtcNow;
        await using var db = TestPostgres.Context(connection);
        await db.Settings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.IdentityOidc, identity)
            .SetProperty(x => x.IdentityOidcEncryptedClientSecret, adminSecret)
            .SetProperty(x => x.IdentityOidcClientSecretSetAt, setAt)
            .SetProperty(x => x.OpenWebUiOidc, chat)
            .SetProperty(x => x.OpenWebUiOidcEncryptedClientSecret, chatSecret)
            .SetProperty(x => x.OpenWebUiOidcClientSecretSetAt, setAt), Ct);
    }

    [Fact]
    public async Task The_source_list_carries_each_source_s_live_document_count_and_latest_run()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);

        var source = (await viewer.GetFromJsonAsync<JsonElement[]>($"/api/collections/{seed.Halfway}/sources", Ct))!.ShouldHaveSingleItem();
        // Live documents only (#374): "altan" is not deleted but has no live chunk set, "borttagen" is deleted; neither counts.
        // OrganisationSharingTests counts a live one.
        source.GetProperty("documents").GetInt32().ShouldBe(0);
        var lastRun = source.GetProperty("lastRun");
        (lastRun.GetProperty("id").GetGuid(), lastRun.GetProperty("state").GetString()).ShouldBe((seed.RunningRun, RunStates.Running));
        lastRun.GetProperty("finishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
