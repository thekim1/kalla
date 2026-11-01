using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The admin write path (admin design §4.4, §11.2): the version lock, the change, the outbox row and version bump, and one
/// audit entry with that version, in one transaction; database refusals answer their error codes.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class WritePathTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NewClient(string name, string ceiling = "internal", bool storesHistory = false, string? oauthClientId = null) => new
    {
        name, kind = ClientKinds.Integration, classificationCeiling = ceiling, storesHistory, oAuthClientId = oauthClientId,
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
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = await ConfigVersionAsync();

        using var created = await admin.PostAsJsonAsync("/api/clients", NewClient("Write path " + Guid.NewGuid()), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var client = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct));
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
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var created = await admin.PostAsJsonAsync("/api/clients", NewClient("Stale " + Guid.NewGuid()), Ct);
        var client = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = client.GetProperty("id").GetString()!;
        var first = client.GetProperty("version").GetString();
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
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = await ConfigVersionAsync();

        // CK_Clients_History: a client that keeps chat history never reaches sensitive assistants.
        using var refused = await admin.PostAsJsonAsync("/api/clients", NewClient("History " + Guid.NewGuid(), "sensitive", storesHistory: true), Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.PlatformRule.Code);
        problem.GetProperty("params").GetProperty("constraint").GetString().ShouldBe("CK_Clients_History");
        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_unique_violation_is_409_already_exists()
    {
        await using var factory = database.HeaderFactory();
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
        await using var factory = database.HeaderFactory();
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
        await using var factory = database.HeaderFactory();
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

    [Fact]
    public async Task A_lock_held_longer_than_5_seconds_is_503_config_busy()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        await using var holder = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false));
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("""SELECT 1 FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE""", holder, transaction))
        {
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var watch = Stopwatch.StartNew();
        using var busy = await admin.PostAsJsonAsync("/api/clients", NewClient("Busy " + Guid.NewGuid()), Ct);
        watch.Stop();

        busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await busy.Content.ReadAsStringAsync(Ct));
        (await busy.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ConfigBusy.Code);
        // One wait for the lock, not one per retry of the execution strategy: two waits take at least 10 s, so the bound is
        // just under that, leaving ~5 s for the request on a busy machine (it flaked at 9 s; #188 review).
        watch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5));
        watch.Elapsed.ShouldBeLessThan(ConfigWrites.LockTimeout * 2 - TimeSpan.FromMilliseconds(100));
        await transaction.RollbackAsync(Ct);
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

        await using var factory = database.HeaderFactory();
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, mine);
        var seen = await viewer.GetFromJsonAsync<JsonElement[]>("/api/organisation/departments", Ct);
        seen!.Select(d => d.GetProperty("costCenterCode").GetString()).ShouldBe([mine]);

        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var all = await platform.GetFromJsonAsync<JsonElement[]>("/api/organisation/departments", Ct);
        all!.Select(d => d.GetProperty("costCenterCode").GetString()).ShouldContain(mine);
        all!.Select(d => d.GetProperty("costCenterCode").GetString()).ShouldContain(other);
    }
}
