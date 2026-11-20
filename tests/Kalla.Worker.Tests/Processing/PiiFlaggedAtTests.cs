using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain;
using Kalla.Infrastructure.Security;
using Kalla.Pipelines.Processing;
using Kalla.TestKit;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Processing.Pii;
using Kalla.Worker.Tests.CommandJobs;
using Kalla.Worker.Tests.Engine;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// <c>ChunkSets."PiiFlaggedAt"</c> (#175 part 4; processing design §9.5) on the test database through
/// <see cref="PiiLifecycle"/> and <see cref="PostgresChunkSetStore"/>: a flagged set gets the time of its event 5103, a
/// re-flag of the same set (swapped back after a clean set) raises the event again and keeps the first time, and a clean set
/// stays null. The engine's fenced swap and the pending sweep are covered in <see cref="PiiModeEngineTests"/>.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class PiiFlaggedAtTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _clock = new(Start);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_flagged_set_gets_its_flag_time_a_re_flag_keeps_the_first_and_a_clean_set_stays_null()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var store = new PostgresChunkSetStore(database.Database(RagDatabase.Worker));
            var lifecycle = new PiiLifecycle(store, LoggerFactory.Create(b => b.AddProvider(_logs)), _clock);
            var context = new PiiSwapContext(Classification.Public, seed.SourceId, null, PiiFlagRules.Default);

            var flagged = await WriteAsync(seed, document, new PiiCounts { Personnummer = 1 });
            (await lifecycle.SwapAsync(flagged, context, Ct)).ShouldBe(ChunkSetState.Held);
            (await FlaggedAtAsync(flagged)).ShouldBe(Start, "the time of the swap that raised event 5103");
            Events(flagged).ShouldBe(1);

            _clock.Advance(TimeSpan.FromHours(1));
            var clean = await WriteAsync(seed, document, new PiiCounts());
            (await lifecycle.SwapAsync(clean, context, Ct)).ShouldBe(ChunkSetState.Live);
            (await FlaggedAtAsync(clean)).ShouldBeNull("a set never flagged has no flag time");

            _clock.Advance(TimeSpan.FromHours(1));
            (await lifecycle.SwapAsync(flagged, context, Ct)).ShouldBe(ChunkSetState.Held, "swapped back: flagged again");
            Events(flagged).ShouldBe(2, "a new transition to flagged raises 5103 again");
            (await FlaggedAtAsync(flagged)).ShouldBe(Start, "a re-flag keeps the first flag time");
            (await store.GetAsync(flagged, Ct))!.PiiFlaggedAt.ShouldBe(Start);
            (await store.GetAsync(clean, Ct))!.PiiFlaggedAt.ShouldBeNull();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private int Events(Guid chunkSetId) =>
        _logs.Entries.Count(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5103 && e.Message.Contains(chunkSetId.ToString(), StringComparison.Ordinal));

    private async Task<DateTimeOffset?> FlaggedAtAsync(Guid chunkSetId)
    {
        var value = await Sql.ScalarAsync<DateTime?>(database.Connection(null), """SELECT "PiiFlaggedAt" FROM content."ChunkSets" WHERE "Id" = $1""", chunkSetId);
        return value is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null;
    }

    /// <summary>A <c>writing</c> set of the document with one chunk and <paramref name="counts"/> (flagged with a personnummer).</summary>
    private async Task<Guid> WriteAsync(SourceSeed seed, Guid document, PiiCounts counts)
    {
        var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, RandomNumberGenerator.GetBytes(32)),
            seed.Classification, seed.CollectionId, RandomNumberGenerator.GetBytes(32))
        {
            ChunkCount = 1,
            PiiCounts = counts,
            PiiFlagged = counts.Personnummer > 0,
        };
        var text = $"Text {Guid.NewGuid():N}";
        var chunk = new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, 0, new ChunkDraft
        {
            Ordinal = 0, Prefix = "x", HeadingPath = "x", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text),
            TokenCount = 4, DocumentType = "page",
        }, EngineKit.ProfileId, new float[EngineKit.Dimensions]) { DeletedAt = _clock.GetUtcNow() };
        await new IndexWriter(database.Database(RagDatabase.Worker)).WriteInvisibleAsync(set, [chunk], Ct);
        return set.Id;
    }
}
