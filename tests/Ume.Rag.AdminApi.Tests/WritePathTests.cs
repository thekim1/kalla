using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The admin write path (admin design §4.4, §11.2): the version lock, the change, the outbox row and version bump, and one
/// audit entry with that version, in one transaction; database refusals answer their error codes.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class WritePathTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NewClient(string name, string? oauthClientId = null) => new
    {
        name, kind = ClientKinds.Integration, classificationCeiling = "internal", oAuthClientId = oauthClientId,
    };

    private async Task<long> ConfigVersionAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        return await db.ConfigVersion.Select(v => v.Version).SingleAsync(Ct);
    }

    private async Task<List<AuditLogEntry>> AuditOfAsync(string entityId)
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        return await db.AuditLog.Where(a => a.EntityId == entityId).OrderBy(a => a.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_saved_change_bumps_the_config_version_with_one_audit_entry()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = await ConfigVersionAsync();

        using var created = await admin.PostAsJsonAsync("/api/clients", NewClient("Write path " + Guid.NewGuid()), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var client = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("client");
        var id = client.GetProperty("id").GetString()!;

        var after = await ConfigVersionAsync();
        after.ShouldBeGreaterThan(before);
        var audit = (await AuditOfAsync(id)).ShouldHaveSingleItem();
        audit.Action.ShouldBe("create");
        audit.EntityType.ShouldBe(AuditEntityTypes.Client);
        audit.ConfigVersion.ShouldBe(after);
        audit.Actor.ShouldBe("test-user"); // the oid
        audit.ActorRoles.ShouldBe(RagRoles.PlatformAdmin);
        audit.Service.ShouldBe(AdminContext.ServiceName);
        audit.Classification.ShouldBe("internal");
        using (var details = JsonDocument.Parse(audit.Details!))
        {
            details.RootElement.GetProperty("before").ValueKind.ShouldBe(JsonValueKind.Null);
            details.RootElement.GetProperty("after").GetProperty("name").GetString().ShouldStartWith("Write path ");
        }

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.ConfigOutbox.CountAsync(o => o.Version == after, Ct)).ShouldBeGreaterThan(0);

        // An update through the same path: one more entry, with before and after.
        var update = new
        {
            name = client.GetProperty("name").GetString(), kind = ClientKinds.Integration, classificationCeiling = "public",
            version = client.GetProperty("version").GetString(),
        };
        using var updated = await admin.PutAsJsonAsync($"/api/clients/{id}", update, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        var entries = await AuditOfAsync(id);
        entries.Count.ShouldBe(2);
        entries[1].Action.ShouldBe("update");
        entries[1].ConfigVersion.ShouldBe(await ConfigVersionAsync());
        entries[1].ConfigVersion!.Value.ShouldBeGreaterThan(after);
    }

    [Fact]
    public async Task A_stale_version_is_409_concurrent_change_with_the_current_version()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var created = await admin.PostAsJsonAsync("/api/clients", NewClient("Stale " + Guid.NewGuid()), Ct);
        var client = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("client");
        var id = client.GetProperty("id").GetString()!;
        var first =client.GetProperty("version").GetString();
        var body = new { name = client.GetProperty("name").GetString(), kind = ClientKinds.Integration, classificationCeiling = "public", version = first };
        (await admin.PutAsJsonAsync($"/api/clients/{id}", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var stale = await admin.PutAsJsonAsync($"/api/clients/{id}", body with { classificationCeiling = "internal" }, Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ConcurrentChange.Code);
        var current = problem.GetProperty("version").GetString();
        current.ShouldNotBe(first);
        (await AuditOfAsync(id)).Count.ShouldBe(2);

        // With the current version the same change goes through.
        (await admin.PutAsJsonAsync($"/api/clients/{id}", body with { classificationCeiling = "internal", version = current }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_check_violation_is_400_platform_rule_and_writes_nothing()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = await ConfigVersionAsync();

        // CK_Clients_Mcp: an MCP client is never sensitive (CK_Clients_History is a field error first, ClientEndpointsTests).
        using var refused = await admin.PostAsJsonAsync("/api/clients",
            new { name = "MCP " + Guid.NewGuid(), kind = ClientKinds.McpClient, classificationCeiling = "sensitive" }, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.PlatformRule.Code);
        problem.GetProperty("params").GetProperty("constraint").GetString().ShouldBe("CK_Clients_Mcp");
        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_unique_violation_is_409_already_exists()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var azp = "azp-" + Guid.NewGuid();
        (await admin.PostAsJsonAsync("/api/clients", NewClient("First " + azp, oauthClientId: azp), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var second = await admin.PostAsJsonAsync("/api/clients", NewClient("Second " + azp, oauthClientId: azp), Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.AlreadyExists.Code);
    }

    [Fact]
    public async Task Field_errors_carry_invalid_value_codes()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        // Attribute rules run first; the cross-field rules only when every attribute passes (System.ComponentModel.DataAnnotations).
        using var refused = await admin.PostAsJsonAsync("/api/clients", new { name = "Fields", kind = "fax", classificationCeiling = "secret" }, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var codes = problem.GetProperty("errorCodes");
        foreach (var field in new[] { "kind", "classificationCeiling" })
        {
            codes.GetProperty(field)[0].GetProperty("code").GetString().ShouldBe(AdminErrorCodes.InvalidValue.Code);
            problem.GetProperty("errors").GetProperty(field).GetArrayLength().ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_failure_after_the_change_rolls_back_the_change_the_outbox_and_the_version()
    {
        // The audit insert fails (EntityType longer than its column) after the change was saved in the transaction.
        var factory = host.Factory;
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "o-1"), new Claim("roles", RagRoles.PlatformAdmin)], "test", "name", "roles")),
        };
        var admin = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var before = await ConfigVersionAsync();
        var id = Guid.CreateVersion7();

        await Should.ThrowAsync<DbUpdateException>(() => admin.WriteAsync(_ =>
        {
            admin.Db.Clients.Add(new Client { Id = id, Name = "Rolled back", Kind = ClientKinds.Integration, ClassificationCeiling = "internal" });
            return Task.FromResult(new AdminChange<bool>(true, "create", new string('x', 500), id, null, null));
        }, Ct));

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Clients.AnyAsync(c => c.Id == id, Ct)).ShouldBeFalse();
        (await ConfigVersionAsync()).ShouldBe(before);
        (await db.ConfigOutbox.AnyAsync(o => o.Version > before, Ct)).ShouldBeFalse();
    }

    /// <summary>Collects the <c>rag.admin.config_lock.wait</c> measurements (seconds) of <paramref name="factory"/>'s meters only.</summary>
    private static MeterListener LockWaits(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, List<double> waits)
    {
        var meters = factory.Services.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == ConfigLockMetrics.WaitName && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    instrument.Unit.ShouldBe("s");
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            lock (waits)
            {
                waits.Add(value);
            }
        });
        listener.Start();
        return listener;
    }

    /// <summary>Holds the config version lock on its own connection until disposed (rolled back).</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldLockAsync()
    {
        var holder = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false));
        await holder.OpenAsync(Ct);
        var transaction = await holder.BeginTransactionAsync(Ct);
        await using var lockRow = new NpgsqlCommand("""SELECT 1 FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE""", holder, transaction);
        await lockRow.ExecuteNonQueryAsync(Ct);
        return (holder, transaction);
    }

    /// <summary>Waits until another backend waits for a lock on the config version row (the admin's write is queued).</summary>
    private async Task UntilAWriteWaitsForTheLockAsync()
    {
        await using var probe = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false));
        await probe.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE wait_event_type = 'Lock' AND query LIKE '%config."ConfigVersion"%FOR UPDATE%' AND pid <> pg_backend_pid()
            """, probe);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while ((long)(await waiting.ExecuteScalarAsync(Ct))! == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "no config write queued behind the lock");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Every_config_write_records_its_lock_wait_and_a_held_lock_a_longer_one()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        List<double> waits = [];
        using var listener = LockWaits(factory, waits);

        // Uncontended: one measurement per write.
        using (var first = await admin.PostAsJsonAsync("/api/clients", NewClient("Lock wait " + Guid.NewGuid()), Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        }

        lock (waits) { waits.Count.ShouldBe(1); }
        using (var second = await admin.PostAsJsonAsync("/api/clients", NewClient("Lock wait " + Guid.NewGuid()), Ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Created, await second.Content.ReadAsStringAsync(Ct));
        }

        double[] uncontended;
        lock (waits) { uncontended = [.. waits]; }
        uncontended.Length.ShouldBe(2);

        // Contended: another transaction holds the lock while the write waits; the release comes after a known hold.
        var (holder, transaction) = await HoldLockAsync();
        await using (holder)
        await using (transaction)
        {
            var write = admin.PostAsJsonAsync("/api/clients", NewClient("Lock wait " + Guid.NewGuid()), Ct);
            await UntilAWriteWaitsForTheLockAsync();
            var held = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            await transaction.RollbackAsync(Ct);
            held.Stop();
            using var contended = await write;
            contended.StatusCode.ShouldBe(HttpStatusCode.Created, await contended.Content.ReadAsStringAsync(Ct));

            double[] all;
            lock (waits) { all = [.. waits]; }
            all.Length.ShouldBe(3);
            all[2].ShouldBeGreaterThanOrEqualTo(held.Elapsed.TotalSeconds);
            all[2].ShouldBeGreaterThan(uncontended.Max());
        }
    }

    [Fact]
    public async Task A_lock_held_longer_than_5_seconds_is_503_config_busy()
    {
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        await using var holder = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false));
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("""SELECT 1 FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE""", holder, transaction))
        {
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        List<double> waits = [];
        using var listener = LockWaits(factory, waits);

        var name = "Busy " + Guid.NewGuid();
        using var busy = await admin.PostAsJsonAsync("/api/clients", NewClient(name), Ct);

        // The outcome, not the wall clock (#297): the write waited for the lock, gave up with config_busy and left nothing.
        busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await busy.Content.ReadAsStringAsync(Ct));
        (await busy.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ConfigBusy.Code);
        // The busy wait is measured too: lock_timeout runs on the server, so the client-side wait covers it.
        lock (waits) { waits.ShouldHaveSingleItem().ShouldBeGreaterThanOrEqualTo(ConfigWrites.LockTimeout.TotalSeconds); }
        await transaction.RollbackAsync(Ct);
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Clients.AnyAsync(c => c.Name == name, Ct)).ShouldBeFalse();

        // Once the lock is free the same write goes through.
        using var after = await admin.PostAsJsonAsync("/api/clients", NewClient(name), Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Created, await after.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Department_lists_are_scoped_to_the_users_departments()
    {
        var mine = "D" + Random.Shared.Next(100_000, 999_999);
        var other = "D" + Random.Shared.Next(100_000, 999_999);
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Admin)))
        {
            db.Departments.AddRange(
                new Department { Id = Guid.CreateVersion7(), Name = "Mine", CostCenterCode = mine, SyncedAt = DateTimeOffset.UtcNow },
                new Department { Id = Guid.CreateVersion7(), Name = "Other", CostCenterCode = other, SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, mine);
        var seen = await viewer.GetFromJsonAsync<JsonElement[]>("/api/organisation/departments", Ct);
        seen!.Select(d => d.GetProperty("costCenterCode").GetString()).ShouldBe([mine]);

        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var all = await platform.GetFromJsonAsync<JsonElement[]>("/api/organisation/departments", Ct);
        all!.Select(d => d.GetProperty("costCenterCode").GetString()).ShouldContain(mine);
        all!.Select(d => d.GetProperty("costCenterCode").GetString()).ShouldContain(other);
    }

    [Fact]
    public async Task Department_list_carries_member_groups_and_the_sharing_summary()
    {
        var code = "S" + Random.Shared.Next(100_000, 999_999);
        Department NewDepartment(string suffix, params string[] groups) => new()
        {
            Id = Guid.CreateVersion7(), Name = "Sharing " + code + suffix, CostCenterCode = code + suffix, MemberGroups = groups, SyncedAt = DateTimeOffset.UtcNow,
        };
        var owner = NewDepartment("O", "Nämnden", "Förvaltningen");
        var first = NewDepartment("A");
        var second = NewDepartment("B");
        var profile = new EmbeddingProfile
        {
            Name = "sharing-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, UpdatedAt = DateTimeOffset.UtcNow,
        };
        Collection NewCollection(string level, Guid? department = null, DateTimeOffset? deletedAt = null) => new()
        {
            Slug = $"sharing-{code.ToLowerInvariant()}-{Guid.NewGuid():N}", Name = "Sharing " + level, DepartmentId = department ?? owner.Id,
            Classification = Classification.Public, EmbeddingProfileId = profile.Id, SharedWith = level, DeletedAt = deletedAt, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var own = NewCollection(CollectionSharingLevels.Department);
        var selectedA = NewCollection(CollectionSharingLevels.Selected);
        var selectedB = NewCollection(CollectionSharingLevels.Selected);
        var municipality = NewCollection(CollectionSharingLevels.Municipality);
        var deleted = NewCollection(CollectionSharingLevels.Municipality, deletedAt: DateTimeOffset.UtcNow);
        var demoted = NewCollection(CollectionSharingLevels.Department);
        var othersSelected = NewCollection(CollectionSharingLevels.Selected, first.Id);
        CollectionShare Share(Collection collection, Department department) => new()
        {
            CollectionId = collection.Id, DepartmentId = department.Id, SharedBy = "test", SharedAt = DateTimeOffset.UtcNow,
        };
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.AddRange(owner, first, second, profile, own, selectedA, selectedB, municipality, deleted, demoted, othersSelected);
            await db.SaveChangesAsync(Ct);
            // Kept rows of a collection no longer at "selected" don't count, nor do another department's shares.
            db.AddRange(Share(selectedA, first), Share(selectedB, first), Share(selectedB, second), Share(demoted, second), Share(othersSelected, owner));
            await db.SaveChangesAsync(Ct);
        }

        var factory = host.Factory;
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var all = await platform.GetFromJsonAsync<JsonElement[]>("/api/organisation/departments", Ct);
        var row = all!.Single(d => d.GetProperty("costCenterCode").GetString() == owner.CostCenterCode);
        row.GetProperty("memberGroups").EnumerateArray().Select(g => g.GetString()).ShouldBe(["Nämnden", "Förvaltningen"]);
        var sharing = row.GetProperty("sharing");
        sharing.GetProperty("department").GetInt32().ShouldBe(2);
        sharing.GetProperty("selected").GetInt32().ShouldBe(2);
        sharing.GetProperty("municipality").GetInt32().ShouldBe(1);
        sharing.GetProperty("sharedWithDepartments").GetInt32().ShouldBe(2);

        var empty = all!.Single(d => d.GetProperty("costCenterCode").GetString() == second.CostCenterCode).GetProperty("sharing");
        empty.GetProperty("selected").GetInt32().ShouldBe(0);
        empty.GetProperty("sharedWithDepartments").GetInt32().ShouldBe(0);
    }
}
