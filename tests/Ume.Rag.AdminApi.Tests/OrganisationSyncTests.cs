extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
using FakeCatalog = fakegateway::Ume.Rag.FakeGateway.FakeCatalog;
using FakeGatewayOptions = fakegateway::Ume.Rag.FakeGateway.FakeGatewayOptions;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>Its own ragdb: the sync deactivates every department the context does not list.</summary>
public sealed class OrgSyncFixture : RagDatabase, IAsyncLifetime
{
    /// <summary>rag-admin's credential key, up to <c>Eu</c>: "Webb – external only" shares no residency with it.</summary>
    public const string AdminKey = "fake-admin-credential-key-org-0123456789";

    /// <summary>
    /// The FakeGateway with rag-admin's credential key, holding <c>organisation.read</c> unless <paramref name="organisationRead"/>
    /// is false; <paramref name="features"/> replaces the gateway's features.
    /// </summary>
    public WebApplicationFactory<FakeGatewayProgram> Gateway(bool organisationRead = true, string[]? features = null) =>
        new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
            web.UseSetting("FakeGateway:CredentialKeys:admin:MaxResidency", "Eu");
            if (organisationRead)
            {
                web.UseSetting("FakeGateway:CredentialKeys:admin:Scopes:0", "organisation.read");
            }

            if (features is not null)
            {
                web.ConfigureTestServices(services => services.PostConfigure<FakeGatewayOptions>(o => o.Gateway.Features = [.. features]));
            }
        });

    /// <summary>The admin API (header sign-in) syncing from <paramref name="gateway"/> with <paramref name="key"/>.</summary>
    public WebApplicationFactory<Program> AdminApi(WebApplicationFactory<FakeGatewayProgram> gateway, string key = AdminKey) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:ragdb", Connection(Admin));
            web.UseSetting("Oidc:Authority", "https://idp.invalid/realms/ume");
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", key);
            web.UseSetting("OrgSync:IntervalMinutes", "1440");
            web.UseSetting("Admin:Retention", "false");
            web.ConfigureTestServices(services =>
            {
                services.AddHeaderAuthentication();
                services.UseGatewayCredentialCeiling();
                services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            });
        });
}

/// <summary>
/// The organisation sync from the FakeGateway's <c>/v1/ume/context</c> (admin design §4.10, identity §2.1, data-model §2.1
/// <c>GatewayKeys</c>): departments, teams, member groups and the assigned keys' mirror.
/// </summary>
public sealed class OrganisationSyncTests(OrgSyncFixture database) : IClassFixture<OrgSyncFixture>
{
    private static readonly Guid ChatbotKey = new("019a0000-0000-7000-8000-00000000b001");
    private static readonly Guid DisabledKey = new("019a0000-0000-7000-8000-00000000b003");
    private static readonly Guid ExternalOnlyKey = new("019a0000-0000-7000-8000-00000000b004");
    private static readonly Guid RevokedKey = new("019a0000-0000-7000-8000-00000000b005");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> ConfigVersionAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        return await db.ConfigVersion.Select(v => v.Version).SingleAsync(Ct);
    }

    /// <summary>The sync's first run starts with the host; wait for it so each test sees a synced mirror.</summary>
    private static async Task<OrganisationSync> StartedAsync(WebApplicationFactory<Program> admin)
    {
        var sync = admin.Services.GetRequiredService<OrganisationSync>();
        for (var i = 0; i < 100 && sync.Status.LastRunAt is null; i++)
        {
            await Task.Delay(100, Ct);
        }

        await sync.RunAsync(null, Ct); // after the first run: nothing left to change
        return sync;
    }

    [Fact]
    public async Task The_context_fills_departments_teams_member_groups_and_keys_and_an_unchanged_etag_writes_nothing()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway);
        var sync = await StartedAsync(admin);

        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        var departments = await platform.GetFromJsonAsync<JsonElement[]>("/api/organisation/departments", Ct);
        departments!.Where(d => d.GetProperty("isActive").GetBoolean()).Select(d => d.GetProperty("costCenterCode").GetString())
            .ShouldBe(FakeCatalog.Departments.Select(d => d.CostCenterCode), ignoreOrder: true);
        var kommunledning = departments!.Single(d => d.GetProperty("costCenterCode").GetString() == "1000");
        kommunledning.GetProperty("teams").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .ShouldBe(["Digitalisering & AI", "Kontaktcenter"], ignoreOrder: true);

        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            // An inactive department and its team are mirrored inactive; member groups come from the context.
            var wound = await db.Departments.Include(d => d.Teams).SingleAsync(d => d.Id == FakeCatalog.OverformyndarId, Ct);
            wound.IsActive.ShouldBeFalse();
            wound.Teams.ShouldHaveSingleItem().IsActive.ShouldBeFalse();
            (await db.Departments.SingleAsync(d => d.CostCenterCode == "3000", Ct)).MemberGroups.ShouldBe(["Byggnadsnämnden"]);

            // The assigned keys: a revoked one is not mirrored, a disabled one keeps its status, usable: false is stored.
            var keys = await db.GatewayKeys.AsNoTracking().ToDictionaryAsync(k => k.Id, Ct);
            keys.Keys.ShouldNotContain(RevokedKey);
            keys.Count.ShouldBe(4);
            keys[DisabledKey].Status.ShouldBe("Disabled");
            keys[ExternalOnlyKey].Usable.ShouldBeFalse();
            keys[ExternalOnlyKey].AllowedResidencies.ShouldBe(["External"]);
            var chatbot = keys[ChatbotKey];
            (chatbot.Usable, chatbot.Status, chatbot.OwnerActive, chatbot.DepartmentId).ShouldBe((true, "Active", true, FakeCatalog.KommunledningskontoretId));
            (chatbot.RequestsPerMinute, chatbot.MaxOutputTokens).ShouldBe((60, 4096));
            chatbot.AllowedModels.ShouldBe(["ume/chat-standard", "ume/chat-eu", "bge-m3"]);
        }

        sync.Status.Degraded.ShouldBeNull();
        sync.Status.Gateway!.Features.ShouldContain("member-groups");
        sync.Status.Gateway.Version.ShouldBe("0.10.0-fake");

        // The second run sends the ETag: 304, nothing written.
        var version = await ConfigVersionAsync();
        (await sync.RunAsync(null, Ct)).ShouldBe(new OrgSyncResult(0, 0, 0));
        (await ConfigVersionAsync()).ShouldBe(version);
        (await sync.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Only_changed_rows_are_written_with_one_audit_entry_missing_ones_deactivated_and_a_revoked_key_deleted()
    {
        await using var gateway = database.Gateway();
        await using (var first = database.AdminApi(gateway))
        {
            await StartedAsync(first);
        }

        var stray = "S" + Random.Shared.Next(100_000, 999_999);
        var strayKey = Guid.CreateVersion7();
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            var renamed = await db.Departments.SingleAsync(d => d.Id == FakeCatalog.FritidsforvaltningenId, Ct);
            renamed.Name = "Renamed by hand";
            var groups = await db.Departments.SingleAsync(d => d.Id == FakeCatalog.IndividOchFamiljeomsorgenId, Ct);
            groups.MemberGroups = ["Entered by hand"];
            db.Departments.Add(new Department { Id = Guid.CreateVersion7(), Name = "Not in the gateway", CostCenterCode = stray, SyncedAt = DateTimeOffset.UtcNow });
            (await db.GatewayKeys.SingleAsync(k => k.Id == ChatbotKey, Ct)).Usable = false;
            db.GatewayKeys.Add(new GatewayKey
            {
                Id = strayKey, Name = "Revoked since", TeamId = FakeCatalog.DigitaliseringTeamId, Status = "Active", PiiPolicy = "Off", ContentGuardPolicy = "Off",
                SyncedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        // A new instance holds no ETag yet: it reads the whole context and writes what differs. Its model sync may write
        // after it (another config version), so the run's audit entry is found by time, not by the latest version.
        var started = DateTimeOffset.UtcNow;
        await using var admin = database.AdminApi(gateway);
        var sync = admin.Services.GetRequiredService<OrganisationSync>();
        for (var i = 0; i < 100 && sync.Status.LastSuccessAt is null; i++)
        {
            await Task.Delay(100, Ct);
        }

        var result = sync.Status.LastResult;
        result.ShouldBe(new OrgSyncResult(3, 0, 2));
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            (await db.Departments.SingleAsync(d => d.Id == FakeCatalog.FritidsforvaltningenId, Ct)).Name.ShouldBe("Fritidsförvaltningen");
            (await db.Departments.SingleAsync(d => d.Id == FakeCatalog.IndividOchFamiljeomsorgenId, Ct)).MemberGroups.ShouldBe(["Byggnadsnämnden"]);
            (await db.Departments.SingleAsync(d => d.CostCenterCode == stray, Ct)).IsActive.ShouldBeFalse();
            (await db.GatewayKeys.SingleAsync(k => k.Id == ChatbotKey, Ct)).Usable.ShouldBeTrue();
            (await db.GatewayKeys.AnyAsync(k => k.Id == strayKey, Ct)).ShouldBeFalse();
            (await db.AuditLog.Where(a => a.EntityType == "organisation" && a.Timestamp >= started).ToListAsync(Ct))
                .ShouldHaveSingleItem().Actor.ShouldBe(OrganisationSync.SystemActor);
        }

        // Sync now by an admin: an unchanged context is a 304 with nothing to write.
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        using var response = await platform.PostAsync("/api/organisation/sync", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<OrgSyncResult>(Ct)).ShouldBe(new OrgSyncResult(0, 0, 0));
        sync.Status.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task Without_organisation_read_departments_and_teams_stay_and_the_sync_is_degraded()
    {
        await using (var full = database.Gateway())
        await using (var first = database.AdminApi(full))
        {
            await StartedAsync(first);
        }

        await using var gateway = database.Gateway(organisationRead: false);
        await using var admin = database.AdminApi(gateway);
        var sync = await StartedAsync(admin);

        // The context lists only the keys' departments: the others would be deactivated if taken.
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Departments.CountAsync(d => d.IsActive, Ct)).ShouldBe(FakeCatalog.Departments.Count);
        (await db.GatewayKeys.CountAsync(Ct)).ShouldBe(4); // the keys are still mirrored
        sync.Status.Degraded.ShouldNotBeNull().ShouldContain("organisation.read");
        var health = await sync.CheckHealthAsync(new HealthCheckContext(), Ct);
        health.Status.ShouldBe(HealthStatus.Degraded);
        health.Description.ShouldBe(sync.Status.Degraded);
    }

    [Fact]
    public async Task Without_the_organisation_feature_departments_stay_and_the_sync_is_degraded()
    {
        await using var gateway = database.Gateway(features: ["max-residency", "model-facts", "integrations"]);
        await using var admin = database.AdminApi(gateway);
        var sync = await StartedAsync(admin);

        sync.Status.Degraded.ShouldNotBeNull().ShouldContain("feature organisation");
        sync.Status.LastResult!.Departments.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_run_keeps_the_mirror_and_is_reported()
    {
        await using var gateway = database.Gateway();
        await using (var good = database.AdminApi(gateway))
        {
            await StartedAsync(good);
        }

        await using var admin = database.AdminApi(gateway, key: "wrong-key");
        var sync = admin.Services.GetRequiredService<OrganisationSync>();
        var version = await ConfigVersionAsync();
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        using var response = await platform.PostAsync("/api/organisation/sync", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.SyncFailed.Code);
        problem.GetProperty("params").GetProperty("error").GetString().ShouldBe("GatewayRefusedException (HTTP 401 invalid_api_key)");
        sync.Status.ConsecutiveFailures.ShouldBeGreaterThanOrEqualTo(1);
        (await ConfigVersionAsync()).ShouldBe(version);
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Departments.CountAsync(d => d.IsActive, Ct)).ShouldBe(FakeCatalog.Departments.Count);
        (await db.GatewayKeys.CountAsync(Ct)).ShouldBe(4);
    }

    [Fact]
    public async Task Without_the_member_groups_feature_the_stored_member_groups_stay()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var id = Guid.CreateVersion7();
        db.Departments.Add(new Department { Id = id, Name = "Hand", CostCenterCode = "H" + Random.Shared.Next(100_000, 999_999), MemberGroups = ["Nämnden"] });
        await db.SaveChangesAsync(Ct);
        var stored = await db.Departments.SingleAsync(d => d.Id == id, Ct);
        var context = new GatewayContext(
            new GatewayContextInfo("0.9.9", null, [GatewayContext.OrganisationFeature]),
            new GatewayContextKey(Guid.NewGuid(), "admin", "Credential", [GatewayContext.OrganisationRead], []),
            [new GatewayContextDepartment(id, "Hand", stored.CostCenterCode, true, ["Ignored without the feature"])], [], []);

        // Applied but not saved: the other departments and keys would change, this one is left as it is.
        await OrganisationSync.ApplyAsync(db, context, DateTimeOffset.UtcNow, Ct);

        db.Entry(stored).State.ShouldBe(EntityState.Unchanged);
        stored.MemberGroups.ShouldBe(["Nämnden"]);
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Member_groups_are_not_edited_by_hand_any_more()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        using var response = await platform.PutAsJsonAsync($"/api/organisation/departments/{FakeCatalog.KommunledningskontoretId}/member-groups",
            new { memberGroups = new[] { "SG-Admin" } }, Ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Retention_loops_the_config_step_until_a_short_batch_and_skips_while_another_instance_holds_the_lock()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway);
        await using (var connection = new NpgsqlConnection(database.Connection(RagDatabase.Admin)))
        {
            await connection.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO config."AdminSessions" ("SessionId", "Value", "ExpiresAt")
                SELECT 'expired-' || g, '\x00'::bytea, now() - interval '1 hour' FROM generate_series(1, 12000) g
                """, connection);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var retention = new RetentionService(admin.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AdminOptions()),
            TimeProvider.System, NullLogger<RetentionService>.Instance);

        // Another instance holds the steps' locks: nothing is removed.
        await using (var holder = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false)))
        {
            await holder.OpenAsync(Ct);
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using var take = new NpgsqlCommand(
                $"SELECT pg_advisory_xact_lock({RetentionService.ConfigStepLockKey}), pg_advisory_xact_lock({RetentionService.ChatStepLockKey})", holder, transaction);
            await take.ExecuteNonQueryAsync(Ct);
            (await retention.RunOnceAsync(Ct)).ShouldBe(-1);
        }

        // The holder's rollback releases the lock on the server a moment after the call returns; a slow runner can see it
        // still held, so wait for a run that got the lock instead of failing on the race. 30 s: under the full suite's
        // parallel load 5 s was not always enough (main @ 4f19651); a run that never gets the lock still fails below.
        // A run can also get the lock and then see it "held" on a later iteration, which ends that run early (correct for two
        // instances, #212). So sum the runs until the expired rows are gone, and check that one run looped past a batch.
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        long removed = 0, largestRun = 0;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await db.AdminSessions.CountAsync(s => s.SessionId.StartsWith("expired-"), Ct) > 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            var run = await retention.RunOnceAsync(Ct);
            removed += Math.Max(run, 0);
            largestRun = Math.Max(largestRun, run);
            if (run <= 0)
            {
                await Task.Delay(50, Ct);
            }
        }

        removed.ShouldBeGreaterThanOrEqualTo(12000); // every expired session, in batches of 5000
        largestRun.ShouldBeGreaterThan(RetentionService.BatchSize); // a run loops while a batch comes back full
        (await db.AdminSessions.CountAsync(s => s.SessionId.StartsWith("expired-"), Ct)).ShouldBe(0);
    }
}
