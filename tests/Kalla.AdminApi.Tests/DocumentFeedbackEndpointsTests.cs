using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Feedback on documents (admin design §4.5, §8.8; Decisions "Downvotes per document"; B-66) on seeded <c>DocumentDailyFeedback</c>
/// rows: a knowledge base's documents most downvoted first, summed over every assistant that cited them, its own department's and
/// another's, which are counted and never named; a deleted document as deleted; the period and its limit; the scope (the owning
/// department, not one the knowledge base is shared with: 404) and the CSV, which holds no question, answer, comment or name.
/// </summary>
[Collection(AdminDatabaseGroup5.Name)]
public sealed class DocumentFeedbackEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Range = "from=2025-03-01&to=2025-04-30";

    private sealed record Seeded(Department A, Department B, Assistant Own, Assistant Foreign, Collection Kb, Document Altan, Document Plank, Document Gone);

    private async Task<Seeded> SeedAsync(RagDbContext db)
    {
        var code = Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var a = new Department { Id = Guid.CreateVersion7(), Name = "Ägare " + code, CostCenterCode = "DA" + code, SyncedAt = now };
        var b = new Department { Id = Guid.CreateVersion7(), Name = "Lånare " + code, CostCenterCode = "DB" + code, SyncedAt = now };
        var profile = await new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin)).AddEmbeddingProfileAsync(8, Ct);
        var own = new Assistant { Slug = "dfa-" + code, Name = "Egna guiden " + code, DepartmentId = a.Id, Classification = Classification.Internal, UpdatedAt = now };
        var foreign = new Assistant { Slug = "dfb-" + code, Name = "Lånade guiden " + code, DepartmentId = b.Id, Classification = Classification.Internal, UpdatedAt = now };
        // Shared with the whole municipality: B's assistant may cite it, but B does not own it.
        var kb = new Collection
        {
            Slug = "df-" + code, Name = "Bygglov " + code, DepartmentId = a.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            SharedWith = CollectionSharingLevels.Municipality, UpdatedAt = now,
        };
        var pipeline = new Pipeline { Name = "df-" + code, TemplateId = "url-list", UpdatedAt = now };
        var source = new Source { CollectionId = kb.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id, UpdatedAt = now };
        Document Page(string slug, string title, DateTimeOffset? deleted = null)
        {
            var url = $"https://www.umea.se/{code}/{slug}";
            return new Document
            {
                SourceId = source.Id, Classification = Classification.Internal, IdentityKind = "url", CanonicalIdentity = url, Url = url, Title = title,
                IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(url)), DeletedAt = deleted,
            };
        }

        var altan = Page("altan", "Altan och uteplats");
        var plank = Page("plank", "Plank och mur");
        var gone = Page("garage", "Garage", now);
        db.AddRange(a, b, own, foreign, kb, pipeline, source, altan, plank, gone);
        await db.SaveChangesAsync(Ct);

        var (client, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        DocumentDailyFeedback Row(string day, Assistant by, Guid document, int down, int up, Guid? collection = null, Guid? via = null) => new()
        {
            Day = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), AssistantId = by.Id, ClientId = via ?? client,
            Classification = Classification.Internal, CollectionId = collection ?? kb.Id, DocumentId = document, DepartmentId = by.DepartmentId, ThumbsDown = down,
            ThumbsUp = up,
        };
        db.DocumentDailyFeedback.AddRange(
            Row("2025-03-10", own, altan.Id, 2, 1),
            Row("2025-03-11", foreign, altan.Id, 1, 0), // another department's assistant: counted
            Row("2025-03-12", own, plank.Id, 0, 2),
            Row("2025-03-13", own, plank.Id, 0, 1, via: other), // another client of the same assistant
            Row("2025-03-12", own, gone.Id, 1, 0),
            Row("2025-03-14", own, Guid.CreateVersion7(), 0, 0), // a rating that was moved away: no row in the report
            // Left out: a day outside the range, and another knowledge base's row.
            Row("2024-12-31", own, plank.Id, 50, 0),
            Row("2025-03-15", own, altan.Id, 9, 0, collection: Guid.CreateVersion7()));
        await db.SaveChangesAsync(Ct);
        return new Seeded(a, b, own, foreign, kb, altan, plank, gone);
    }

    private static async Task<JsonElement> OkAsync(HttpClient user, string path)
    {
        using var response = await user.GetAsync(path, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static (Guid Document, string? Title, bool Deleted, long Down, long Up, int Assistants) Row(JsonElement r) =>
        (r.GetProperty("documentId").GetGuid(), r.GetProperty("title").GetString(), r.GetProperty("deleted").GetBoolean(), r.GetProperty("thumbsDown").GetInt64(),
            r.GetProperty("thumbsUp").GetInt64(), r.GetProperty("assistants").GetInt32());

    [Fact]
    public async Task The_documents_come_most_downvoted_first_summed_over_every_assistant_without_naming_them()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        using var response = await viewer.GetAsync($"/api/collections/{s.Kb.Id}/feedback?{Range}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var report = JsonDocument.Parse(text).RootElement;
        (report.GetProperty("from").GetString(), report.GetProperty("to").GetString()).ShouldBe(("2025-03-01", "2025-04-30"));
        var rows = report.GetProperty("documents").EnumerateArray().ToList();
        rows.Select(Row).ShouldBe(
        [
            (s.Altan.Id, "Altan och uteplats", false, 3, 1, 2), // A's and B's assistant
            (s.Gone.Id, null, true, 1, 0, 1),
            (s.Plank.Id, "Plank och mur", false, 0, 3, 1), // two clients of one assistant
        ]);

        // No content and no assistant: the rows' fields are the document, its title, a flag and counts.
        rows[0].EnumerateObject().Select(p => p.Name).ShouldBe(["documentId", "title", "deleted", "thumbsDown", "thumbsUp", "assistants"], ignoreOrder: true);
        text.ShouldNotContain(s.Own.Name);
        text.ShouldNotContain(s.Foreign.Name);
        text.ShouldNotContain(s.B.Name);
        text.ShouldNotContain(s.Foreign.Id.ToString());

        var empty = await OkAsync(viewer, $"/api/collections/{s.Kb.Id}/feedback?from=2025-05-01&to=2025-05-31");
        empty.GetProperty("documents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_owning_department_sees_it_and_a_department_it_is_shared_with_gets_not_found()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        // B links the knowledge base (shared with the municipality) and its assistant's ratings are in the counts, yet B sees none of them.
        using var adminB = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.DepartmentAdmin, s.B.CostCenterCode);
        (await adminB.GetAsync($"/api/collections/{s.Kb.Id}/feedback?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetAsync($"/api/collections/{s.Kb.Id}/feedback?{Range}&format=csv", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var stranger = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, "X" + s.A.CostCenterCode);
        (await stranger.GetAsync($"/api/collections/{s.Kb.Id}/feedback?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var adminA = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.DepartmentAdmin, s.A.CostCenterCode);
        (await adminA.GetAsync($"/api/collections/{Guid.CreateVersion7()}/feedback", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OkAsync(adminA, $"/api/collections/{s.Kb.Id}/feedback?{Range}")).GetProperty("documents").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task The_csv_has_the_documented_headers_and_one_row_per_document_without_a_name()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        using var response = await viewer.GetAsync($"/api/collections/{s.Kb.Id}/feedback?{Range}&format=csv", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("document-feedback-2025-03-01-2025-04-30.csv");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        var csv = Encoding.UTF8.GetString(bytes[3..]);
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ShouldBe(
        [
            "document;thumbs_down;thumbs_up;assistants",
            "Altan och uteplats;3;1;2",
            $"{s.Gone.Id} (deleted);1;0;1",
            "Plank och mur;0;3;1",
        ]);
        csv.ShouldNotContain(s.Own.Name);
        csv.ShouldNotContain(s.Foreign.Name);
    }

    [Fact]
    public async Task A_period_over_400_days_is_period_too_long()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, s.A.CostCenterCode);

        using (var response = await viewer.GetAsync($"/api/collections/{s.Kb.Id}/feedback?from=2024-01-01&to=2025-02-04", Ct)) // 401 days
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.PeriodTooLong.Code);
        }

        (await viewer.GetAsync($"/api/collections/{s.Kb.Id}/feedback?from=2024-01-01&to=2025-02-03", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // 400 days
        (await viewer.GetAsync($"/api/collections/{s.Kb.Id}/feedback", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // the last 30 days by default
    }
}
