using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Granskningslogg (admin design §9.1, P1b item 9): platform admins see every entry, department admins their departments'
/// entries; the filters and the keyset (newest first); the detail with the snapshot JSON as text; editors and viewers are
/// refused; each listed entry names its object (current name, else the snapshot's, else none).
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class AuditEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
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
            Details = """{"before":null,"after":{"name":"Webb"}}""", Service = "kalla-admin", DepartmentId = department, Classification = "public",
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
        var factory = host.Factory;
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
        var factory = host.Factory;
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var basePath = $"/api/audit?action={seed.Action}";

        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&entityId=s-other", Ct)).ShouldBe([seed.Other]);
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}", Ct))
            .ShouldBe([seed.Platform, seed.Other, seed.Mine]);
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&actor=anna", Ct)).Count().ShouldBe(4); // the name, case-insensitive
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&actor=o-{seed.Code}&service=kalla-worker", Ct)).ShouldBeEmpty();
        Ids(await platform.GetFromJsonAsync<JsonElement>($"{basePath}&entityType=collection", Ct)).ShouldBeEmpty();

        var first = await platform.GetFromJsonAsync<JsonElement>($"{basePath}&limit=3", Ct);
        Ids(first).ShouldBe([seed.Platform, seed.Other, seed.Mine]);
        var next = first.GetProperty("next").GetInt64();
        var second = await platform.GetFromJsonAsync<JsonElement>($"{basePath}&limit=3&after={next}", Ct);
        Ids(second).ShouldBe([seed.Older]);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Entries_name_their_object_by_its_current_name_else_by_the_snapshot()
    {
        var code = "N" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var profile = await db.EmbeddingProfiles.AsNoTracking().Select(p => new { p.Id, p.Name }).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "audit-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = new { created.Id, created.Name };
        }

        var collection = new Collection
        {
            Slug = "audit-" + code.ToLowerInvariant(), Name = "Bygglov " + code, Classification = Classification.Internal, EmbeddingProfileId = profile.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var second = new Collection
        {
            Slug = "audit2-" + code.ToLowerInvariant(), Name = "Avfall " + code, Classification = Classification.Internal, EmbeddingProfileId = profile.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "audit-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor " + code, Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var assistant = new Assistant
        {
            Slug = "audit-" + code.ToLowerInvariant(), Name = "Hjälpen " + code, Classification = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var client = new Client { Name = "Webb " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Public, UpdatedAt = DateTimeOffset.UtcNow };
        var model = "audit/" + code.ToLowerInvariant();
        db.GatewayModels.Add(new GatewayModel { Name = model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        db.AddRange(collection, second, pipeline, source, assistant, client);
        await db.SaveChangesAsync(Ct);

        var action = "names-" + code.ToLowerInvariant();
        var at = DateTimeOffset.UtcNow;
        var deleted = Guid.CreateVersion7();
        var missingRun = Guid.CreateVersion7();
        AuditLogEntry Entry(string type, string? id, string? details) => new()
        {
            Timestamp = at, RecordedAt = at, Actor = "o-" + code, Action = action, EntityType = type, EntityId = id, Details = details, Service = "kalla-admin",
        };
        // Oldest first, so the list (newest first) is the reverse of this order.
        var entries = new[]
        {
            Entry("collection", collection.Id.ToString(), """{"before":{"name":"Gammalt namn"},"after":{"name":"Gammalt namn"}}"""),
            Entry("collection", second.Id.ToString(), null),
            Entry("collection", collection.Id.ToString(), null),
            Entry("source", source.Id.ToString(), null),
            Entry("assistant", assistant.Id.ToString(), null),
            Entry("client", client.Id.ToString(), null),
            Entry("gateway-model", model, null),
            Entry("embedding-profile", profile.Id.ToString(), null),
            Entry("collection", deleted.ToString(), """{"before":{"name":"Borttagen"},"after":null}"""),
            Entry("run", missingRun.ToString(), """{"before":null,"after":{"jobId":"j"}}"""),
            Entry("settings", "chat", """{"before":{"value":{}},"after":{"value":{}}}"""),
            Entry("organisation", null, null),
        };
        db.AuditLog.AddRange(entries);
        await db.SaveChangesAsync(Ct);

        var factory = host.Factory;
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var items = (await platform.GetFromJsonAsync<JsonElement>($"/api/audit?action={action}", Ct)).GetProperty("items").EnumerateArray()
            .Select(i => (Name: i.GetProperty("entityName").GetString(), Exists: i.GetProperty("entityExists").ValueKind == JsonValueKind.Null ? (bool?)null : i.GetProperty("entityExists").GetBoolean()))
            .Reverse().ToList();

        items.ShouldBe(
        [
            ("Bygglov " + code, true), // the current name wins over the snapshot's
            ("Avfall " + code, true),
            ("Bygglov " + code, true),
            ("Sidor " + code, true),
            ("Hjälpen " + code, true),
            ("Webb " + code, true),
            (model, true),
            (profile.Name, true),
            ("Borttagen", false), // deleted: the snapshot's name
            (null, false), // a run has no name
            (null, null), // a settings section is not looked up and its snapshot has no name
            (null, null),
        ]);
    }
}
