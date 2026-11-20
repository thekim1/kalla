extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using FakeGatewayOptions = fakegateway::Kalla.FakeGateway.FakeGatewayOptions;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// B-43 (#580, gateway phase 2; admin §4.5, §4.6 V8, V39; cost-attribution §4.2): once the admin's context shows
/// <c>act-as-key</c>, V39 checks an object's models with <c>/v1/models</c> as its key, the picker's availability follows the
/// key, publish needs a key, a knowledge base without one still saves, and the status makes one acted call.
/// </summary>
[Collection(AdminDatabaseGroup8.Name)]
public sealed class ChecksAsKeyTests(AdminDatabaseFixture database, ChecksAsKeyTests.Host host) : IClassFixture<ChecksAsKeyTests.Host>
{
    private const string AdminKey = "sk-ume-checks-as-key-0000000000000";
    private const string AnswerModel = "fors/chat-eu";
    private const string CondenseModel = "fors/chat-standard";

    // Assigned keys of the FakeGateway's integration, added for these tests (indexes after its five dev keys).
    private static readonly Guid AnswerOnly = new("019a0000-0000-7000-8000-00000000b0c1"); // allows only the answer model
    private static readonly Guid Disabled = new("019a0000-0000-7000-8000-00000000b0c2");
    private static readonly Guid Full = new("019a0000-0000-7000-8000-00000000b0c3");
    private static readonly Guid EuOnly = new("019a0000-0000-7000-8000-00000000b0c4"); // every model, at Eu only (the dev chatbot key's case, #720)

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<FakeGatewayProgram> Gateway(bool acting = true, bool dropEcho = false) =>
        new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
            web.UseSetting("FakeGateway:DropOnBehalfOfEcho", dropEcho ? "true" : "false");
            foreach (var (n, id, status, model) in new[] { (5, AnswerOnly, "Active", AnswerModel), (6, Disabled, "Disabled", (string?)null), (7, Full, "Active", null) })
            {
                web.UseSetting($"FakeGateway:AssignedKeys:{n}:Id", id.ToString());
                web.UseSetting($"FakeGateway:AssignedKeys:{n}:Name", $"Checks {n}");
                web.UseSetting($"FakeGateway:AssignedKeys:{n}:TeamId", "019a0000-0000-7000-8000-000000001002");
                web.UseSetting($"FakeGateway:AssignedKeys:{n}:Status", status);
                if (model is not null)
                {
                    web.UseSetting($"FakeGateway:AssignedKeys:{n}:AllowedModels:0", model);
                }
            }

            web.UseSetting("FakeGateway:AssignedKeys:8:Id", EuOnly.ToString());
            web.UseSetting("FakeGateway:AssignedKeys:8:Name", "Checks 8");
            web.UseSetting("FakeGateway:AssignedKeys:8:TeamId", "019a0000-0000-7000-8000-000000001002");
            web.UseSetting("FakeGateway:AssignedKeys:8:AllowedResidencies:0", "Eu");

            if (!acting)
            {
                // A gateway before phase 2 (the binder appends to a list with a default, so the feature is removed here).
                web.ConfigureTestServices(services => services.PostConfigure<FakeGatewayOptions>(o => o.Gateway.Features.Remove(GatewayFeatures.ActAsKeyFeature)));
            }
        });

    /// <summary>The class's admin host on a FakeGateway of its own (<see cref="Gateway"/> with its defaults).</summary>
    public sealed class Host(AdminDatabaseFixture database) : AdminHost(database)
    {
        private readonly WebApplicationFactory<FakeGatewayProgram> _gateway = Gateway();

        protected override WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> host) => OnGateway(host, _gateway);

        protected override ValueTask CleanupAsync() => _gateway.DisposeAsync();
    }

    /// <summary>The class's shared admin host, having read the gateway's context.</summary>
    private async Task<WebApplicationFactory<Program>> SharedAsync()
    {
        (await host.Factory.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        return host.Factory;
    }

    private static async Task<WebApplicationFactory<Program>> AdminAsync(AdminDatabaseFixture database, WebApplicationFactory<FakeGatewayProgram> gateway,
        CapturingLoggerProvider? logs = null)
    {
        var factory = OnGateway(database.HeaderFactory(), gateway, logs);
        (await factory.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        return factory;
    }

    private static WebApplicationFactory<Program> OnGateway(WebApplicationFactory<Program> admin, WebApplicationFactory<FakeGatewayProgram> gateway,
        CapturingLoggerProvider? logs = null) =>
        admin.WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", AdminKey);
            web.ConfigureTestServices(services =>
            {
                services.UseGatewayCredentialCeiling();
                services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
                if (logs is not null)
                {
                    services.AddLogging(b => b.AddProvider(logs));
                }
            });
        });

    private sealed record Seed(string Code, Guid Department, Guid Collection);

    /// <summary>A department with a published knowledge base, and the mirror rows of the three keys (Active and usable in the mirror).</summary>
    private async Task<Seed> SeedAsync()
    {
        var code = "C" + Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Checks " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var team = new Team { Id = Guid.CreateVersion7(), DepartmentId = department.Id, Name = "Team " + code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "checks-" + code.ToLowerInvariant(), Name = "Checks " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, team, collection);
        if (!await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault && p.State == EmbeddingProfileStates.Active, Ct))
        {
            db.EmbeddingProfiles.Add(new EmbeddingProfile
            {
                Name = "checks-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        foreach (var model in new[] { AnswerModel, CondenseModel })
        {
            if (!await db.GatewayModels.AnyAsync(m => m.Name == model, Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = model, Residencies = ["Eu"], Source = GatewayModelSources.Manual });
            }
        }

        foreach (var id in new[] { AnswerOnly, Disabled, Full })
        {
            await db.GatewayKeys.Where(k => k.Id == id).ExecuteDeleteAsync(Ct);
            db.GatewayKeys.Add(new GatewayKey
            {
                // "0…" sorts first, so the status's acted call takes the full key.
                Id = id, Name = id == Full ? "0 Checks full" : $"Checks {id.ToString()[^2..]}", TeamId = team.Id, DepartmentId = department.Id, Status = "Active",
                OwnerActive = true, Usable = true, PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(Ct);
        return new Seed(code, department.Id, collection.Id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>An assistant with a draft on the answer model linking the seed's knowledge base, and its contact (ready to publish).</summary>
    private static async Task<(Guid Id, JsonElement Detail)> AssistantAsync(HttpClient admin, Seed seed, Guid? key = null)
    {
        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = $"Checks {Guid.NewGuid():N}", departmentId = seed.Department, classification = Classification.Internal, description = "Svarar.",
            contactName = "Anna", contactEmail = "anna@umea.se", gatewayKeyId = key,
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var detail = await Json(created);
        var id = detail.GetProperty("id").GetGuid();
        using var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
        {
            gatewayModel = AnswerModel, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } },
            version = detail.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct);
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
        return (id, await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct));
    }

    private static Task<HttpResponseMessage> SetKeyAsync(HttpClient admin, Guid id, JsonElement detail, Guid? key) =>
        admin.PutAsJsonAsync($"/api/assistants/{id}", new
        {
            name = detail.GetProperty("name").GetString(), classification = Classification.Internal, description = "Svarar.", contactName = "Anna",
            contactEmail = "anna@umea.se", gatewayKeyId = key, version = detail.GetProperty("version").GetString(),
        }, Ct);

    /// <summary>Runs <paramref name="body"/> with <c>Settings.Chat.condenseModel</c> set to <see cref="CondenseModel"/>, then restores the setting.</summary>
    private async Task WithCondenseModelAsync(Func<Task> body)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var saved = await db.Settings.AsNoTracking().Select(x => x.Chat).SingleAsync(Ct);
        var chat = JsonNode.Parse(saved) as JsonObject ?? [];
        chat["condenseModel"] = CondenseModel;
        await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Chat, chat.ToJsonString()), Ct);
        try
        {
            await body();
        }
        finally
        {
            await db.Settings.ExecuteUpdateAsync(u => u.SetProperty(x => x.Chat, saved), Ct);
        }
    }

    [Fact]
    public async Task V39_as_the_key_names_the_condense_model_its_list_lacks_and_refuses_a_disabled_key()
    {
        var seed = await SeedAsync();
        var factory = await SharedAsync();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        var (id, detail) = await AssistantAsync(admin, seed);

        await WithCondenseModelAsync(async () =>
        {
            // The mirror allows every model for the key; the gateway's list as the key lacks the condense model.
            using var lacking = await SetKeyAsync(admin, id, detail, AnswerOnly);
            lacking.StatusCode.ShouldBe(HttpStatusCode.Conflict, await lacking.Content.ReadAsStringAsync(Ct));
            var problem = await Json(lacking);
            problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayKeyModel.Code);
            var affected = problem.GetProperty("affected").EnumerateArray().Select(a => a.GetString()).ToList();
            affected.ShouldContain(CondenseModel);
            affected.ShouldNotContain(AnswerModel);
        });

        using var disabled = await SetKeyAsync(admin, id, detail, Disabled);
        disabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var refused = await Json(disabled);
        refused.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayKeyInvalid.Code);
        refused.GetProperty("params").GetProperty("reason").GetString().ShouldBe("key_disabled");

        // The picker's availability as the key: what the key may use.
        var models = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/gateway/models?level=internal&gatewayKeyId={AnswerOnly}", Ct))!
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("available"));
        models[AnswerModel].GetBoolean().ShouldBeTrue();
        models[CondenseModel].GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task V39_as_the_key_names_the_embedding_model_the_key_allows_but_reaches_only_at_another_residency()
    {
        // #720: the key lists bge-m3 and allows Eu only; bge-m3 runs only at OnPrem, so the key's list at Eu lacks it.
        var seed = await SeedAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var profile = await db.Collections.Where(c => c.Id == seed.Collection).Select(c => c.EmbeddingProfileId).SingleAsync(Ct);
            await db.EmbeddingProfiles.Where(p => p.Id == profile).ExecuteUpdateAsync(u => u.SetProperty(p => p.GatewayModel, "bge-m3"), Ct);
            if (!await db.GatewayModels.AnyAsync(m => m.Name == "bge-m3", Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = "bge-m3", ModelKind = GatewayModelModelKinds.Embedding, Residencies = ["OnPrem"], Source = GatewayModelSources.Manual });
            }

            // As the mirror sync writes it from the FakeGateway's catalogue.
            await db.GatewayModels.Where(m => m.Name == "bge-m3")
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.ModelKind, GatewayModelModelKinds.Embedding).SetProperty(m => m.Residencies, new[] { "OnPrem" }), Ct);
            await db.GatewayKeys.Where(k => k.Id == EuOnly).ExecuteDeleteAsync(Ct);
            var team = await db.GatewayKeys.Where(k => k.Id == Full).Select(k => k.TeamId).SingleAsync(Ct);
            db.GatewayKeys.Add(new GatewayKey
            {
                Id = EuOnly, Name = "Checks Eu only", TeamId = team, DepartmentId = seed.Department, Status = "Active", OwnerActive = true, Usable = true,
                AllowedModels = [AnswerModel, "bge-m3"], AllowedResidencies = ["Eu"], PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var factory = await SharedAsync();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        var (id, detail) = await AssistantAsync(admin, seed);

        using var response = await SetKeyAsync(admin, id, detail, EuOnly);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        var problem = (await Json(response)).GetProperty("problems").EnumerateArray().Single();
        problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayKeyEmbeddingResidency.Code);
        var parameters = problem.GetProperty("params");
        parameters.GetProperty("key").GetString().ShouldBe("Checks Eu only");
        parameters.GetProperty("model").GetString().ShouldBe("bge-m3");
        parameters.GetProperty("modelResidencies").GetString().ShouldBe("OnPrem");
        parameters.GetProperty("keyResidencies").GetString().ShouldBe("Eu");
        problem.GetProperty("affected").EnumerateArray().Select(a => a.GetString()).ShouldBe(["bge-m3"]);
    }

    [Fact]
    public async Task A_missing_echo_on_the_save_check_is_gateway_unavailable_with_5011()
    {
        var seed = await SeedAsync();
        var logs = new CapturingLoggerProvider();
        await using var gateway = Gateway(dropEcho: true);
        await using var factory = await AdminAsync(database, gateway, logs);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        var (id, detail) = await AssistantAsync(admin, seed);

        using var response = await SetKeyAsync(admin, id, detail, Full);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Ct));
        (await Json(response)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayUnavailable.Code);
        logs.Entries.ShouldContain(e => e.EventId.Id == 5011);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publishing_without_a_key_is_refused_once_the_gateway_acts_as_keys_and_only_warned_before(bool acting)
    {
        var seed = await SeedAsync();
        await using var gateway = Gateway(acting);
        await using var factory = await AdminAsync(database, gateway);
        factory.Services.GetRequiredService<GatewayFeatures>().ActAsKey.ShouldBe(acting);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        var (id, detail) = await AssistantAsync(admin, seed);
        var draft = await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}/draft", Ct);
        draft.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ShouldContain(AdminErrorCodes.GatewayKeyRequired.Code);

        using var publish = await admin.PostAsJsonAsync($"/api/assistants/{id}/publish", new
        {
            version = detail.GetProperty("version").GetString(), draftVersion = draft.GetProperty("version").GetString(),
        }, Ct);

        if (acting)
        {
            publish.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Json(publish);
            problem.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ShouldContain(AdminErrorCodes.GatewayKeyRequired.Code);
        }
        else
        {
            publish.StatusCode.ShouldBe(HttpStatusCode.OK, await publish.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_published_assistants_key_cannot_be_cleared_once_the_gateway_acts_as_keys()
    {
        // #618 review: clearing the key would put the live assistant on kalla-api's credential key, outside its department's budget.
        var seed = await SeedAsync();
        var factory = await SharedAsync();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        var (id, _) = await AssistantAsync(admin, seed, Full);
        // Published as the draft (the fixture's 8-dimension profile model is not in the FakeGateway's catalogue, so V39 as the key
        // would refuse a real publish for a reason that is not this rule's).
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Assistants.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.PublishedVersionId, a => a.DraftVersionId), Ct);
        }

        var published = await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct);
        using var cleared = await SetKeyAsync(admin, id, published, null);
        cleared.StatusCode.ShouldBe(HttpStatusCode.Conflict, await cleared.Content.ReadAsStringAsync(Ct));
        (await Json(cleared)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayKeyRequired.Code);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct)).GetProperty("gatewayKeyId").GetGuid().ShouldBe(Full);

        // A draft-only assistant may still drop its key: publishing it is what needs one.
        var (draftOnly, draftDetail) = await AssistantAsync(admin, seed, Full);
        using var dropped = await SetKeyAsync(admin, draftOnly, draftDetail, null);
        dropped.StatusCode.ShouldBe(HttpStatusCode.OK, await dropped.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_pickers_availability_refuses_a_key_outside_the_callers_departments_before_any_gateway_call()
    {
        // #618 review (admin §4.5): a gatewayKeyId that is not a key of a department in scope is 404, as /api/gateway/keys.
        var seed = await SeedAsync();
        var factory = await SharedAsync();
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, "X" + seed.Code);
        using var own = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);

        (await stranger.GetAsync($"/api/gateway/models?level=internal&gatewayKeyId={Full}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await own.GetAsync($"/api/gateway/models?level=internal&gatewayKeyId={Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await own.GetAsync($"/api/gateway/models?level=internal&gatewayKeyId={Full}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stranger.GetAsync("/api/gateway/models?level=internal", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_knowledge_base_without_a_key_saves_and_the_status_reports_the_acted_echo()
    {
        var seed = await SeedAsync();
        var factory = await SharedAsync();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        using var created = await admin.PostAsJsonAsync("/api/collections", new { name = $"Keyless {seed.Code}", departmentId = seed.Department, classification = "internal" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));

        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var status = await platform.GetFromJsonAsync<JsonElement>("/api/gateway/status", Ct);
        var acted = status.GetProperty("acted");
        (acted.GetProperty("gatewayKeyId").GetGuid(), acted.GetProperty("outcome").GetString()).ShouldBe((Full, "ok"));
    }
}
