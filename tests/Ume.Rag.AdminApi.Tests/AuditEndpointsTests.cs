using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Granskningslogg (admin design §9.1, P1b item 9): platform admins see every entry, department admins their departments'
/// entries; the filters and the keyset (newest first); the detail with the snapshot JSON as text; editors and viewers are
/// refused.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class AuditEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, string Action, long Mine, long Other, long Platform, long Older);

    private async Task<Seed> SeedAsync()
    {
        var code = "A" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var mine = new Department { Id = Guid.CreateVersion7(), Name = "Revision " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var other = new Department { Id = Guid.CreateVersion7(), Name = "Annan " + code, CostCenterCode = "O" + code, SyncedAt = DateTimeOffset.UtcNow };
        db.AddRange(mine, other);
        await db.SaveChangesAsync(Ct);
        var action = "test-" + code.ToLowerInvariant(); // unique per test run, so the shared log's other entries never match
        var now = DateTimeOffset.UtcNow;
        AuditLogEntry Entry(Guid? department, DateTimeOffset at, string entityId) => new()
        {
            Timestamp = at, RecordedAt = at, Actor = "o-" + code, ActorName = "Anna Andersson", Action = action, EntityType = "source", EntityId = entityId,
            Details = """{"before":null,"after":{"name":"Webb"}}""", Service = "rag-admin", DepartmentId = department, Classification = "public",
            ConfigVersion = 7, Reason = department is null ? null : "Testorsak",
        };
        var older = Entry(mine.Id, now.AddDays(-3), "s-older");
        var minEntry = Entry(mine.Id, now.AddMinutes(-2), "s-mine");
        var otherEntry = Entry(other.Id, now.AddMinutes(-1), "s-other");
        var platform = Entry(null, now, "s-platform");
        db.AuditLog.AddRange(older, minEntry, otherEntry, platform);
        await db.SaveChangesAsync(Ct);
        return new Seed(code, action, minEntry.Id, otherEntry.Id, platform.Id, older.Id);
    }

    private static IEnumerable<long> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetInt64());

    [Fact]
    public async Task Platform_admins_see_every_entry_and_department_admins_their_departments_only()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var department = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);

        Ids(await platform.GetFromJsonAsync<JsonElement>($"/api/audit?action={seed.Action}", Ct))
            .ShouldBe([seed.Platform, seed.Other, seed.Mine, seed.Older]); // newest first
        Ids(await department.GetFromJsonAsync<JsonElement>($"/api/audit?action={seed.Action}", Ct)).ShouldBe([seed.Mine, seed.Older]);
        (await editor.GetAsync("/api/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await department.GetAsync($"/api/audit/{seed.Other}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var detail = await department.GetFromJsonAsync<JsonElement>($"/api/audit/{seed.Mine}", Ct);
        (detail.GetProperty("reason").GetString(), detail.GetProperty("actorName").GetString()).ShouldBe(("Testorsak", "Anna Andersson"));
        using var details = JsonDocument.Parse(detail.GetProperty("details").GetString()!); // jsonb: stored as text, reformatted by Postgres
        details.RootElement.GetProperty("after").GetProperty("name").GetString().ShouldBe("Webb");
    }

    [Fact]
    public async Task The_filters_and_the_keyset_narrow_the_list()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var basePath = $"/api/audit?action={seed.Action}";

        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&entityId=s-other", Ct)).ShouldBe([seed.Other]);
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}", Ct))
            .ShouldBe([seed.Platform, seed.Other, seed.Mine]);
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&actor=anna", Ct)).Count().ShouldBe(4); // the name, case-insensitive
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&actor=o-{seed.Code}&service=rag-worker", Ct)).ShouldBeEmpty();
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&entityType=collection", Ct)).ShouldBeEmpty();

        var first = await platform.GetFromJsonAsync<JsonElement>($"{basePath}&limit=3", Ct);
        Ids(first).ShouldBe([seed.Platform, seed.Other, seed.Mine]);
        var next = first.GetProperty("next").GetInt64();
        var second = await platform.GetFromJsonAsync<JsonElement>($"{basePath}&limit=3&after={next}", Ct);
        Ids(second).ShouldBe([seed.Older]);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
