extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.ServiceDefaults;
using Kalla.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Its own ragdb with one embedding profile (FakeGateway's <c>bge-m3</c>, 1024 dimensions, ceiling <c>Eu</c>): the sync
/// removes every catalogue row the fake does not serve, and the canary is recorded once per database.
/// </summary>
public sealed class GatewayClientFixture : RagDatabase, IAsyncLifetime
{
    /// <summary>rag-admin's credential key (identity design §4.7): one key at every ceiling up to its maximum.</summary>
    public const string AdminKey = "fake-admin-credential-key-gw-0123456789";

    public Guid ProfileId { get; } = Guid.CreateVersion7();

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        await InitializeAsync();
        await using var db = TestPostgres.Context(Connection(Admin));
        db.EmbeddingProfiles.Add(new EmbeddingProfile
        {
            Id = ProfileId, Name = "pilot-embedding", GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The FakeGateway with rag-admin's credential key.</summary>
    public WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
    });

    /// <summary>
    /// The admin API with its credential key (none when <paramref name="key"/> is false), whose residencies reach up to
    /// <paramref name="maxResidency"/> (all by default; M-17 C9), syncing from <paramref name="gateway"/>.
    /// </summary>
    public WebApplicationFactory<Program> AdminApi(WebApplicationFactory<FakeGatewayProgram> gateway, Residency? maxResidency = null, bool key = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:ragdb", Connection(Admin));
            web.UseSetting("Oidc:Authority", "https://idp.invalid/realms/ume");
            web.UseSetting("OrgSync:IntervalMinutes", "1440");
            web.UseSetting("Admin:Retention", "false");
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            if (key)
            {
                web.UseSetting("Gateway:Key", AdminKey);
            }

            web.ConfigureTestServices(services =>
            {
                services.AddHeaderAuthentication();
                services.UseGatewayCredentialCeiling(maxResidency ?? Residency.External);
                services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            });
        });
}

/// <summary>
/// The gateway client, release-1 side (P1a item 13; gateway-changes §13.4, admin §4.5, §4.6 V8, §4.10, identity §4.7):
/// the model mirror, the profile canary and its on-demand check (#353), V8's <c>/v1/models</c> check and the per-level status,
/// against the FakeGateway.
/// </summary>
public sealed class GatewayClientTests(GatewayClientFixture database) : IClassFixture<GatewayClientFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> ConfigVersionAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        return await db.ConfigVersion.Select(v => v.Version).SingleAsync(Ct);
    }

    /// <summary>The host's first run, then one more: afterwards nothing is left to change.</summary>
    private static async Task<GatewayModelSync> SyncedAsync(WebApplicationFactory<Program> admin)
    {
        var sync = admin.Services.GetRequiredService<GatewayModelSync>();
        await admin.Services.GetRequiredService<OrganisationSync>().RunAsync(null, Ct);
        (await sync.RunAsync(null, Ct)).ShouldNotBeNull(sync.Status.LastError);
        return sync;
    }

    [Fact]
    public async Task A_platform_admin_sets_a_models_limits_the_sync_keeps_them_and_a_bad_pair_is_refused()
    {
        // Decisions "Answer length from the model" (admin design §4.5, §4.10): ContextWindow and MaxOutputTokens are manual
        // while the gateway sends none for the model (ume/chat-onprem), and come from /v1/models once it does (ume/chat-eu).
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        await SyncedAsync(admin);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        const string Name = "ume/chat-onprem";
        var url = $"/api/gateway/models/{Name}";

        var fromGateway = (await platform.GetFromJsonAsync<GatewayModelDto[]>("/api/gateway/models", Ct))!.Single(m => m.Name == "ume/chat-eu");
        (fromGateway.ContextWindow, fromGateway.MaxOutputTokens, fromGateway.LimitsSource).ShouldBe((128_000, 16_384, GatewayModelSources.Catalog));
        using (var refused = await platform.PutAsJsonAsync("/api/gateway/models/ume/chat-eu", new { contextWindow = 1000 }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
            problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.PlatformRule.Code);
            problem.GetProperty("params").GetProperty("constraint").GetString().ShouldBe(GatewayEndpoints.LimitsFromGateway);
        }

        using (var set = await platform.PutAsJsonAsync(url, new { contextWindow = 128_000, maxOutputTokens = 16_384 }, Ct))
        {
            set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
            var row = (await set.Content.ReadFromJsonAsync<GatewayModelDto>(Ct))!;
            row.ContextWindow.ShouldBe(128_000);
            row.MaxOutputTokens.ShouldBe(16_384);
            row.LimitsSource.ShouldBe(GatewayModelSources.Manual);
        }

        // A new instance reads the whole list (no ETag) and keeps the manual limits the gateway does not send.
        await using (var other = database.AdminApi(gateway, Residency.Eu))
        {
            await SyncedAsync(other);
        }

        var listed = (await platform.GetFromJsonAsync<GatewayModelDto[]>("/api/gateway/models", Ct))!.Single(m => m.Name == Name);
        (listed.ContextWindow, listed.MaxOutputTokens).ShouldBe((128_000, 16_384)); // kept by the sync

        foreach (var body in new object[] { new { contextWindow = 4096, maxOutputTokens = 8192 }, new { contextWindow = 0 }, new { maxOutputTokens = 10_000_001 } })
        {
            using var refused = await platform.PutAsJsonAsync(url, body, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(body));
        }

        using (var unknown = await platform.PutAsJsonAsync("/api/gateway/models/no-such-model", new { contextWindow = 1000 }, Ct))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var reader = await AdminDatabaseFixture.UserAsync(admin, RagRoles.Reviewer, "1000"))
        using (var forbidden = await reader.PutAsJsonAsync(url, new { contextWindow = 1000 }, Ct))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The name may also come escaped.
        using (var cleared = await platform.PutAsJsonAsync($"/api/gateway/models/{Uri.EscapeDataString(Name)}", new { contextWindow = (int?)null, maxOutputTokens = (int?)null }, Ct))
        {
            var row = (await cleared.Content.ReadFromJsonAsync<GatewayModelDto>(Ct))!;
            (row.ContextWindow, row.MaxOutputTokens).ShouldBe((null, null));
        }

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var audit = await db.AuditLog.Where(a => a.EntityType == "gateway-model" && a.EntityId == Name).ToListAsync(Ct);
        audit.Count.ShouldBe(2);
        audit.ShouldAllBe(a => a.Action == "update");

        // The table's own rule, whatever writes it.
        var violation = await Should.ThrowAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"""UPDATE config."GatewayModels" SET "ContextWindow" = 1000, "MaxOutputTokens" = 2000 WHERE "Name" = {Name}""", Ct));
        violation.ConstraintName.ShouldBe("CK_GatewayModels_Limits");
    }

    [Fact]
    public async Task A_gateway_rerank_model_is_mirrored_as_kind_rerank_and_goes_when_the_gateway_drops_it()
    {
        // B-16: Settings.Retrieval.gatewayRerankModel names a rerank row (V32); the gateway's catalogue Kind "Rerank".
        await using (var gateway = database.Gateway().WithWebHostBuilder(web => web.UseSetting("FakeGateway:RerankEnabled", "true")))
        await using (var admin = database.AdminApi(gateway, Residency.Eu))
        {
            await SyncedAsync(admin);
            using var reader = await AdminDatabaseFixture.UserAsync(admin, RagRoles.Reviewer, "1000");
            var models = (await reader.GetFromJsonAsync<GatewayModelDto[]>("/api/gateway/models", Ct))!.ToDictionary(m => m.Name);
            models["bge-reranker-v2-m3"].ModelKind.ShouldBe(GatewayModelModelKinds.Rerank);
            models["bge-reranker-v2-m3"].Residencies.ShouldBe(["Eu"]);
            models["bge-reranker-v2-m3"].InputPerMillionUsd.ShouldBe(0.10m);
        }

        await using (var gateway = database.Gateway())
        await using (var admin = database.AdminApi(gateway, Residency.Eu))
        {
            await SyncedAsync(admin);
            await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
            (await db.GatewayModels.AnyAsync(m => m.Name == "bge-reranker-v2-m3", Ct)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_mirror_holds_each_model_and_alias_within_the_ceiling_with_its_facts_and_an_unchanged_etag_writes_nothing()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        var sync = await SyncedAsync(admin);

        using var reader = await AdminDatabaseFixture.UserAsync(admin, RagRoles.Reviewer, "1000");
        var models = (await reader.GetFromJsonAsync<GatewayModelDto[]>("/api/gateway/models", Ct))!.ToDictionary(m => m.Name);
        models["ume/chat-eu"].Residencies.ShouldBe(["Eu"]);
        models["ume/chat-eu"].Kind.ShouldBe(GatewayModelKinds.Model);
        models["ume/chat-eu"].ModelKind.ShouldBe(GatewayModelModelKinds.Chat); // ume.kind "Chat" (#363 point 1)
        models["ume/chat-eu"].Source.ShouldBe(GatewayModelSources.Catalog);
        models["ume/chat-eu"].OutputPerMillionUsd.ShouldBe(0.60m);
        models["ume/chat-eu"].SupportsTools.ShouldBeTrue(); // ume.features "tools"
        models["bge-m3"].Residencies.ShouldBe(["OnPrem"]);
        models["bge-m3"].InputPerMillionUsd.ShouldBe(0.02m);
        models["bge-m3"].Kind.ShouldBe(GatewayModelKinds.Model);
        models["bge-m3"].ModelKind.ShouldBe(GatewayModelModelKinds.Embedding); // ume.kind "Embedding"
        models["bge-m3"].SupportsTools.ShouldBeFalse();
        // An alias is a route with its targets' residencies, from the catalogue.
        models["ume/chat-standard"].Kind.ShouldBe(GatewayModelKinds.Route);
        models["ume/chat-standard"].Source.ShouldBe(GatewayModelSources.Catalog);
        models["ume/chat-standard"].Residencies.ShouldBe(["OnPrem", "Eu"]);
        // The list is the gateway's within the admin's ceiling (Eu here), and this gateway has no rerank model (B-16).
        models.ShouldNotContainKey("ume/chat-advanced");
        models.ShouldNotContainKey("bge-reranker-v2-m3");

        var version = await ConfigVersionAsync();
        (await sync.RunAsync(null, Ct)).ShouldBe(new GatewayModelSyncResult(0, 0, 0));
        (await ConfigVersionAsync()).ShouldBe(version);
    }

    [Fact]
    public async Task The_facts_set_kind_residencies_tools_and_limits_and_an_unknown_kind_is_not_mirrored()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string alias = $"route-{suffix}", limited = $"limited-{suffix}", unknown = $"unknown-{suffix}", moderation = $"moderation-{suffix}";
        db.GatewayModels.Add(new GatewayModel { Name = unknown, Residencies = ["Eu"], Source = GatewayModelSources.Catalog, ContextWindow = 4096, MaxOutputTokens = 1024 });
        await db.SaveChangesAsync(Ct);
        static GatewayListedModel Model(string id, string type, string kind, string? residency, string[]? residencies, int? window, int? output, string[] features) =>
            new(id, new GatewayModelFacts(type, kind, residency, residencies, window, output, features, new GatewayModelPricing(1m, 0.5m, 2m)));
        GatewayListedModel[] models =
        [
            Model(alias, "alias", "Chat", null, ["Eu", "OnPrem"], 32_000, null, ["tools"]),
            Model(limited, "model", "Chat", "External", null, 200_000, 32_000, []),
            Model(unknown, "model", "Chat", "Eu", null, null, null, []),
            Model(moderation, "model", "Moderation", "Eu", null, null, null, []),
        ];

        // Applied but not saved: every other catalogue row would be removed.
        await GatewayModelSync.ApplyAsync(db, models, DateTimeOffset.UtcNow, Ct);

        var rows = db.ChangeTracker.Entries<GatewayModel>().Select(e => e.Entity).ToDictionary(m => m.Name);
        (rows[alias].Kind, rows[alias].SupportsTools).ShouldBe((GatewayModelKinds.Route, true));
        rows[alias].Residencies.ShouldBe(["OnPrem", "Eu"]);
        // One limit sent: both come from the gateway, the missing one null.
        (rows[alias].ContextWindow, rows[alias].MaxOutputTokens, rows[alias].LimitsSource).ShouldBe((32_000, (int?)null, GatewayModelSources.Catalog));
        (rows[limited].ContextWindow, rows[limited].MaxOutputTokens, rows[limited].LimitsSource).ShouldBe((200_000, 32_000, GatewayModelSources.Catalog));
        (rows[limited].InputPerMillionUsd, rows[limited].CachedInputPerMillionUsd, rows[limited].OutputPerMillionUsd).ShouldBe((1m, 0.5m, 2m));
        // No limit sent: the manual ones stay.
        (rows[unknown].ContextWindow, rows[unknown].MaxOutputTokens, rows[unknown].LimitsSource).ShouldBe((4096, 1024, GatewayModelSources.Manual));
        rows.ShouldNotContainKey(moderation);
        db.ChangeTracker.Clear();
        await db.GatewayModels.Where(m => m.Name == unknown).ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task Manual_rows_are_kept_and_a_catalogue_row_the_gateway_no_longer_serves_is_removed_with_one_audit_entry()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        await SyncedAsync(admin);
        var gone = "gone-" + Guid.NewGuid().ToString("N");
        var alias = "alias-" + Guid.NewGuid().ToString("N");
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            db.GatewayModels.Add(new GatewayModel { Name = gone, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
            db.GatewayModels.Add(new GatewayModel { Name = alias, Kind = GatewayModelKinds.Route, Residencies = ["OnPrem", "Eu"], Source = GatewayModelSources.Manual });
            var changed = await db.GatewayModels.SingleAsync(m => m.Name == "ume/chat-eu", Ct);
            changed.OutputPerMillionUsd = 9m;
            // A row whose ModelKind is wrong (the migration's default 'chat') is corrected by the next run: no compatibility step.
            var embedding = await db.GatewayModels.SingleAsync(m => m.Name == "bge-m3", Ct);
            embedding.ModelKind = GatewayModelModelKinds.Chat;
            await db.SaveChangesAsync(Ct);
        }

        // An unchanged list (the ETag) writes nothing, even over a hand edit; a new instance reads it in full.
        using (var same = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin))
        using (var unchanged = await same.PostAsync("/api/gateway/sync", null, Ct))
        {
            (await unchanged.Content.ReadFromJsonAsync<GatewayModelSyncResult>(Ct)).ShouldBe(new GatewayModelSyncResult(0, 0, 0));
        }

        await using var fresh = database.AdminApi(gateway, Residency.Eu);
        var sync = fresh.Services.GetRequiredService<GatewayModelSync>();
        for (var i = 0; i < 100 && sync.Status.LastResult is null; i++)
        {
            await Task.Delay(100, Ct); // the host's first run
        }

        (sync.Status.LastResult! with { CanariesRecorded = 0 }).ShouldBe(new GatewayModelSyncResult(2, 1, 0));
        var version = await ConfigVersionAsync();
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            (await db.GatewayModels.AnyAsync(m => m.Name == gone, Ct)).ShouldBeFalse();
            var kept = await db.GatewayModels.SingleAsync(m => m.Name == alias, Ct);
            kept.Residencies.ShouldBe(["OnPrem", "Eu"]);
            kept.ModelKind.ShouldBe(GatewayModelModelKinds.Chat); // manual route rows are chat
            (await db.GatewayModels.SingleAsync(m => m.Name == "ume/chat-eu", Ct)).OutputPerMillionUsd.ShouldBe(0.60m);
            (await db.GatewayModels.SingleAsync(m => m.Name == "bge-m3", Ct)).ModelKind.ShouldBe(GatewayModelModelKinds.Embedding);
            var audit = await db.AuditLog.Where(a => a.EntityType == "gateway-models" && a.ConfigVersion == version).ToListAsync(Ct);
            audit.ShouldHaveSingleItem().Actor.ShouldBe(GatewayModelSync.SystemActor);
            db.GatewayModels.Remove(await db.GatewayModels.SingleAsync(m => m.Name == alias, Ct));
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task The_profile_canary_is_recorded_once_from_the_gateway_at_the_profile_ceiling_and_never_replaced()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        await SyncedAsync(admin);

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var stored = await db.Database.SqlQuery<string>($"""
            SELECT "CanaryEmbedding"::text AS "Value" FROM config."EmbeddingProfiles" WHERE "Id" = {database.ProfileId}
            """).SingleAsync(Ct);
        (await db.Database.SqlQuery<int>($"""
            SELECT vector_dims("CanaryEmbedding") AS "Value" FROM config."EmbeddingProfiles" WHERE "Id" = {database.ProfileId}
            """).SingleAsync(Ct)).ShouldBe(1024);
        (await db.AuditLog.CountAsync(a => a.Action == "record-canary" && a.EntityId == database.ProfileId.ToString(), Ct)).ShouldBe(1);

        // Recorded is final: another run (another instance, a restart) leaves the vector as it is.
        (await admin.Services.GetRequiredService<GatewayModelSync>().RecordCanariesAsync(Ct)).ShouldBe(0);
        (await db.Database.SqlQuery<string>($"""
            SELECT "CanaryEmbedding"::text AS "Value" FROM config."EmbeddingProfiles" WHERE "Id" = {database.ProfileId}
            """).SingleAsync(Ct)).ShouldBe(stored);
    }

    [Fact]
    public async Task V8_lists_the_model_at_the_ceiling_or_refuses_with_its_code()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        await SyncedAsync(admin);
        var drained = "fake-drained";
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            if (!await db.GatewayModels.AnyAsync(m => m.Name == drained, Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = drained, Kind = GatewayModelKinds.Route, Residencies = ["OnPrem", "External"], Source = GatewayModelSources.Manual });
                await db.SaveChangesAsync(Ct);
            }
        }

        await using var scope = admin.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<GatewayModelCheck>();
        await check.EnsureServableAsync("ume/chat-eu", Residency.Eu, Ct);
        await check.EnsureServableAsync("bge-m3", Residency.Eu, Ct); // an OnPrem model is within an Eu ceiling

        (await Code(() => check.EnsureServableAsync("ume/chat-advanced", Residency.Eu, Ct))).ShouldBe(AdminErrorCodes.ModelResidency.Code);
        (await Code(() => check.EnsureServableAsync("no-such-model", Residency.Eu, Ct))).ShouldBe(AdminErrorCodes.ModelResidency.Code);
        // The mirror allows it at OnPrem, but its OnPrem target is drained: the gateway does not list it (gateway-changes §3.4).
        (await Code(() => check.EnsureServableAsync(drained, Residency.OnPrem, Ct))).ShouldBe(AdminErrorCodes.ModelNotListed.Code);
        // An instance without a credential key cannot ask the gateway, and the code says the key is missing (#237).
        await using var keyless = database.AdminApi(gateway, key: false);
        await using var keylessScope = keyless.Services.CreateAsyncScope();
        (await Code(() => keylessScope.ServiceProvider.GetRequiredService<GatewayModelCheck>().EnsureServableAsync("ume/chat-eu", Residency.Eu, Ct)))
            .ShouldBe(AdminErrorCodes.GatewayKeyMissing.Code);
    }

    [Fact]
    public async Task Under_a_max_residency_V8_checks_a_public_ceiling_at_the_cap()
    {
        await using var gateway = database.Gateway();
        await using var eu = database.AdminApi(gateway, Residency.Eu);
        await using var admin = eu.WithWebHostBuilder(web => web.ConfigureTestServices(services => services.UseGatewayCredentialCeiling(Residency.Eu)));
        await SyncedAsync(admin);
        await using var scope = admin.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<GatewayModelCheck>();

        // A public assistant asks for External; M2's cap checks it at Eu, with the Eu key (#237).
        await check.EnsureServableAsync("ume/chat-eu", Residency.External, Ct);
        var refused = await Should.ThrowAsync<ApiFaultException>(() => check.EnsureServableAsync("ume/chat-advanced", Residency.External, Ct));
        refused.Extensions!["code"].ShouldBe(AdminErrorCodes.ModelResidency.Code);
        ((IReadOnlyDictionary<string, string>)refused.Extensions["params"]!)["ceiling"].ShouldBe("Eu"); // the capped ceiling
        // The cap only lowers: an OnPrem ceiling stays OnPrem, checked with the same credential key.
        await check.EnsureServableAsync("bge-m3", Residency.OnPrem, Ct);
    }

    [Fact]
    public async Task The_status_embeds_once_per_held_key_at_its_ceiling_and_shows_the_canary()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        await SyncedAsync(admin);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        var status = await platform.GetFromJsonAsync<JsonElement>("/api/gateway/status", Ct);

        status.GetProperty("configured").GetBoolean().ShouldBeTrue();
        status.GetProperty("model").GetString().ShouldBe("bge-m3");
        var levels = status.GetProperty("levels").EnumerateArray().ToDictionary(l => l.GetProperty("level").GetString()!, l => l);
        levels["Eu"].GetProperty("outcome").GetString().ShouldBe("ok");
        levels["Eu"].GetProperty("residency").GetString().ShouldBe("OnPrem");
        levels["OnPrem"].GetProperty("outcome").GetString().ShouldBe("ok");
        levels["External"].GetProperty("outcome").GetString().ShouldBe("not_configured");
        var profile = status.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == database.ProfileId);
        profile.GetProperty("canaryRecorded").GetBoolean().ShouldBeTrue();
        profile.GetProperty("maxResidency").GetString().ShouldBe("Eu");
        status.GetProperty("sync").GetProperty("consecutiveFailures").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_canary_check_stores_each_profiles_similarity_audits_it_and_the_status_shows_it()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        await SyncedAsync(admin);
        var drifted = Guid.CreateVersion7();
        var other = "[" + string.Join(',', fakegateway::Kalla.FakeGateway.FakeContent.Vector("En helt annan text om något annat.", 1024)
            .Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        db.EmbeddingProfiles.Add(new EmbeddingProfile
        {
            Id = drifted, Name = "drifted-" + drifted.ToString("N"), GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192,
            MaxResidency = Residency.Eu, CanaryText = EmbeddingCanaryText.Default, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test",
        });
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlAsync($"""UPDATE config."EmbeddingProfiles" SET "CanaryEmbedding" = {other}::vector WHERE "Id" = {drifted}""", Ct);
        try
        {
            using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
            using var response = await platform.PostAsync("/api/gateway/canary-check", null, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var results = (await response.Content.ReadFromJsonAsync<EmbeddingCanaryCheckResult[]>(Ct))!.ToDictionary(r => r.ProfileId);
            var pilot = results[database.ProfileId];
            pilot.Passed.ShouldBeTrue();
            pilot.Similarity!.Value.ShouldBeGreaterThanOrEqualTo(EmbeddingCanaryThreshold.MinCosine);
            pilot.CheckedAt.ShouldNotBeNull();
            pilot.Error.ShouldBeNull();
            results[drifted].Passed.ShouldBeFalse();
            results[drifted].Similarity!.Value.ShouldBeLessThan(EmbeddingCanaryThreshold.MinCosine);

            var stored = await db.EmbeddingProfiles.AsNoTracking().Where(p => p.Id == database.ProfileId || p.Id == drifted).ToDictionaryAsync(p => p.Id, Ct);
            stored[database.ProfileId].CanaryCheckedAt.ShouldNotBeNull();
            ((double)stored[database.ProfileId].CanarySimilarity!.Value).ShouldBe(pilot.Similarity!.Value);
            ((double)stored[drifted].CanarySimilarity!.Value).ShouldBe(results[drifted].Similarity!.Value);
            var audit = await db.AuditLog.Where(a => a.Action == "check-canary" && a.EntityId == drifted.ToString()).ToListAsync(Ct);
            audit.ShouldHaveSingleItem().Actor.ShouldBe("test-user");
            audit[0].EntityType.ShouldBe("embedding-profile");

            var status = await platform.GetFromJsonAsync<JsonElement>("/api/gateway/status", Ct);
            var profiles = status.GetProperty("profiles").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
            profiles[drifted].GetProperty("canarySimilarity").GetDouble().ShouldBe(results[drifted].Similarity!.Value, 1e-6);
            profiles[drifted].GetProperty("canaryCheckedAt").GetDateTimeOffset().ShouldBe(results[drifted].CheckedAt!.Value, TimeSpan.FromMilliseconds(1));
            profiles[database.ProfileId].GetProperty("canarySimilarity").GetDouble().ShouldBeGreaterThanOrEqualTo(EmbeddingCanaryThreshold.MinCosine);
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"""DELETE FROM config."EmbeddingProfiles" WHERE "Id" = {drifted}""", Ct);
        }
    }

    [Fact]
    public async Task The_last_canary_check_is_read_from_the_database_without_a_gateway_request()
    {
        await using var gateway = database.Gateway();
        await using (var recording = database.AdminApi(gateway, Residency.Eu))
        {
            await SyncedAsync(recording);
        }

        var counter = new CountingHandler { InnerHandler = gateway.Server.CreateHandler() };
        await using var admin = database.AdminApi(gateway, Residency.Eu).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            services.UseGatewayCredentialCeiling().AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => counter)));
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        var checkedAt = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        await db.Database.ExecuteSqlAsync($"""
            UPDATE config."EmbeddingProfiles" SET "CanaryCheckedAt" = {checkedAt}, "CanarySimilarity" = {0.5f} WHERE "Id" = {database.ProfileId}
            """, Ct);
        var before = counter.Count;

        var last = await platform.GetFromJsonAsync<EmbeddingCanaryLastResult[]>("/api/gateway/canary-check", Ct);

        counter.Count.ShouldBe(before);
        var pilot = last!.Single(r => r.ProfileId == database.ProfileId);
        pilot.Recorded.ShouldBeTrue();
        pilot.Similarity!.Value.ShouldBe(0.5, 1e-6);
        pilot.Passed.ShouldBe(false);
        pilot.CheckedAt.ShouldBe(checkedAt);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE config."EmbeddingProfiles" SET "CanaryCheckedAt" = NULL, "CanarySimilarity" = NULL WHERE "Id" = {database.ProfileId}
            """, Ct);
        var never = (await platform.GetFromJsonAsync<EmbeddingCanaryLastResult[]>("/api/gateway/canary-check", Ct))!.Single(r => r.ProfileId == database.ProfileId);
        never.Passed.ShouldBeNull();
        never.Similarity.ShouldBeNull();
        never.CheckedAt.ShouldBeNull();
        counter.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_canary_check_the_gateway_cannot_answer_reports_the_error_and_stores_nothing()
    {
        await using var gateway = database.Gateway();
        await using (var recording = database.AdminApi(gateway, Residency.Eu))
        {
            await SyncedAsync(recording);
        }

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var before = await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == database.ProfileId, Ct);
        var audits = await db.AuditLog.CountAsync(a => a.Action == "check-canary", Ct);
        // This instance's maximum is OnPrem (its credential key's residencies), below the profile's Eu ceiling.
        await using var admin = database.AdminApi(gateway, Residency.OnPrem);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        using var response = await platform.PostAsync("/api/gateway/canary-check", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var result = (await response.Content.ReadFromJsonAsync<EmbeddingCanaryCheckResult[]>(Ct))!.Single(r => r.ProfileId == database.ProfileId);
        result.Error.ShouldBe("gateway_key_missing");
        result.Passed.ShouldBeFalse();
        result.Similarity.ShouldBeNull();
        result.CheckedAt.ShouldBeNull();
        var after = await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == database.ProfileId, Ct);
        after.CanaryCheckedAt.ShouldBe(before.CanaryCheckedAt);
        after.CanarySimilarity.ShouldBe(before.CanarySimilarity);
        (await db.AuditLog.CountAsync(a => a.Action == "check-canary", Ct)).ShouldBe(audits);
    }

    [Fact]
    public async Task Without_a_gateway_the_canary_check_says_so_per_profile()
    {
        await using var gateway = database.Gateway();
        await using (var recording = database.AdminApi(gateway, Residency.Eu))
        {
            await SyncedAsync(recording);
        }

        await using var admin = database.AdminApi(gateway, key: false).WithWebHostBuilder(web => web.UseSetting("Gateway:BaseUrl", ""));
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        using var response = await platform.PostAsync("/api/gateway/canary-check", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<EmbeddingCanaryCheckResult[]>(Ct))!.Single(r => r.ProfileId == database.ProfileId).Error.ShouldBe("gateway_not_configured");
    }

    [Fact]
    public async Task Dimensions_are_sent_only_when_asked()
    {
        // #142: the canary and the status probe pass the profile's Dimensions when it sets RequestDimensions.
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        var api = admin.Services.GetRequiredService<GatewayApi>();

        (await api.EmbedAsync("bge-m3", ["bygglov"], Residency.Eu, Ct, dimensions: 256)).Vectors[0].Length.ShouldBe(256);
        (await api.EmbedAsync("bge-m3", ["bygglov"], Residency.Eu, Ct)).Vectors[0].Length.ShouldBe(1024);
    }

    [Fact]
    public async Task One_level_failing_oddly_is_that_levels_error_and_the_page_still_answers()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            services.UseGatewayCredentialCeiling().AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
                new MalformedAtOnPrem { InnerHandler = gateway.Server.CreateHandler() })));
        await SyncedAsync(admin);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        var status = await platform.GetFromJsonAsync<JsonElement>("/api/gateway/status", Ct);

        var levels = status.GetProperty("levels").EnumerateArray().ToDictionary(l => l.GetProperty("level").GetString()!, l => l);
        levels["Eu"].GetProperty("outcome").GetString().ShouldBe("ok");
        levels["OnPrem"].GetProperty("outcome").GetString().ShouldBe("error");
        levels["OnPrem"].GetProperty("code").GetString().ShouldBe(nameof(JsonException));
    }

    /// <summary>Counts the requests that reach the gateway.</summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Answers embeddings at ceiling OnPrem with a body that is not JSON; everything else goes to the FakeGateway.</summary>
    private sealed class MalformedAtOnPrem : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal) &&
            request.Headers.TryGetValues("x-ume-max-residency", out var ceiling) && ceiling.Single() == "OnPrem"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json"),
                    Headers = { { "x-ume-residency", "OnPrem" } },
                }
                : await base.SendAsync(request, cancellationToken);
    }

    private static async Task<string?> Code(Func<Task> action)
    {
        var fault = await Should.ThrowAsync<ApiFaultException>(action);
        return fault.Extensions!["code"] as string;
    }
}
