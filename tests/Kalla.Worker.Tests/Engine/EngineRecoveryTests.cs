using Kalla.Domain.Entities;
using Kalla.TestKit;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// Crash recovery (engine design §19, §9.4, §16): "worker restart loses no work". A worker is killed without a clean stop
/// (its jobs stay leased, its instance row stays) during a fetch and during an embedding call; a second worker reclaims the
/// jobs through the reaper and the run ends with the same state as an undisturbed one: every document live once, no
/// duplicate sets, no stray searchable chunks, exact counters, and the reclaimed job's attempt not consumed. A set left
/// <c>writing</c> by a crash between its batches and its swap is replaced by the retry and never searchable; and a failed
/// fence keeps the old set live.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class EngineRecoveryTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-engine-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_worker_killed_during_a_fetch_loses_no_work()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var site = await EngineKit.SiteAsync(async (name, context) =>
        {
            if (name == "slow" && !release.Task.IsCompleted)
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }

            return EngineKit.Html(EngineKit.Page("skola " + name));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/slow"), site.Url("/p/c")]);
        try
        {
            var first = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            var runId = await EngineKit.SyncNowAsync(database, seed);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var slowJob = await HeldFetchAsync(runId);
            await first.KillAsync();
            release.TrySetResult();

            await using var second = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            await ShouldBeCompleteAsync(seed, run, documents: 3);
            var job = await Sql.JobAsync(database.Connection(null), slowJob);
            job.State.ShouldBe(JobStates.Succeeded);
            job.Reclaims.ShouldBe((short)1);
            job.Attempts.ShouldBe((short)1, "the reclaim gave back the attempt the kill used");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_worker_killed_during_embedding_loses_no_work()
    {
        // Text of its own: a page another test embedded is an embedding-cache hit, and the gate would never see a call.
        var tag = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"äldreomsorg {name} {tag}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        try
        {
            var gate = new EmbeddingGate();
            gate.Arm();
            await using var first = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, gate);
            var runId = await EngineKit.SyncNowAsync(database, seed);
            await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await first.KillAsync();
            gate.Open();

            await using var second = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            await ShouldBeCompleteAsync(seed, run, documents: 2);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'embed' AND "Reclaims" > 0 AND "State" = 'succeeded'
                """, runId)).ShouldBeGreaterThan(0, "an embed job was reclaimed from the killed worker");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_set_left_writing_by_a_crash_is_never_searchable_and_the_retry_replaces_it()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("parkering " + name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        try
        {
            await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs))
            {
                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            }

            // A new pipeline version is published; an attempt for it died between its chunk batches and its swap.
            var document = await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);
            var newVersion = await PublishNewVersionAsync(seed);
            var orphan = await WriteOrphanAsync(document, newVersion);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0, "a writing set's chunks are invisible");

            await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs))
            {
                var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));
                run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            }

            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ChunkSets" WHERE "Id" = $1""", orphan)).ShouldBe(0);
            (await Sql.ScalarAsync<Guid>(database.Connection(null), """
                SELECT s."PipelineVersionId" FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
                """, document)).ShouldBe(newVersion);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_swap_whose_fence_fails_rolls_back_and_the_old_set_stays_live()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("bibliotek " + name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        try
        {
            await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs))
            {
                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            }

            var document = await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);
            var live = await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1""", document);
            var liveChunks = await EngineKit.LiveChunksAsync(database, seed);
            var candidate = await WriteOrphanAsync(document, seed.VersionId, contentHash: new byte[32]);
            var hash = await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", document);

            await using (var commit = await EngineCommit.BeginAsync(database.Database(RagDatabase.Worker), Ct))
            {
                // A superseded execution: the fence finds no row, so the swap is never applied and nothing commits.
                (await commit.FenceAsync(document, seed.Classification, Guid.CreateVersion7(), hash, Ct)).ShouldBeFalse();
            }

            (await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1""", document)).ShouldBe(live);
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBe(liveChunks);
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."ChunkSets" WHERE "Id" = $1""", candidate)).ShouldBe(ChunkSetStates.Writing);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private async Task ShouldBeCompleteAsync(SourceSeed seed, RunCounters run, int documents)
    {
        run.ShouldBe(new RunCounters(RunStates.Succeeded, documents, 0, 0, 0, 0, 0));
        (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(documents);
        (await EngineKit.DocumentsAsync(database, seed, live: false)).ShouldBe(documents, "no duplicate documents");
        (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
        (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(documents);
    }

    /// <summary>
    /// The fetch job held at the site: the one still running once the run's two other fetches have succeeded (until then a
    /// running fetch may be one of those, finishing).
    /// </summary>
    private async Task<Guid> HeldFetchAsync(Guid runId)
    {
        await Commands.WaitUntilAsync(async () => await Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'fetch' AND "State" = 'succeeded'
            """, runId) == 2);
        return await Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'fetch' AND "State" = 'running'
            """, runId);
    }

    private async Task<Guid> PublishNewVersionAsync(SourceSeed seed)
    {
        var version = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."PipelineVersions" ("Id", "PipelineId", "Number", "State", "Definition", "PublishedAt")
            VALUES ($1, $2, 2, 'published', '{"schema":"pipeline/v2","template":"url-list","templateVersion":1,"params":{"chunkTokens":300}}', now())
            """, version, seed.PipelineId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."PipelineVersions" SET "State" = 'superseded' WHERE "Id" = $1""", seed.VersionId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Pipelines" SET "PublishedVersionId" = $1 WHERE "Id" = $2""", version, seed.PipelineId);
        return version;
    }

    /// <summary>A <c>writing</c> set with one invisible chunk, as an attempt that died before its swap leaves it.</summary>
    private async Task<Guid> WriteOrphanAsync(Guid document, Guid version, byte[]? contentHash = null)
    {
        var writer = new IndexWriter(database.Database(RagDatabase.Worker));
        var hash = contentHash ?? await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", document);
        var collection = await Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT s."CollectionId" FROM config."Sources" s JOIN content."Documents" d ON d."SourceId" = s."Id" WHERE d."Id" = $1
            """, document);
        var level = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Classification" FROM content."Documents" WHERE "Id" = $1""", document);
        var key = new ChunkSetKey(document, version, null, EngineKit.ProfileId, hash);
        var set = new ChunkSetRecord(Guid.CreateVersion7(), key, level, collection, new byte[32]) { ChunkCount = 1 };
        var draft = new Worker.Processing.ChunkDraft
        {
            Ordinal = 0, Prefix = "x", HeadingPath = "x", Text = "Halvskriven text.", SearchText = "Halvskriven text.", OverlapChars = 0,
            TextHash = Convert.ToHexStringLower(new byte[32]), TokenCount = 3, DocumentType = "page",
        };
        await writer.WriteInvisibleAsync(set, [new ChunkRecord(Guid.CreateVersion7(), set.Id, document, collection, level, 0, draft, EngineKit.ProfileId,
            new float[EngineKit.Dimensions]) { DeletedAt = DateTimeOffset.UtcNow }], Ct);
        return set.Id;
    }
}
