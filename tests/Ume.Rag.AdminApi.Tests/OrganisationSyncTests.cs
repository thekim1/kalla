extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using FakeCatalog = fakegateway::Ume.Rag.FakeGateway.FakeCatalog;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>Its own ragdb: the sync deactivates every department the catalogue does not list.</summary>
public sealed class OrgSyncFixture : RagDatabase, IAsyncLifetime
{
    public string DataApiKey { get; } = TestSecrets.RandomHex();

    /// <summary>The FakeGateway with its Data API catalogue behind <see cref="DataApiKey"/>.</summary>
    public WebApplicationFactory<FakeGatewayProgram> Gateway(string? key = null) => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        web.UseSetting("FakeGateway:DataApiKey", key ?? DataApiKey));

    /// <summary>The admin API (header sign-in) syncing from <paramref name="gateway"/> with <paramref name="key"/>.</summary>
    public WebApplicationFactory<Program> AdminApi(WebApplicationFactory<FakeGatewayProgram> gateway, string? key = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:ragdb", Connection(Admin));
            web.UseSetting("Oidc:Authority", "https://idp.invalid/realms/ume");
            web.UseSetting("OrgSync:BaseUrl", "http://fake-gateway/");
            web.UseSetting("OrgSync:DataApiKey", key ?? DataApiKey);
            web.UseSetting("OrgSync:IntervalMinutes", "1440");
            web.UseSetting("Admin:Retention", "false");
            web.ConfigureTestServices(services =>
            {
                services.AddHeaderAuthentication();
                services.AddHttpClient(OrganisationSync.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            });
        });
}

/// <summary>The organisation sync against the FakeGateway catalogue (admin design §4.10, §11.2 "Sync").</summary>
public sealed class OrganisationSyncTests(OrgSyncFixture database) : IClassFixture<OrgSyncFixture>
{
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
        // The model sync shares the Data API settings: let its first run finish too, so it cannot move the config version later.
        await admin.Services.GetRequiredService<GatewayModelSync>().RunAsync(null, Ct);
        return sync;
    }

    [Fact]
    public async Task Synced_departments_and_teams_are_visible_and_a_second_run_writes_nothing()
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

        var version = await ConfigVersionAsync();
        (await sync.RunAsync(null, Ct)).ShouldBe(new OrgSyncResult(0, 0));
        (await ConfigVersionAsync()).ShouldBe(version);

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Departments.SingleAsync(d => d.CostCenterCode == "3000", Ct)).MemberGroups.ShouldBe(["Byggnadsnämnden"]);
    }

    [Fact]
    public async Task Only_changed_rows_are_written_with_one_audit_entry_and_missing_ones_are_deactivated()
    {
        await using var gateway = database.Gateway();
        await using var admin = database.AdminApi(gateway);
        var sync = await StartedAsync(admin);
        var stray = "S" + Random.Shared.Next(100_000, 999_999);
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            var renamed = await db.Departments.SingleAsync(d => d.Id == FakeCatalog.FritidsforvaltningenId, Ct);
            renamed.Name = "Renamed by hand";
            db.Departments.Add(new Department { Id = Guid.CreateVersion7(), Name = "Not in the gateway", CostCenterCode = stray, SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var before = await ConfigVersionAsync();
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        using var response = await platform.PostAsync("/api/organisation/sync", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<OrgSyncResult>(Ct)).ShouldBe(new OrgSyncResult(2, 0));
        var after = await ConfigVersionAsync();
        after.ShouldBeGreaterThan(before);
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            (await db.Departments.SingleAsync(d => d.Id == FakeCatalog.FritidsforvaltningenId, Ct)).Name.ShouldBe("Fritidsförvaltningen");
            (await db.Departments.SingleAsync(d => d.CostCenterCode == stray, Ct)).IsActive.ShouldBeFalse();
            var audit = await db.AuditLog.Where(a => a.EntityType == "organisation" && a.ConfigVersion == after).ToListAsync(Ct);
            audit.ShouldHaveSingleItem().Actor.ShouldBe("test-user"); // Sync now by an admin: the admin is the actor
        }

        sync.Status.ConsecutiveFailures.ShouldBe(0);
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
        problem.GetProperty("params").GetProperty("error").GetString().ShouldBe("HttpRequestException (HTTP 401)");
        sync.Status.ConsecutiveFailures.ShouldBeGreaterThanOrEqualTo(1);
        (await ConfigVersionAsync()).ShouldBe(version);
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Departments.CountAsync(d => d.IsActive, Ct)).ShouldBe(FakeCatalog.Departments.Count);
    }

    [Fact]
    public async Task Member_groups_absent_from_the_catalogue_keep_the_stored_ones()
    {
        // Gateway release 1 sends no memberGroups; until release 2 they are entered by hand (identity plan §3).
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var id = Guid.CreateVersion7();
        db.Departments.Add(new Department { Id = id, Name = "Hand", CostCenterCode = "H" + Random.Shared.Next(100_000, 999_999), MemberGroups = ["Nämnden"] });
        await db.SaveChangesAsync(Ct);
        var stored = await db.Departments.SingleAsync(d => d.Id == id, Ct);

        // Applied but not saved: the other departments would be deactivated, this one is left as it is.
        await OrganisationSync.ApplyAsync(db, [new CatalogDepartmentItem(id, "Hand", stored.CostCenterCode, true, null)], [], DateTimeOffset.UtcNow, Ct);

        db.Entry(stored).State.ShouldBe(EntityState.Unchanged);
        stored.MemberGroups.ShouldBe(["Nämnden"]);
        db.ChangeTracker.Clear();
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

        // Another instance holds the step's lock: nothing is removed.
        await using (var holder = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false)))
        {
            await holder.OpenAsync(Ct);
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({RetentionService.ConfigStepLockKey})", holder, transaction);
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
