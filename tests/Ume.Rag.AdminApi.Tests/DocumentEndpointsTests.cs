using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Documents, holds and deletion decisions (admin design §4.5, §4.6 V26 and V28, P1b item 9): the list with its filters
/// and keyset, a document's chunk sets and PII state, the live chunks, and the decisions as <c>command</c> jobs whose
/// payloads match the worker's contracts (<c>contracts/jobs/v1</c>).
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class DocumentEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid Source, Guid Flagged, Guid Clean, Guid Held, Guid Deleted, Guid Awaiting, Guid Finished,
        IReadOnlyList<Guid> CleanChunks);

    private async Task<Seed> SeedAsync()
    {
        var code = "D" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Dok " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = new EmbeddingProfile
        {
            Name = "doc-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var collection = new Collection
        {
            Slug = "doc-" + code.ToLowerInvariant(), Name = "Dok " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "doc-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Webb", Classification = Classification.Public, PipelineId = pipeline.Id,
            Settings = $$"""{"urls":["https://www.umea.se/{{code}}"]}""", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, profile, collection, pipeline, version, source);
        await db.SaveChangesAsync(Ct);

        Document NewDocument(string path, string? title = null) => new()
        {
            SourceId = source.Id, Classification = Classification.Public, IdentityKind = "url", CanonicalIdentity = $"https://www.umea.se/{code}/{path}",
            IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{code}/{path}")), Title = title ?? path, Url = $"https://www.umea.se/{code}/{path}",
            ParseOutcome = "ok", LastSeenAt = DateTimeOffset.UtcNow,
        };
        var flagged = NewDocument("kontakt", "Kontakta oss");
        var clean = NewDocument("taxor", "Taxor och avgifter");
        var held = NewDocument("arkiv", "=HYPERLINK(\"x\")"); // a formula-like title: the CSV must not run it
        held.HeldAt = DateTimeOffset.UtcNow.AddDays(-1);
        held.HeldBy = "o-earlier";
        var deleted = NewDocument("borta");
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        db.AddRange(flagged, clean, held, deleted);
        await db.SaveChangesAsync(Ct);

        ChunkSet NewSet(Document document, bool piiFlagged, string? counts) => new()
        {
            DocumentId = document.Id, Classification = Classification.Public, CollectionId = collection.Id, PipelineVersionId = version.Id,
            EmbeddingProfileId = profile.Id, ContentHash = RandomNumberGenerator.GetBytes(32), InputHash = RandomNumberGenerator.GetBytes(32),
            State = ChunkSetStates.Live, PiiFlagged = piiFlagged, PiiCounts = counts, ChunkCount = 2, CreatedAt = DateTimeOffset.UtcNow, LiveAt = DateTimeOffset.UtcNow,
        };
        var flaggedSet = NewSet(flagged, true, """{"email":2,"emailExternal":1,"phone":1,"rules":1,"typesFired":["emailExternal"]}""");
        var cleanSet = NewSet(clean, false, null);
        db.AddRange(flaggedSet, cleanSet);
        await db.SaveChangesAsync(Ct);
        flagged.LiveChunkSetId = flaggedSet.Id;
        clean.LiveChunkSetId = cleanSet.Id;
        Chunk NewChunk(int ordinal, string text) => new()
        {
            ChunkSetId = cleanSet.Id, DocumentId = clean.Id, CollectionId = collection.Id, Classification = Classification.Public, Ordinal = ordinal,
            HeadingPath = "Taxor › Avfall", Text = text, SearchText = text, TextHash = SHA256.HashData(Encoding.UTF8.GetBytes(text)), TokenCount = 5,
            DeletedAt = null,
        };
        var second = NewChunk(1, "Avgiften för tömning är 1 200 kronor.");
        var first = NewChunk(0, "Avfallstaxan gäller från januari.");
        db.AddRange(second, first);

        var now = DateTimeOffset.UtcNow;
        var awaiting = new Run
        {
            SourceId = source.Id, Classification = Classification.Public, Kind = "sync", Trigger = "schedule", State = RunStates.AwaitingDeletionConfirmation,
            StartedAt = now.AddHours(-1), ProposedDeletions = 2,
        };
        var finished = new Run
        {
            SourceId = source.Id, Classification = Classification.Public, Kind = "sync", Trigger = "schedule", State = RunStates.Succeeded,
            StartedAt = now.AddDays(-1), FinishedAt = now.AddDays(-1),
        };
        db.Runs.AddRange(awaiting, finished);
        await db.SaveChangesAsync(Ct);
        db.DeletionProposals.AddRange(
            new DeletionProposal { RunId = awaiting.Id, DocumentId = held.Id, SourceId = source.Id, Classification = Classification.Public, Reason = "not-found" },
            new DeletionProposal { RunId = awaiting.Id, DocumentId = clean.Id, SourceId = source.Id, Classification = Classification.Public, Reason = "gone" });
        db.RunItems.Add(new RunItem
        {
            RunId = finished.Id, CreatedAt = now.AddHours(-2), Classification = Classification.Public, DocumentId = clean.Id, Identity = clean.CanonicalIdentity,
            Outcome = "updated", Strategy = "html",
        });
        await db.SaveChangesAsync(Ct);
        return new Seed(code, source.Id, flagged.Id, clean.Id, held.Id, deleted.Id, awaiting.Id, finished.Id, [first.Id, second.Id]);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static IEnumerable<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid());

    private async Task<Job> JobAsync(string dedupKey)
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == dedupKey, Ct);
    }

    private static string? Field(Job job, string name)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        return payload.RootElement.GetProperty(name).GetString();
    }

    private static void Valid<T>(Job job)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        ContractSchemas.Validator(typeof(T)).Validate(payload.RootElement).ShouldBeEmpty(job.Payload);
    }

    [Fact]
    public async Task The_list_filters_pages_and_keeps_to_the_caller_s_departments()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);

        var all = await viewer.GetFromJsonAsync<JsonElement>($"/api/documents?sourceId={seed.Source}", Ct);
        Ids(all).ShouldBe([seed.Flagged, seed.Clean, seed.Held], ignoreOrder: true); // the soft-deleted one is not listed
        var flagged = await viewer.GetFromJsonAsync<JsonElement>($"/api/documents?sourceId={seed.Source}&pii=flagged", Ct);
        Ids(flagged).ShouldBe([seed.Flagged]);
        var summary = flagged.GetProperty("items")[0];
        (summary.GetProperty("piiFlagged").GetBoolean(), summary.GetProperty("live").GetBoolean(), summary.GetProperty("held").GetBoolean()).ShouldBe((true, true, false));
        Ids(await viewer.GetFromJsonAsync<JsonElement>($"/api/documents?sourceId={seed.Source}&q=avgift", Ct)).ShouldBe([seed.Clean]);
        (await viewer.GetAsync($"/api/documents?pii=maybe", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Keyset: one per page, three pages, then no next.
        var seen = new List<Guid>();
        string? next = null;
        do
        {
            var page = await viewer.GetFromJsonAsync<JsonElement>($"/api/documents?sourceId={seed.Source}&limit=1{(next is null ? "" : "&after=" + next)}", Ct);
            seen.AddRange(Ids(page));
            next = page.GetProperty("next").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next").GetString();
        }
        while (next is not null);
        seen.ShouldBe([seed.Flagged, seed.Clean, seed.Held], ignoreOrder: true);

        Ids(await stranger.GetFromJsonAsync<JsonElement>($"/api/documents?sourceId={seed.Source}", Ct)).ShouldBeEmpty();
        (await stranger.GetAsync($"/api/documents/{seed.Clean}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_document_shows_its_sets_pii_counts_and_run_items_and_its_chunks_need_edit_or_edit_pipelines()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        using var pipelines = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PipelineEditor, seed.Code);

        var flagged = await viewer.GetFromJsonAsync<JsonElement>($"/api/documents/{seed.Flagged}", Ct);
        var pii = flagged.GetProperty("livePii");
        pii.GetProperty("flagged").GetBoolean().ShouldBeTrue();
        pii.GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).ShouldBe([("email", 2), ("emailExternal", 1), ("phone", 1)], ignoreOrder: true);
        pii.GetProperty("typesFired").EnumerateArray().Select(t => t.GetString()).ShouldBe(["emailExternal"]); // #217: the stored fired types
        flagged.GetProperty("chunkSets").GetArrayLength().ShouldBe(1);
        flagged.GetProperty("chunkSets")[0].GetProperty("pii").GetProperty("typesFired").EnumerateArray().Select(t => t.GetString()).ShouldBe(["emailExternal"]);

        var clean = await viewer.GetFromJsonAsync<JsonElement>($"/api/documents/{seed.Clean}", Ct);
        clean.GetProperty("runItems").EnumerateArray().Select(i => (i.GetProperty("runId").GetGuid(), i.GetProperty("outcome").GetString()))
            .ShouldBe([(seed.Finished, "updated")]);
        (await viewer.GetAsync($"/api/documents/{seed.Deleted}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // #283: Edit or EditPipelines; an editor reads chunk text too, a viewer does not.
        (await editor.GetAsync($"/api/documents/{seed.Clean}/chunks", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var chunks = await pipelines.GetFromJsonAsync<JsonElement>($"/api/documents/{seed.Clean}/chunks", Ct);
        chunks.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe(seed.CleanChunks); // ordinal order
        chunks.GetProperty("chunks")[0].GetProperty("headingPath").GetString().ShouldBe("Taxor › Avfall");
        chunks.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Clearing_pii_needs_manage_a_reason_of_ten_and_a_flagged_document_and_enqueues_clear_pii_flag()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        var reason = new { reason = "Kontaktuppgifterna är publicerade avsiktligt." };

        (await editor.PostAsJsonAsync($"/api/documents/{seed.Flagged}/pii-clear", reason, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var short_ = await admin.PostAsJsonAsync($"/api/documents/{seed.Flagged}/pii-clear", new { reason = "  för kort " }, Ct))
        {
            short_.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var code = (await Json(short_)).GetProperty("errorCodes").GetProperty("reason")[0];
            (code.GetProperty("code").GetString(), code.GetProperty("params").GetProperty("min").GetString()).ShouldBe(("reason_too_short", "10"));
        }

        using (var clean = await admin.PostAsJsonAsync($"/api/documents/{seed.Clean}/pii-clear", reason, Ct))
        {
            clean.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(clean)).GetProperty("code").GetString().ShouldBe("pii_not_flagged");
        }

        using var cleared = await admin.PostAsJsonAsync($"/api/documents/{seed.Flagged}/pii-clear", reason, Ct);
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(Ct));
        var job = await JobAsync($"pii-clear:{seed.Flagged}");
        (await Json(cleared)).GetProperty("jobId").GetGuid().ShouldBe(job.Id);
        Valid<ClearPiiFlagCommand>(job);
        await using var db = TestPostgres.Context(database.Connection(null));
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == "document" && a.EntityId == seed.Flagged.ToString() && a.Action == "pii-clear", Ct);
        audit.Reason.ShouldBe("Kontaktuppgifterna är publicerade avsiktligt.");
        using var payload = JsonDocument.Parse(job.Payload);
        payload.RootElement.GetProperty("decidedBy").GetString().ShouldBe(audit.Actor);
    }

    [Fact]
    public async Task Reprocess_and_release_hold_enqueue_their_commands()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.Code);

        (await admin.PostAsJsonAsync($"/api/documents/{seed.Clean}/reprocess", new { fromStage = "cook" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/documents/{seed.Clean}/reprocess", new { fromStage = "chunk" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var reprocess = await JobAsync($"reprocess:{seed.Clean}");
        Valid<ReprocessDocumentCommand>(reprocess);
        Field(reprocess, "fromStage").ShouldBe("chunk");

        var held = await viewer.GetFromJsonAsync<JsonElement>($"/api/sources/{seed.Source}/held", Ct);
        held.GetProperty("items").EnumerateArray().Select(d => (d.GetProperty("id").GetGuid(), d.GetProperty("heldBy").GetString()))
            .ShouldBe([(seed.Held, "o-earlier")]);
        (await viewer.PostAsync($"/api/sources/{seed.Source}/held/release", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PostAsync($"/api/sources/{seed.Source}/held/release", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Valid<ReleaseHoldCommand>(await JobAsync($"release-hold:{seed.Source}"));
    }

    [Fact]
    public async Task Deletion_decisions_need_the_waiting_run_and_the_count_seen_and_the_csv_is_safe()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);

        var proposals = await editor.GetFromJsonAsync<JsonElement>($"/api/runs/{seed.Awaiting}/deletions", Ct);
        proposals.GetProperty("items").EnumerateArray().Select(p => (p.GetProperty("documentId").GetGuid(), p.GetProperty("reason").GetString()))
            .ShouldBe([(seed.Held, "not-found"), (seed.Clean, "gone")], ignoreOrder: true);
        // The configured guard for the card's text, readable by an editor (B-23; Settings itself is platform admin only).
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var settings = await db.Settings.AsNoTracking().SingleOrDefaultAsync(Ct) ?? new PlatformSettings();
            var guard = proposals.GetProperty("guard");
            (guard.GetProperty("percent").GetInt32(), guard.GetProperty("minCount").GetInt32(), guard.GetProperty("maxCount").GetInt32())
                .ShouldBe((settings.DeletionGuardPercent, settings.DeletionGuardMinCount, settings.DeletionGuardMaxCount));
        }

        var csv = await editor.GetStringAsync($"/api/runs/{seed.Awaiting}/deletions?format=csv", Ct);
        csv.ShouldStartWith("documentId,title,url,reason,exempt,decision,lastSeenAt\r\n");
        csv.ShouldContain("\"'=HYPERLINK(\"\"x\"\")\""); // quoted, and the formula prefixed with '
        csv.ShouldNotContain(",=HYPERLINK");

        (await editor.PostAsJsonAsync($"/api/runs/{seed.Awaiting}/deletions/confirm", new { proposedCount = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreach (var (run, count) in new[] { (seed.Awaiting, 3), (seed.Finished, 0) })
        {
            using var superseded = await admin.PostAsJsonAsync($"/api/runs/{run}/deletions/confirm", new { proposedCount = count }, Ct);
            superseded.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(superseded)).GetProperty("code").GetString().ShouldBe("deletion_superseded");
        }

        (await admin.PostAsJsonAsync($"/api/runs/{seed.Awaiting}/deletions/confirm", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var confirmed = await admin.PostAsJsonAsync($"/api/runs/{seed.Awaiting}/deletions/confirm", new { proposedCount = 2 }, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
        var job = await JobAsync($"deletion:{seed.Awaiting}");
        (job.Class, job.RunId).ShouldBe((JobClasses.Command, seed.Awaiting));
        Valid<ConfirmDeletionCommand>(job);
    }

    [Fact]
    public async Task A_rejected_deletion_enqueues_reject_deletion()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        (await admin.PostAsJsonAsync($"/api/runs/{seed.Awaiting}/deletions/reject", new { proposedCount = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var job = await JobAsync($"deletion:{seed.Awaiting}");
        Valid<RejectDeletionCommand>(job);
        Field(job, "kind").ShouldBe("reject-deletion");
    }
}
