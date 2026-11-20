using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Security;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.TestKit;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Pii;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// The source setting <c>piiMode</c> (Decisions table, "Personal data on the municipality's own published site",
/// 2026-11-01) in a running worker: with <c>report</c> in a <c>public</c> collection, a page with an external e-mail address
/// and a phone number goes <c>live</c> and is served, flagged on its set, reported in its run item and by event 5103; a page
/// with a personnummer is still held and <c>clear-pii-flag</c> releases it; a NER-pending set goes live and the drift sweep
/// that later checks its names does not hold it.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class PiiModeEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly string[] ListLike =
    [
        "Anna Lindqvist, 070-311 22 33", "Erik Johansson, 070-322 33 44", "Maria Nilsson, 070-333 44 55",
        "Johan Berglund, 070-344 55 66", "Sara Ek, 070-355 66 77",
    ];

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-pii-mode-");
    private readonly CapturingLoggerProvider _logs = new();
    // Letters only (hex digits mapped to g–p): the marker can never read as a phone number or personnummer.
    private readonly string _marker = string.Concat(Guid.NewGuid().ToString("N").Select(c => char.IsAsciiDigit(c) ? (char)('g' + (c - '0')) : c));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Report_mode_serves_a_page_with_contact_details_flagged_and_still_holds_a_personnummer_until_cleared()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"kontakt {name} {_marker}", name switch
        {
            "contact" => "Frågor besvaras av anna.svensson@gmail.com eller på telefon 070-311 22 33.",
            "pnr" => "Sökande: Tolvan Tolvansson, personnummer 19121212-1212.",
            _ => string.Empty,
        }))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/contact"), site.Url("/p/pnr")], Classification.Public);
        await ReportAsync(seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var run = await LastRunAsync(seed);

            var contact = await DocumentAsync(seed, "/p/contact");
            (await StateAsync(contact)).ShouldBe(ChunkSetStates.Live, "report mode: e-mail and phone do not hold");
            (await ServedChunksAsync(contact)).ShouldBeGreaterThan(0, "the live set's chunks are served (DeletedAt null)");
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT s."PiiFlagged"::text || '|' || (s."PiiCounts" ->> 'emailExternal') || '|' || (s."PiiCounts" ->> 'phone')
                FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
                """, contact)).ShouldBe("true|1|1");
            (await ReasonAsync(run, "/p/contact")).ShouldBe(PiiLifecycle.FlaggedReason);
            FlaggedEvents(contact).ShouldHaveSingleItem().Message.ShouldContain("emailExternal=1");
            (await FlaggedAtAsync(contact)).ShouldBe("flagged at the swap", "PiiFlaggedAt is the swap's time (#175), the set's LiveAt here");

            var pnr = await DocumentAsync(seed, "/p/pnr");
            (await StateAsync(pnr)).ShouldBe(ChunkSetStates.Held, "a personnummer holds in report mode too");
            (await ServedChunksAsync(pnr)).ShouldBe(0);
            (await ReasonAsync(run, "/p/pnr")).ShouldBe(PiiLifecycle.FlaggedReason);
            FlaggedEvents(pnr).ShouldHaveSingleItem().Message.ShouldContain("personnummer=1");
            (await FlaggedAtAsync(pnr)).ShouldBe("flagged", "a held set has no LiveAt yet");

            var clear = new JobRequest(JobClasses.Command, seed.DepartmentId, seed.Classification,
                ContractJson.Serialize(new ClearPiiFlagCommand { DocumentId = pnr, DecidedBy = "admin-oid" }));
            (await database.Queue(RagDatabase.Admin).EnqueueAsync([clear], null, Ct)).ShouldContain(clear.Id);
            await Commands.WaitForStateAsync(database, clear.Id, JobStates.Succeeded);

            (await StateAsync(pnr)).ShouldBe(ChunkSetStates.Live);
            (await ServedChunksAsync(pnr)).ShouldBeGreaterThan(0, "clear-pii-flag releases the held set");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Report_mode_puts_a_ner_pending_set_live_and_the_drift_sweep_does_not_hold_it_later()
    {
        // NER has not been ready for more than five minutes: the stage completes with patterns only and ner pending (§9.6).
        var ner = new FakeNer { Ready = false };
        foreach (var line in ListLike)
        {
            ner.Names.Add((line[..line.IndexOf(',', StringComparison.Ordinal)], "person", 0.95));
        }

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"kontakter {name} {_marker}",
            string.Join("<br>", ListLike)))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/list")], Classification.Public);
        await ReportAsync(seed);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs, services: services =>
            services.AddSingleton(sp => new PersonalDataStage(sp.GetRequiredService<PiiPatterns>(), ner, sp.GetRequiredService<IFirstNameGazetteer>(), clock)));
        try
        {
            var stage = worker.Services.GetRequiredService<PersonalDataStage>();
            await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(Draft(string.Join('\n', ListLike)),
                new PersonalDataContext(Classification.Public), Ct));
            clock.Advance(TimeSpan.FromMinutes(6));

            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            var document = await DocumentAsync(seed, "/p/list");
            (await NerAsync(document)).ShouldBe("pending");
            (await StateAsync(document)).ShouldBe(ChunkSetStates.Live, "report mode does not hold for unchecked names");
            (await ServedChunksAsync(document)).ShouldBeGreaterThan(0);
            (await ReasonAsync(await LastRunAsync(seed), "/p/list")).ShouldBeNull();

            var sweep = worker.Services.GetRequiredService<DriftSweep>();
            (await sweep.RunAsync(Ct)).PendingSettled.ShouldBe(0, "NER still down: the set stays pending");
            (await StateAsync(document)).ShouldBe(ChunkSetStates.Live);
            (await FlaggedAtAsync(document)).ShouldBe("never flagged");

            ner.Ready = true;
            (await sweep.RunAsync(Ct)).PendingSettled.ShouldBe(1);

            (await NerAsync(document)).ShouldBe("done");
            (await Sql.ScalarAsync<bool>(database.Connection(null), """
                SELECT s."PiiFlagged" FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
                """, document)).ShouldBeTrue("five names and five phones: the list-like rule fires");
            (await StateAsync(document)).ShouldBe(ChunkSetStates.Live, "names never hold in report mode, not even after the sweep");
            (await ServedChunksAsync(document)).ShouldBeGreaterThan(0);
            FlaggedEvents(document).ShouldHaveSingleItem().Message.ShouldContain("name=5");
            (await FlaggedAtAsync(document)).ShouldBe("flagged at the swap", "the sweep stamps PiiFlaggedAt with the time of its state change");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>Sets <c>piiMode: report</c> on the source, as the admin's settings form would.</summary>
    private Task ReportAsync(SourceSeed seed) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = "Settings" || '{"piiMode":"report"}'::jsonb WHERE "Id" = $1""",
            seed.SourceId);

    private IEnumerable<LogEntry> FlaggedEvents(Guid document) =>
        _logs.Entries.Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5103 && e.Message.Contains(document.ToString(), StringComparison.Ordinal));

    private Task<Guid> DocumentAsync(SourceSeed seed, string path) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "Url" LIKE '%' || $2""",
            seed.SourceId, path);

    private Task<string> StateAsync(Guid document) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT s."State" FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
            """, document);

    /// <summary>The live set's <c>PiiFlaggedAt</c>: never flagged, flagged at the swap (equal to <c>LiveAt</c>) or flagged.</summary>
    private Task<string> FlaggedAtAsync(Guid document) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT CASE WHEN s."PiiFlaggedAt" IS NULL THEN 'never flagged' WHEN s."PiiFlaggedAt" = s."LiveAt" THEN 'flagged at the swap' ELSE 'flagged' END
            FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
            """, document);

    private Task<string> NerAsync(Guid document) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT s."PiiCounts" ->> 'ner' FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
            """, document);

    /// <summary>The document's searchable chunks: in its current set, not deleted.</summary>
    private Task<long> ServedChunksAsync(Guid document) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
            WHERE d."Id" = $1 AND c."DeletedAt" IS NULL
            """, document);

    private Task<string?> ReasonAsync(Guid runId, string path) =>
        Sql.ScalarAsync<string?>(database.Connection(null), """
            SELECT "Reason" FROM content."RunItems" WHERE "RunId" = $1 AND "Identity" LIKE '%' || $2 AND "Outcome" = 'added'
            """, runId, path);

    private Task<Guid> LastRunAsync(SourceSeed seed) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Runs" WHERE "SourceId" = $1 ORDER BY "StartedAt" DESC LIMIT 1""", seed.SourceId);

    private static ChunkSetDraft Draft(string text) => new()
    {
        Chunks = [new ChunkDraft { Ordinal = 0, Prefix = "Sida", HeadingPath = "Sida", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text), TokenCount = 10, DocumentType = "page" }],
    };
}
