using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// The flag lifecycle of processing design §9.5–§9.6 and §17, on the in-memory chunk-set store (the database-backed
/// index writer is item 9): a flagged set in a <c>public</c> collection is swapped in <c>held</c> and the old content
/// stops being served; clearing makes it <c>live</c> without re-processing; a clearance covers the next set at equal
/// counts and a higher count re-flags it; NER down gives <c>pending</c> and <c>held</c>, the sweep completes it;
/// non-public collections never block; event 5103 once per transition.
/// </summary>
public sealed class PiiLifecycleTests
{
    private static readonly Guid Document = Guid.CreateVersion7();
    private static readonly Guid Source = Guid.CreateVersion7();
    private static readonly Guid Collection = Guid.CreateVersion7();
    private static readonly Guid Profile = Guid.CreateVersion7();

    private static readonly string[] ListLike =
    [
        "Anna Lindqvist, 070-311 22 33", "Erik Johansson, 070-322 33 44", "Maria Nilsson, 070-333 44 55",
        "Johan Berglund, 070-344 55 66", "Sara Ek, 070-355 66 77",
    ];

    private readonly InMemoryChunkSetStore _store = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _clock = new(ProcessingHarness.Now);
    private readonly PiiLifecycle _lifecycle;
    private int _version;

    public PiiLifecycleTests()
    {
        _lifecycle = new PiiLifecycle(_store, LoggerFactory.Create(b => b.AddProvider(_logs)), _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PiiSwapContext Public => new(Classification.Public, Source, Guid.CreateVersion7(), PiiFlagRules.Default);

    private IEnumerable<LogEntry> Events => _logs.Entries.Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5103);

    /// <summary>A new set of the document from text, through the real personal-data stage, written invisible.</summary>
    private async Task<Guid> WriteAsync(string text, string collection = Classification.Public, PersonalDataStage? stage = null)
    {
        stage ??= new PersonalDataStage(new PiiPatterns(_clock), Ner(), new EmptyGazetteer(), _clock);
        var (chunks, assessment) = await stage.RunAsync(Draft(text), new PersonalDataContext(collection), Ct);
        var id = Guid.CreateVersion7();
        var set = new ChunkSetRecord(id, new ChunkSetKey(Document, Guid.CreateVersion7(), null, Profile, [(byte)++_version]), Classification.Public, Collection, [1])
        {
            PiiCounts = assessment.Counts,
            PiiFlagged = assessment.Flagged,
            CreatedAt = _clock.GetUtcNow(),
        };
        await _store.InsertWritingAsync(set, [.. chunks.Chunks.Select(c => new ChunkRecord(Guid.CreateVersion7(), id, Document, Collection, Classification.Public, c.Ordinal, c, Profile, new float[4]))], Ct);
        return id;
    }

    private static FakeNer Ner(bool ready = true)
    {
        var ner = new FakeNer { Ready = ready };
        foreach (var line in ListLike)
        {
            ner.Names.Add((line[..line.IndexOf(',', StringComparison.Ordinal)], "person", 0.95));
        }

        return ner;
    }

    private string Served => string.Join('|', _store.Searchable().Select(c => c.Draft.Text));

    [Fact]
    public async Task A_flagged_set_in_a_public_collection_is_held_and_the_old_content_stops_being_served()
    {
        await _lifecycle.SwapAsync(await WriteAsync("Öppettider vardagar 8–16."), Public, Ct);
        Served.ShouldBe("Öppettider vardagar 8–16.");

        var flagged = await WriteAsync("Sökande 19121212-1212 har ansökt.");
        (await _lifecycle.SwapAsync(flagged, Public, Ct)).ShouldBe(ChunkSetState.Held);

        Served.ShouldBeEmpty();
        (await _store.GetCurrentAsync(Document, Ct))!.Id.ShouldBe(flagged);
        Events.Count().ShouldBe(1);
        Events.Single().Message.ShouldContain("personnummer=1");
        Events.Single().Message.ShouldNotContain("19121212");
    }

    [Fact]
    public async Task Clearing_releases_the_held_set_without_re_processing()
    {
        var flagged = await WriteAsync("Sökande 19121212-1212 har ansökt.");
        await _lifecycle.SwapAsync(flagged, Public, Ct);
        var inserts = _store.ChunkInserts;

        (await _lifecycle.ClearAsync(Document, "anna.admin", Ct)).ShouldBeTrue();

        (await _store.GetAsync(flagged, Ct))!.State.ShouldBe(ChunkSetState.Live);
        Served.ShouldBe("Sökande 19121212-1212 har ansökt.");
        _store.ChunkInserts.ShouldBe(inserts);
        (await _store.GetClearanceAsync(Document, Ct))!.Counts.Personnummer.ShouldBe(1);
        (await _lifecycle.ClearAsync(Document, "anna.admin", Ct)).ShouldBeFalse(); // nothing held any more
    }

    [Fact]
    public async Task A_clearance_covers_the_next_set_at_equal_counts_and_a_higher_count_re_flags()
    {
        await _lifecycle.SwapAsync(await WriteAsync("Sökande 19121212-1212 har ansökt."), Public, Ct);
        await _lifecycle.ClearAsync(Document, "anna.admin", Ct);

        var equal = await WriteAsync("Sökande 19121212-1212 har ansökt igen.");
        (await _lifecycle.SwapAsync(equal, Public, Ct)).ShouldBe(ChunkSetState.Live);
        Events.Count().ShouldBe(1); // covered: no new transition

        var higher = await WriteAsync("Sökande 19121212-1212 och medsökande 20000101-0008.");
        (await _lifecycle.SwapAsync(higher, Public, Ct)).ShouldBe(ChunkSetState.Held);
        Events.Count().ShouldBe(2);
        Events.Last().Message.ShouldContain("reflag True");
        Served.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ner_down_holds_the_set_as_pending_and_the_sweep_completes_it()
    {
        await _lifecycle.SwapAsync(await WriteAsync("Öppettider vardagar 8–16."), Public, Ct);
        var down = Ner(ready: false);
        var stage = new PersonalDataStage(new PiiPatterns(_clock), down, new EmptyGazetteer(), _clock);
        var pending = await PendingAsync(stage, string.Join('\n', ListLike));
        var set = (await _store.GetAsync(pending, Ct))!;
        set.PiiCounts!.Ner.ShouldBe(NerStatus.Pending);

        (await _lifecycle.SwapAsync(pending, Public, Ct)).ShouldBe(ChunkSetState.Held);
        Served.ShouldBeEmpty();
        Events.ShouldBeEmpty(); // pending is not flagged

        // The sweep while NER is still down leaves it pending; once NER is ready the set settles (list-like: flagged, held, 5103).
        (await _lifecycle.SweepPendingAsync(stage, Context, 100, Ct)).ShouldBe(0);
        down.Ready = true;
        (await _lifecycle.SweepPendingAsync(stage, Context, 100, Ct)).ShouldBe(1);
        var settled = (await _store.GetAsync(pending, Ct))!;
        settled.PiiCounts!.Ner.ShouldBe(NerStatus.Done);
        settled.PiiFlagged.ShouldBeTrue();
        settled.State.ShouldBe(ChunkSetState.Held);
        Events.Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_pending_set_that_the_sweep_finds_clean_goes_live()
    {
        var down = Ner(ready: false);
        var stage = new PersonalDataStage(new PiiPatterns(_clock), down, new EmptyGazetteer(), _clock);
        var text = string.Join('\n', ListLike.Select(l => "Tel " + l[(l.IndexOf(',', StringComparison.Ordinal) + 2)..]));
        var pending = await PendingAsync(stage, text);
        (await _lifecycle.SwapAsync(pending, Public, Ct)).ShouldBe(ChunkSetState.Held);

        down.Ready = true;
        down.Names.Clear(); // no names: the list-like rule does not fire
        (await _lifecycle.SweepPendingAsync(stage, Context, 100, Ct)).ShouldBe(1);

        (await _store.GetAsync(pending, Ct))!.State.ShouldBe(ChunkSetState.Live);
        Served.ShouldBe(text);
        Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Classification.Internal)]
    [InlineData(Classification.Sensitive)]
    public async Task A_non_public_collection_never_blocks_or_waits(string level)
    {
        var flagged = await WriteAsync("Sökande 19121212-1212 har ansökt.", level, new PersonalDataStage(new PiiPatterns(_clock), Ner(ready: false), new EmptyGazetteer(), _clock));
        var context = Public with { CollectionClassification = level };

        (await _lifecycle.SwapAsync(flagged, context, Ct)).ShouldBe(ChunkSetState.Live);
        Served.ShouldBe("Sökande 19121212-1212 har ansökt.");
        (await _store.GetAsync(flagged, Ct))!.PiiCounts!.Ner.ShouldBe(NerStatus.Skipped); // names never awaited
        Events.Count().ShouldBe(1); // the flag is shown (5103) but nothing is blocked
    }

    /// <summary>A set written while NER has not been ready for more than five minutes: the stage completes as pending.</summary>
    private async Task<Guid> PendingAsync(PersonalDataStage stage, string text)
    {
        (await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(Draft(text), new PersonalDataContext(Classification.Public), Ct))).CountsAttempt.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromMinutes(6));
        return await WriteAsync(text, Classification.Public, stage);
    }

    private static ChunkSetDraft Draft(string text) => new()
    {
        Chunks = [new ChunkDraft { Ordinal = 0, Prefix = "Sida", HeadingPath = "Sida", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text), TokenCount = 10, DocumentType = "page" }],
    };

    private static Task<(PersonalDataContext Data, PiiSwapContext Swap)> Context(ChunkSetRecord set, CancellationToken cancellationToken) =>
        Task.FromResult((new PersonalDataContext(Classification.Public), Public));
}
