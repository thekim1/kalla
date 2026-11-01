using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The web editors' report (issue #175): held and flagged-but-live documents of a <c>piiMode: report</c> source in a
/// <c>public</c> collection, with counts per type (numbers only) and the last <c>pii.flagged</c> run item.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class PiiReportEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid ReportSource, Guid HoldSource, Guid Flagged, Guid Clean, Guid Held, Guid FlagRun);

    private async Task<Seed> SeedAsync()
    {
        var code = "P" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Pii " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = new EmbeddingProfile
        {
            Name = "pii-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var collection = new Collection
        {
            Slug = "pii-" + code.ToLowerInvariant(), Name = "Webb " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "pii-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
        Source NewSource(string mode) => new()
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = mode, Classification = Classification.Public, PipelineId = pipeline.Id,
            Settings = $$"""{"urls":["https://www.umea.se/{{code}}"],"piiMode":"{{mode}}"}""", UpdatedAt = DateTimeOffset.UtcNow,
        };
        var report = NewSource("report");
        var hold = NewSource("hold");
        db.AddRange(department, profile, collection, pipeline, version, report, hold);
        await db.SaveChangesAsync(Ct);

        Document NewDocument(Source source, string path) => new()
        {
            SourceId = source.Id, Classification = Classification.Public, IdentityKind = "url", CanonicalIdentity = $"https://www.umea.se/{code}/{path}",
            IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{code}/{path}")), Title = path, Url = $"https://www.umea.se/{code}/{path}",
        };
        ChunkSet NewSet(Document document, string state, bool flagged, string? counts) => new()
        {
            DocumentId = document.Id, Classification = Classification.Public, CollectionId = collection.Id, PipelineVersionId = version.Id,
            EmbeddingProfileId = profile.Id, ContentHash = RandomNumberGenerator.GetBytes(32), InputHash = RandomNumberGenerator.GetBytes(32),
            State = state, PiiFlagged = flagged, PiiCounts = counts, CreatedAt = DateTimeOffset.UtcNow, LiveAt = state == ChunkSetStates.Live ? DateTimeOffset.UtcNow : null,
        };
        var flaggedDoc = NewDocument(report, "kontakt");
        var cleanDoc = NewDocument(report, "taxor");
        var heldDoc = NewDocument(report, "personal");
        var holdDoc = NewDocument(hold, "kontakt");
        db.AddRange(flaggedDoc, cleanDoc, heldDoc, holdDoc);
        await db.SaveChangesAsync(Ct);
        const string Counts = """{"personnummer":0,"samordningsnummer":0,"email":3,"emailExternal":2,"phone":1,"name":0,"ner":"skipped","rules":1}""";
        var flaggedSet = NewSet(flaggedDoc, ChunkSetStates.Live, true, Counts);
        var cleanSet = NewSet(cleanDoc, ChunkSetStates.Live, false, """{"email":1,"rules":0}""");
        var heldSet = NewSet(heldDoc, ChunkSetStates.Held, true, Counts);
        var holdSet = NewSet(holdDoc, ChunkSetStates.Live, true, Counts);
        // taxor's next version came back flagged and held; its clean older version stays live.
        var cleanNextHeld = NewSet(cleanDoc, ChunkSetStates.Held, true, """{"personnummer":1,"rules":1}""");
        db.AddRange(flaggedSet, cleanSet, heldSet, holdSet, cleanNextHeld);
        await db.SaveChangesAsync(Ct);
        flaggedDoc.LiveChunkSetId = flaggedSet.Id;
        cleanDoc.LiveChunkSetId = cleanSet.Id;
        holdDoc.LiveChunkSetId = holdSet.Id; // heldDoc has no live set: its flagged set is held

        var now = DateTimeOffset.UtcNow;
        var older = new Run { SourceId = report.Id, Classification = Classification.Public, Kind = "sync", Trigger = "schedule", State = RunStates.Succeeded, StartedAt = now.AddDays(-2), FinishedAt = now.AddDays(-2) };
        var newer = new Run { SourceId = report.Id, Classification = Classification.Public, Kind = "sync", Trigger = "schedule", State = RunStates.Succeeded, StartedAt = now.AddHours(-1), FinishedAt = now.AddHours(-1) };
        db.Runs.AddRange(older, newer);
        await db.SaveChangesAsync(Ct);
        db.RunItems.AddRange(
            new RunItem { RunId = older.Id, CreatedAt = now.AddDays(-2), Classification = Classification.Public, DocumentId = flaggedDoc.Id, Identity = flaggedDoc.CanonicalIdentity, Outcome = "updated", Reason = PiiReportEndpoints.FlaggedReason },
            new RunItem { RunId = newer.Id, CreatedAt = now.AddHours(-1), Classification = Classification.Public, DocumentId = flaggedDoc.Id, Identity = flaggedDoc.CanonicalIdentity, Outcome = "updated", Reason = PiiReportEndpoints.FlaggedReason },
            new RunItem { RunId = newer.Id, CreatedAt = now.AddHours(-1), Classification = Classification.Public, DocumentId = cleanDoc.Id, Identity = cleanDoc.CanonicalIdentity, Outcome = "unchanged" });
        await db.SaveChangesAsync(Ct);
        return new Seed(code, report.Id, hold.Id, flaggedDoc.Id, cleanDoc.Id, heldDoc.Id, newer.Id);
    }

    [Fact]
    public async Task A_report_source_lists_its_held_then_flagged_live_documents_with_counts_only()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);

        var report = await editor.GetFromJsonAsync<JsonElement>($"/api/sources/{seed.ReportSource}/pii-report", Ct);
        (report.GetProperty("applies").GetBoolean(), report.GetProperty("piiMode").GetString(), report.GetProperty("truncated").GetBoolean())
            .ShouldBe((true, "report", false));
        var documents = report.GetProperty("documents").EnumerateArray().ToList();
        // Held first (by title), then the live flagged page; the clean live set itself is never listed.
        documents.Select(d => (d.GetProperty("documentId").GetGuid(), d.GetProperty("state").GetString(), d.GetProperty("previousVersionLive").GetBoolean()))
            .ShouldBe([(seed.Held, "held", false), (seed.Clean, "held", true), (seed.Flagged, "live", false)]);
        documents[1].GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).ShouldBe([("personnummer", 1)]);
        var document = documents[2];
        document.GetProperty("title").GetString().ShouldBe("kontakt");
        document.GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).ShouldBe([("email", 3), ("emailExternal", 2), ("phone", 1)]);
        document.GetProperty("rulesFired").GetInt32().ShouldBe(1);
        document.GetProperty("lastFlaggedRunId").GetGuid().ShouldBe(seed.FlagRun);
        document.GetProperty("lastFlaggedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(-1));

        // A hold source (the default): the report does not apply.
        var hold = await editor.GetFromJsonAsync<JsonElement>($"/api/sources/{seed.HoldSource}/pii-report", Ct);
        (hold.GetProperty("applies").GetBoolean(), hold.GetProperty("piiMode").GetString(), hold.GetProperty("documents").GetArrayLength()).ShouldBe((false, "hold", 0));

        // Another department's admin does not see the source at all.
        (await stranger.GetAsync($"/api/sources/{seed.ReportSource}/pii-report", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Counts_are_numbers_per_type_and_never_values()
    {
        var counts = PiiReportEndpoints.Counts("""{"personnummer":2,"name":4,"ner":"done","rules":2,"model":"kb-ner@1"}""", out var rules);
        counts.ShouldBe(new Dictionary<string, int> { ["personnummer"] = 2, ["name"] = 4 });
        rules.ShouldBe(2);
        PiiReportEndpoints.Counts(null, out rules).ShouldBeEmpty();
        rules.ShouldBe(0);
    }
}
