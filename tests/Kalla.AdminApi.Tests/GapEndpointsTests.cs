using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The content-gap report (admin design §8.7; Decisions "Content-gap report"; B-62) on seeded <c>AssistantDailyGaps</c> and
/// <c>AssistantDailyTotals</c> rows: the nearest documents most first with their two counts, <i>no hit</i> as its own row, a
/// deleted document as deleted, another department's knowledge base named but not openable while it is shared with the
/// assistant's department and neither named nor titled once the share is withdrawn (#983/#999), the period's turns, the period
/// limit, the scope by the rows' department (404 for another department's assistant) and the CSV.
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class GapEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Range = "from=2025-03-01&to=2025-04-30";

    private sealed record Seeded(
        Department A, Department B, Assistant Own, Collection Kb, Collection Foreign, Document Altan, Document Plank, Document Gone, Document Elsewhere);

    private async Task<Seeded> SeedAsync(RagDbContext db)
    {
        var code = Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var a = new Department { Id = Guid.CreateVersion7(), Name = "Lucka A " + code, CostCenterCode = "GA" + code, SyncedAt = now };
        var b = new Department { Id = Guid.CreateVersion7(), Name = "Lucka B " + code, CostCenterCode = "GB" + code, SyncedAt = now };
        var profile = await new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin)).AddEmbeddingProfileAsync(8, Ct);
        var own = new Assistant { Slug = "gap-" + code, Name = "Luckguiden " + code, DepartmentId = a.Id, Classification = Classification.Internal, UpdatedAt = now };
        var kb = new Collection
        {
            Slug = "gap-a-" + code, Name = "Bygglov " + code, DepartmentId = a.Id, Classification = Classification.Internal, EmbeddingProfileId = profile, UpdatedAt = now,
        };
        var foreign = new Collection
        {
            Slug = "gap-b-" + code, Name = "Gator " + code, DepartmentId = b.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            SharedWith = CollectionSharingLevels.Municipality, UpdatedAt = now,
        };
        var pipeline = new Pipeline { Name = "gap-" + code, TemplateId = "url-list", UpdatedAt = now };
        var source = new Source { CollectionId = kb.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = now };
        var foreignSource = new Source
        {
            CollectionId = foreign.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = now,
        };
        Document Page(Source of, string slug, string title, DateTimeOffset? deleted = null)
        {
            var url = $"https://www.umea.se/{code}/{slug}";
            return new Document
            {
                SourceId = of.Id, Classification = Classification.Internal, IdentityKind = "url", CanonicalIdentity = url, Url = url, Title = title,
                IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(url)), DeletedAt = deleted,
            };
        }

        var altan = Page(source, "altan", "Altan och uteplats");
        var plank = Page(source, "plank", "Plank och mur");
        var gone = Page(source, "garage", "Garage", now);
        var elsewhere = Page(foreignSource, "parkering", "Parkering");
        db.AddRange(a, b, own, kb, foreign, pipeline, source, foreignSource, altan, plank, gone, elsewhere);
        await db.SaveChangesAsync(Ct);

        var (client, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        AssistantDailyGap Gap(string day, string outcome, Guid collection, Guid document, int turns, Guid? department = null, Guid? by = null) => new()
        {
            Day = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), AssistantId = own.Id, ClientId = by ?? client, Classification = Classification.Internal,
            Outcome = outcome, CollectionId = collection, DocumentId = document, DepartmentId = department ?? a.Id, Turns = turns,
        };
        db.AssistantDailyGaps.AddRange(
            Gap("2025-03-10", "not-found", kb.Id, altan.Id, 3),
            Gap("2025-03-11", "not-found", kb.Id, altan.Id, 1, by: other), // another client's turns add to the same document
            Gap("2025-04-02", "answered-general", kb.Id, altan.Id, 1),
            Gap("2025-03-12", "not-found", Guid.Empty, Guid.Empty, 4), // no hit
            Gap("2025-03-12", "answered-general", kb.Id, gone.Id, 3),
            Gap("2025-03-13", "not-found", foreign.Id, elsewhere.Id, 2),
            Gap("2025-03-14", "not-found", kb.Id, plank.Id, 1),
            // Left out for department A: a day outside the range, and a turn from the assistant's time in B.
            Gap("2024-12-31", "not-found", kb.Id, plank.Id, 50),
            Gap("2025-03-15", "not-found", kb.Id, plank.Id, 9, department: b.Id));
        db.AssistantDailyTotals.AddRange(
            new AssistantDailyTotal
            {
                Day = new DateOnly(2025, 3, 10), AssistantId = own.Id, ClientId = client, DepartmentId = a.Id, Classification = Classification.Internal, Messages = 15, NotFound = 3,
            },
            new AssistantDailyTotal
            {
                Day = new DateOnly(2025, 3, 11), AssistantId = own.Id, ClientId = other, DepartmentId = a.Id, Classification = Classification.Internal, Messages = 5, NotFound = 1,
            },
            new AssistantDailyTotal
            {
                Day = new DateOnly(2025, 3, 15), AssistantId = own.Id, ClientId = client, DepartmentId = b.Id, Classification = Classification.Internal, Messages = 30, NotFound = 9,
            });
        await db.SaveChangesAsync(Ct);
        return new Seeded(a, b, own, kb, foreign, altan, plank, gone, elsewhere);
    }

    private static async Task<JsonElement> OkAsync(HttpClient user, string path)
    {
        using var response = await user.GetAsync(path, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static (Guid Document, bool NoHit, string? Title, bool Deleted, bool NotShared, bool CanOpen, long NotFound, long General) Row(JsonElement r) =>
        (r.GetProperty("documentId").GetGuid(), r.GetProperty("noHit").GetBoolean(), r.GetProperty("documentTitle").GetString(), r.GetProperty("deleted").GetBoolean(),
            r.GetProperty("notShared").GetBoolean(), r.GetProperty("canOpen").GetBoolean(), r.GetProperty("notFound").GetInt64(), r.GetProperty("answeredGeneral").GetInt64());

    [Fact]
    public async Task The_nearest_documents_come_most_first_with_no_hit_a_deleted_document_and_another_departments_knowledge_base()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        var report = await OkAsync(viewer, $"/api/assistants/{s.Own.Id}/gaps?{Range}");
        (report.GetProperty("from").GetString(), report.GetProperty("to").GetString()).ShouldBe(("2025-03-01", "2025-04-30"));
        // The period's turns from the totals, the two counts from the gap rows: B's rows and the day outside the range left out.
        (report.GetProperty("turns").GetInt64(), report.GetProperty("notFound").GetInt64(), report.GetProperty("answeredGeneral").GetInt64()).ShouldBe((20L, 11L, 4L));
        var rows = report.GetProperty("documents").EnumerateArray().ToList();
        rows.Select(Row).ShouldBe(
        [
            (s.Altan.Id, false, "Altan och uteplats", false, false, true, 4, 1),
            (Guid.Empty, true, null, false, false, false, 4, 0), // no hit, its own row; a tie on the sum goes to the more not-found
            (s.Gone.Id, false, null, true, false, false, 0, 3),
            (s.Elsewhere.Id, false, "Parkering", false, false, false, 2, 0), // another department's, shared with A: named, not openable here
            (s.Plank.Id, false, "Plank och mur", false, false, true, 1, 0),
        ]);
        rows.Select(r => r.GetProperty("collectionName").GetString()).ShouldBe([s.Kb.Name, null, s.Kb.Name, s.Foreign.Name, s.Kb.Name]);
        rows[1].GetProperty("collectionId").GetGuid().ShouldBe(Guid.Empty);
        rows[3].GetProperty("collectionId").GetGuid().ShouldBe(s.Foreign.Id);

        // Nothing in the report names a question or who asked: the rows' fields are ids, names, titles, flags and counts.
        rows[0].EnumerateObject().Select(p => p.Name).ShouldBe(
            ["collectionId", "documentId", "noHit", "collectionName", "documentTitle", "deleted", "notShared", "canOpen", "notFound", "answeredGeneral"], ignoreOrder: true);

        // A period with no gap: the counts are 0 and the list empty.
        var empty = await OkAsync(viewer, $"/api/assistants/{s.Own.Id}/gaps?from=2025-05-01&to=2025-05-31");
        (empty.GetProperty("turns").GetInt64(), empty.GetProperty("notFound").GetInt64(), empty.GetProperty("documents").GetArrayLength()).ShouldBe((0L, 0L, 0));
    }

    [Fact]
    public async Task A_knowledge_base_no_longer_shared_shows_neither_name_nor_title()
    {
        // A row lives 3 years; once B withdraws the share, A's report keeps the counts under the ids but no longer names B's
        // knowledge base or document (V11 today, as the test questions' documents, #983/#999). B's admin, who owns it, still sees it.
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        await db.Collections.Where(c => c.Id == s.Foreign.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.SharedWith, CollectionSharingLevels.Department), Ct);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        var rows = (await OkAsync(viewer, $"/api/assistants/{s.Own.Id}/gaps?{Range}")).GetProperty("documents").EnumerateArray().ToList();
        var withdrawn = rows.Single(r => r.GetProperty("documentId").GetGuid() == s.Elsewhere.Id);
        Row(withdrawn).ShouldBe((s.Elsewhere.Id, false, null, false, true, false, 2L, 0L));
        withdrawn.GetProperty("collectionName").ValueKind.ShouldBe(JsonValueKind.Null);
        withdrawn.GetProperty("collectionId").GetGuid().ShouldBe(s.Foreign.Id); // the counts' key; a bare id names nothing
        rows.Single(r => r.GetProperty("documentId").GetGuid() == s.Altan.Id).GetProperty("collectionName").GetString().ShouldBe(s.Kb.Name); // own: unchanged
        rows.Single(r => r.GetProperty("documentId").GetGuid() == s.Gone.Id).GetProperty("deleted").GetBoolean().ShouldBeTrue();

        using var response = await viewer.GetAsync($"/api/assistants/{s.Own.Id}/gaps?{Range}&format=csv", Ct);
        var csv = Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync(Ct))[3..]);
        csv.Split("\r\n").ShouldContain("(not shared);(not shared);2;0");
        csv.ShouldNotContain(s.Foreign.Name);
        csv.ShouldNotContain("Parkering");

        // A platform admin's departments own B's knowledge base, so the caller may see it: named and openable.
        using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
        var owned = (await OkAsync(platform, $"/api/assistants/{s.Own.Id}/gaps?{Range}")).GetProperty("documents").EnumerateArray()
            .Single(r => r.GetProperty("documentId").GetGuid() == s.Elsewhere.Id);
        Row(owned).ShouldBe((s.Elsewhere.Id, false, "Parkering", false, false, true, 2L, 0L));
    }

    [Fact]
    public async Task The_scope_is_the_rows_department_and_another_departments_assistant_is_not_found()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var adminB = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.DepartmentAdmin, s.B.CostCenterCode);
        (await adminB.GetAsync($"/api/assistants/{s.Own.Id}/gaps?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A platform admin's departments include B, so the turns of the assistant's time in B are counted too.
        using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
        var report = await OkAsync(platform, $"/api/assistants/{s.Own.Id}/gaps?{Range}");
        (report.GetProperty("turns").GetInt64(), report.GetProperty("notFound").GetInt64()).ShouldBe((50L, 20L));
        report.GetProperty("documents").EnumerateArray().Select(Row).Single(r => r.Document == s.Plank.Id).NotFound.ShouldBe(10);
    }

    [Fact]
    public async Task The_csv_has_the_documented_headers_and_one_row_per_nearest_document()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        using var response = await viewer.GetAsync($"/api/assistants/{s.Own.Id}/gaps?{Range}&format=csv", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("content-gaps-2025-03-01-2025-04-30.csv");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ShouldBe(
        [
            "knowledge_base;document;not_found;answered_general",
            $"{s.Kb.Name};Altan och uteplats;4;1",
            ";(no hit);4;0",
            $"{s.Kb.Name};{s.Gone.Id} (deleted);0;3",
            $"{s.Foreign.Name};Parkering;2;0",
            $"{s.Kb.Name};Plank och mur;1;0",
        ]);
    }

    [Fact]
    public async Task A_period_over_400_days_is_period_too_long()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        using (var response = await viewer.GetAsync($"/api/assistants/{s.Own.Id}/gaps?from=2024-01-01&to=2025-02-04", Ct)) // 401 days
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.PeriodTooLong.Code);
        }

        (await viewer.GetAsync($"/api/assistants/{s.Own.Id}/gaps?from=2024-01-01&to=2025-02-03", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // 400 days
        (await viewer.GetAsync($"/api/assistants/{s.Own.Id}/gaps?from=2025-02-01&to=2025-01-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await viewer.GetAsync($"/api/assistants/{s.Own.Id}/gaps", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // the last 30 days by default
    }
}
