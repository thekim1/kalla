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
using FakeUsage = fakegateway::Ume.Rag.FakeGateway.FakeUsage;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The test panel's usage (admin design §4.9, cost-attribution §5 C2; B-39): every model call of a test turn is upserted into
/// <c>AssistantDailyUsage</c> under the assistant and the <c>admin-console</c> client row with the FakeGateway's tokens and
/// cost, never into <c>AssistantDailyTotals</c>; the response's <c>costUsd</c> is the sum of the turn's calls and
/// <c>costEstimated</c> is true on <c>fake-no-cost</c>; a failed write leaves the answer intact.
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class AssistantTestPanelUsageTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "sk-ume-eu-panel-0000000000000000"; // rag-admin's credential key
    private static readonly string[] PanelSteps = ["condense", "condense-long", "query-embed", "rerank", "chat-answer"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Key", AdminKey);
        web.ConfigureTestServices(services =>
        {
            foreach (var name in new[] { GatewayChatClient.HttpClientName, GatewayQueryEmbedder.HttpClientName, Ume.Rag.Retrieval.ChatModelReranker.HttpClientName })
            {
                services.UseGatewayCredentialCeiling();
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            }
        });
    });

    [Fact]
    public async Task A_test_turn_writes_its_steps_under_admin_console_and_no_totals()
    {
        var code = "U" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var adminConsole = await AdminConsoleAsync(db);
        var assistant = await SeedAsync(db, code);

        await using var gateway = FakeGateway();
        await using var factory = Admin(gateway);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        var path = $"/api/assistants/{assistant.Id}/test";

        // The gateway's cost: every step's row equals what the FakeGateway served and charged.
        using (var response = await editor.PostAsJsonAsync(path, new { question = $"Hur komposterar jag {code}?" }, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var call = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("gateway");
            var served = gateway.Services.GetRequiredService<FakeUsage>().Records.Where(r => PanelSteps.Contains(r.Step)).ToList();
            served.Select(r => r.Step).ShouldContain("query-embed");
            served.Select(r => r.Step).ShouldContain("chat-answer");
            call.GetProperty("costUsd").GetDecimal().ShouldBe(served.Sum(r => r.CostUsd));
            call.GetProperty("costEstimated").GetBoolean().ShouldBeFalse();

            var rows = await db.AssistantDailyUsage.AsNoTracking().Where(u => u.AssistantId == assistant.Id).ToListAsync(Ct);
            rows.ShouldAllBe(u => u.ClientId == adminConsole && u.Classification == Classification.Internal && u.DepartmentId == assistant.DepartmentId);
            rows.Select(u => u.Step).Order(StringComparer.Ordinal).ShouldBe(served.Select(r => r.Step!).Distinct().Order(StringComparer.Ordinal));
            foreach (var row in rows)
            {
                var step = served.Where(r => r.Step == row.Step).ToList();
                (row.Calls, row.InputTokens, row.OutputTokens, row.CostUsd, row.EstimatedCalls, row.EstimatedCostUsd)
                    .ShouldBe((step.Count, step.Sum(r => r.InputTokens), step.Sum(r => r.OutputTokens), step.Sum(r => r.CostUsd), 0, 0m), row.Step);
            }
        }

        (await db.AssistantDailyTotals.AsNoTracking().CountAsync(t => t.AssistantId == assistant.Id, Ct)).ShouldBe(0);
        (await db.Conversations.AsNoTracking().CountAsync(c => c.AssistantId == assistant.Id, Ct)).ShouldBe(0);

        // fake-no-cost: the answer has no gateway cost, so it is estimated and counted in EstimatedCalls (the same question: the
        // query embedding is cached and adds nothing).
        await db.AssistantVersions.Where(v => v.AssistantId == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.GatewayModel, "fake-no-cost"), Ct);
        using (var response = await editor.PostAsJsonAsync(path, new { question = $"Hur komposterar jag {code}?" }, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var body = await response.Content.ReadAsStringAsync(Ct);
            JsonDocument.Parse(body).RootElement.GetProperty("gateway").GetProperty("costEstimated").GetBoolean().ShouldBeTrue(body);
            (await db.AssistantDailyUsage.AsNoTracking().SingleAsync(u => u.AssistantId == assistant.Id && u.Step == "query-embed", Ct)).Calls.ShouldBe(1);
            var answer = await db.AssistantDailyUsage.AsNoTracking().SingleAsync(u => u.AssistantId == assistant.Id && u.Step == "chat-answer", Ct);
            (answer.Calls, answer.EstimatedCalls).ShouldBe((2, 1));
        }

        (await db.AssistantDailyTotals.AsNoTracking().CountAsync(t => t.AssistantId == assistant.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_usage_write_leaves_the_answer_intact()
    {
        var code = "F" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        await AdminConsoleAsync(db);
        var assistant = await SeedAsync(db, code);
        var trigger = "panel_usage_fails_" + code.ToLowerInvariant();
        // Only this assistant's rows fail, so the other tests of the group are not touched.
        // DDL takes no parameters; the names and the id are the test's own.
        var create = $"""
            CREATE FUNCTION chat.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."AssistantId" = '{assistant.Id}' THEN RAISE EXCEPTION 'usage write refused'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON chat."AssistantDailyUsage" FOR EACH ROW EXECUTE FUNCTION chat.{trigger}();
            """;
        await db.Database.ExecuteSqlRawAsync(create, Ct);
        try
        {
            await using var gateway = FakeGateway();
            await using var factory = Admin(gateway);
            using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
            using var response = await editor.PostAsJsonAsync($"/api/assistants/{assistant.Id}/test", new { question = $"Hur komposterar jag {code}?" }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            result.GetProperty("answer").GetProperty("delivered").GetString().ShouldNotBeNullOrWhiteSpace();
            result.GetProperty("gateway").GetProperty("costUsd").GetDecimal().ShouldBeGreaterThan(0m);
            (await db.AssistantDailyUsage.AsNoTracking().CountAsync(u => u.AssistantId == assistant.Id, Ct)).ShouldBe(0);
        }
        finally
        {
            var drop = $"""
                DROP TRIGGER {trigger} ON chat."AssistantDailyUsage";
                DROP FUNCTION chat.{trigger}();
                """;
            await db.Database.ExecuteSqlRawAsync(drop, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_disabled_key_shows_chats_reason_on_the_panel_and_makes_no_model_call()
    {
        // B-44 (chat §3.6): the panel runs chat's step 7, so an unusable key in the mirror refuses before any call, with chat's reason.
        var code = "D" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var assistant = await SeedAsync(db, code);
        var key = Guid.CreateVersion7();
        db.GatewayKeys.Add(new GatewayKey
        {
            Id = key, Name = "Panel " + code, TeamId = Guid.CreateVersion7(), DepartmentId = assistant.DepartmentId, Status = "Disabled", OwnerActive = true,
            Usable = true, PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.GatewayKeyId, key), Ct);

        await using var gateway = FakeGateway();
        await using var factory = Admin(gateway);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);
        using var response = await editor.PostAsJsonAsync($"/api/assistants/{assistant.Id}/test", new { question = $"Hur komposterar jag {code}?" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("test_assistant_unavailable");
        problem.GetProperty("params").GetProperty("reason").GetString().ShouldBe("The assistant is unavailable: its gateway key is disabled.");
        gateway.Services.GetRequiredService<FakeUsage>().Records.ShouldBeEmpty();
    }

    private static WebApplicationFactory<FakeGatewayProgram> FakeGateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
    });

    /// <summary>The <c>admin-console</c> row of identity §4.1 (as the dev seed adds it), inserted once for the group.</summary>
    private static async Task<Guid> AdminConsoleAsync(Infrastructure.Persistence.RagDbContext db)
    {
        var id = await db.Clients.AsNoTracking().Where(c => c.Name == AdminTestPanel.ClientName && c.Kind == ClientKinds.Internal).Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(Ct);
        if (id is { } known)
        {
            return known;
        }

        var row = new Client
        {
            Name = AdminTestPanel.ClientName, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Sensitive, UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        db.Clients.Add(row);
        await db.SaveChangesAsync(Ct);
        return row.Id;
    }

    /// <summary>An assistant with a draft on <c>ume/chat-eu</c> over an unpublished collection with one document, embedded as the FakeGateway embeds.</summary>
    private async Task<Assistant> SeedAsync(Infrastructure.Persistence.RagDbContext db, string code)
    {
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin),
            (text, dims) => fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector(text, dims));
        var profile = await writer.AddEmbeddingProfileAsync(1024, Ct);
        await db.EmbeddingProfiles.Where(p => p.Id == profile).ExecuteUpdateAsync(u => u.SetProperty(p => p.GatewayModel, "bge-m3"), Ct);
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Usage " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "usage-" + code.ToLowerInvariant(), Name = "Kompost " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var assistant = new Assistant
        {
            Slug = "usage-" + code.ToLowerInvariant(), Name = "Kompostguiden " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var draft = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", SystemPrompt = "Svara kort.", CollectionLinks = [CollectionLink.Whole(collection.Id)],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "usage-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
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
        await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Kompostering",
            Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk($"Så komposterar du {code} hemma i Umeå.") { HeadingPath = "Avfall › Kompost" }], Ct);
        return assistant;
    }
}
