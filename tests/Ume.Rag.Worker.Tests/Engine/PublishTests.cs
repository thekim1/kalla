using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Preview;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// P1b item 6's done-when (P1b README §3; engine design §12, §14, §19): <c>publish-plan</c> lists every live document of the
/// pipeline's sources with a forecast; a publish's <c>reprocess</c> swaps per document, so live answers use the old set of
/// a document until its own swap; superseded sets are kept 14 days and a rollback within them swaps them back with no
/// <c>parse</c> or <c>embed</c> job and no gateway call; a set past 14 days is purged (and that document re-runs from the
/// caches); each finished publish or rollback run enqueues one <c>eval</c> <c>publish-report</c>.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class PublishTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    // The eval class stays off, so the publish reports stay queued where the tests can count them.
    private static readonly Dictionary<string, string?> NoEval = new() { ["Worker:Classes"] = "preview,command,sync,fetch,parse,embed" };

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-publish-blobs-");

    // Unique per test run: the embedding cache and the database are shared.
    private readonly string _tag = Guid.NewGuid().ToString("N");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Publish_plan_lists_every_live_document_of_the_pipelines_sources_with_a_forecast()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: NoEval);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var draft = await DraftAsync(seed, """{"chunkTokens":200}""");
            var tokens = await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT sum(c."TokenCount") FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" WHERE d."SourceId" = $1
                """, seed.SourceId);

            var plan = await ResultAsync<PublishPlanResult>(await EnqueuePreviewAsync(seed,
                new PublishPlanPayload { PipelineId = seed.PipelineId, PipelineVersionId = draft }));

            plan.PipelineVersionId.ShouldBe(draft);
            plan.Documents.ShouldBe(2);
            plan.Sources.ShouldHaveSingleItem().ShouldBe(new PublishPlanSource { SourceId = seed.SourceId, Documents = 2 });
            plan.FromStage.ShouldBe("parse");
            plan.ParserPages.ShouldBe(0, "HTML pages need no parser");
            plan.EmbeddingTokens.ShouldBe((int)tokens);
            plan.EstimatedMinutes.ShouldBe((int)Math.Ceiling((2 * PublishPlan.SecondsPerDocument + tokens / PublishPlan.EmbeddingTokensPerSecond) / 60));
            plan.EstimatedCostUsd.ShouldBe(0m, "the worker has no price source");

            var foreign = await EnqueuePreviewAsync(seed, new PublishPlanPayload { PipelineId = Guid.CreateVersion7(), PipelineVersionId = draft });
            (await TerminalAsync(foreign)).ShouldBe((JobStates.Failed, PublishPlan.VersionUnknown));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_publish_swaps_per_document_so_live_answers_keep_the_old_set_until_each_swap_and_enqueues_one_publish_report()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        var gate = new EmbeddingGate();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, gate, settings: NoEval);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var before = await LiveSetsAsync(seed);
            var (a, b) = (site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri);
            var v2 = await EngineKit.PublishAsync(database, seed, "url-list", """{"chunkTokens":200}""");
            gate.Only = Marker;
            gate.Arm();

            var runId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish);
            await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var waited = Stopwatch.StartNew();
            while ((await LiveSetsAsync(seed))[a].Version != v2)
            {
                waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "the first document's swap");
                await Task.Delay(100, Ct);
            }

            // Mid-run: the first document serves its new set, the second (its embed held at the gate) still its old one.
            var mid = await LiveSetsAsync(seed);
            (await EngineKit.RunAsync(database, runId)).ShouldNotBeNull().State.ShouldBe(RunStates.Running);
            mid[a].Set.ShouldNotBe(before[a].Set);
            (await SearchableSetsAsync(a)).ShouldBe([mid[a].Set]);
            mid[b].ShouldBe(before[b]);
            (await SearchableSetsAsync(b)).ShouldBe([before[b].Set], "the old set answers until the document's own swap");
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);

            gate.Open();
            (await EngineKit.WaitForRunAsync(database, runId)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 2, 0, 0, 0, 0));
            var after = await LiveSetsAsync(seed);
            after.Values.Select(s => s.Version).Distinct().ShouldBe([v2]);
            (await SearchableSetsAsync(b)).ShouldBe([after[b].Set]);
            (await StateAsync(before[a].Set)).ShouldBe(ChunkSetStates.Superseded);
            (await StateAsync(before[b].Set)).ShouldBe(ChunkSetStates.Superseded);
            await Task.Delay(TimeSpan.FromSeconds(2.5), Ct); // two more finalizer passes
            (await PublishReportsAsync(runId)).ShouldBe([new ReportJob(JobStates.Queued, seed.CollectionId, v2)]);
        }
        finally
        {
            gate.Open();
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_rollback_within_14_days_swaps_the_kept_sets_back_without_parse_embed_or_a_gateway_call()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        var gate = new EmbeddingGate();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, gate, settings: NoEval);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var v1Sets = await LiveSetsAsync(seed);
            var v2 = await EngineKit.PublishAsync(database, seed, "url-list", """{"chunkTokens":200}""");
            var publishId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish);
            (await EngineKit.WaitForRunAsync(database, publishId)).Updated.ShouldBe(2);
            var v2Sets = await LiveSetsAsync(seed);
            // The nightly sweep 13 days after the publish keeps the superseded sets.
            await SweepAsync(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(13)));
            await RollBackAsync(seed, seed.VersionId, v2);
            var requests = gate.Requests;

            var rollbackId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Rollback);
            var run = await EngineKit.WaitForRunAsync(database, rollbackId);

            (await ContentJobsAsync(rollbackId)).ShouldBe(0, "a rollback within 14 days parses and embeds nothing");
            gate.Requests.ShouldBe(requests, "a rollback within 14 days makes no gateway call");
            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 2, 0, 0, 0, 0));
            (await LiveSetsAsync(seed)).ShouldBe(v1Sets, "the kept sets are live again");
            foreach (var (url, set) in v1Sets)
            {
                (await SearchableSetsAsync(url)).ShouldBe([set.Set]);
                (await StateAsync(v2Sets[url].Set)).ShouldBe(ChunkSetStates.Superseded);
            }

            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."RunItems" WHERE "RunId" = $1 AND "Outcome" = 'updated' AND "Reason" = $2
                """, rollbackId, ChunkSetSwap.SwappedBackReason)).ShouldBe(2);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await PublishReportsAsync(publishId)).ShouldBe([new ReportJob(JobStates.Queued, seed.CollectionId, v2)]);
            (await PublishReportsAsync(rollbackId)).ShouldBe([new ReportJob(JobStates.Queued, seed.CollectionId, seed.VersionId)]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_superseded_set_is_purged_after_14_days_and_kept_before_and_the_purged_document_rebuilds_from_the_caches()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: NoEval);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var v1Sets = await LiveSetsAsync(seed);
            var (a, b) = (site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri);
            var v2 = await EngineKit.PublishAsync(database, seed, "url-list", """{"chunkTokens":200}""");
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish))).Updated.ShouldBe(2);
            var supersededAt = new DateTimeOffset(await Sql.ScalarAsync<DateTime>(database.Connection(null),
                """SELECT "SupersededAt" FROM content."ChunkSets" WHERE "Id" = $1""", v1Sets[a].Set), TimeSpan.Zero);
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."ChunkSets" SET "SupersededAt" = $2 WHERE "Id" = $1""",
                v1Sets[b].Set, supersededAt.AddDays(2));
            var clock = new FakeTimeProvider(supersededAt);
            clock.Advance(TimeSpan.FromDays(15));

            await SweepAsync(clock);

            (await StateAsync(v1Sets[a].Set)).ShouldBeNull("15 days after it was superseded the set is purged");
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1""", v1Sets[a].Set))
                .ShouldBe(0);
            (await StateAsync(v1Sets[b].Set)).ShouldBe(ChunkSetStates.Superseded, "13 days after it was superseded the set is kept");

            await RollBackAsync(seed, seed.VersionId, v2);
            var rollbackId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Rollback);

            (await EngineKit.WaitForRunAsync(database, rollbackId)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 2, 0, 0, 0, 0));
            var live = await LiveSetsAsync(seed);
            live[b].ShouldBe(v1Sets[b], "the kept set is swapped back");
            live[a].Version.ShouldBe(seed.VersionId);
            live[a].Set.ShouldNotBe(v1Sets[a].Set, "the purged set is rebuilt from the cached stage outputs");
            (await ContentJobsAsync(rollbackId, b)).ShouldBe(0);
            (await ContentJobsAsync(rollbackId, a)).ShouldBeGreaterThan(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private sealed record LiveSet(Guid Set, Guid Version);

    private sealed record ReportJob(string State, Guid CollectionId, Guid PipelineVersionId);

    /// <summary>The second page's text: an armed gate with <see cref="EmbeddingGate.Only"/> holds only its embeddings.</summary>
    private string Marker => "zz" + _tag + "gate";

    /// <summary>Two pages long enough that a smaller <c>chunkTokens</c> gives new chunks; every sentence carries the run's tag.</summary>
    private Task<TestSite> SiteAsync() => EngineKit.SiteAsync((name, _) =>
    {
        var text = string.Concat(Enumerable.Repeat(
            $"Bygglov {_tag} krävs för nya byggnader, tillbyggnader och vissa ändringar av fasaden. Handläggaren granskar ritningar och situationsplan. ", 40));
        return Task.FromResult(EngineKit.Html(EngineKit.Page($"bygglov {name} {_tag}", text + (name == "b" ? " " + Marker : string.Empty))));
    });

    private Task<long> SweepAsync(TimeProvider clock) => new IndexWriter(database.Database(RagDatabase.Worker)).SweepOrphansAsync(clock.GetUtcNow(), Ct);

    /// <summary>What the admin's rollback does (engine design §12.3): the previous version published again, the current one superseded.</summary>
    private async Task RollBackAsync(SourceSeed seed, Guid to, Guid from)
    {
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."PipelineVersions" SET "State" = 'superseded' WHERE "Id" = $1""", from);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."PipelineVersions" SET "State" = 'published', "PublishedAt" = now() WHERE "Id" = $1""", to);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Pipelines" SET "PublishedVersionId" = $1 WHERE "Id" = $2""", to, seed.PipelineId);
    }

    private async Task<Guid> DraftAsync(SourceSeed seed, string parameters)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."PipelineVersions" ("Id", "PipelineId", "Number", "State", "Definition")
            VALUES ($1, $2, (SELECT coalesce(max("Number"), 0) + 1 FROM config."PipelineVersions" WHERE "PipelineId" = $2), 'draft', $3::jsonb)
            """, id, seed.PipelineId, """{"schema":"pipeline/v2","template":"url-list","templateVersion":1,"params":""" + parameters + "}");
        return id;
    }

    private async Task<Dictionary<string, LiveSet>> LiveSetsAsync(SourceSeed seed)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT d."CanonicalIdentity", s."Id", s."PipelineVersionId" FROM content."Documents" d JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId"
            WHERE d."SourceId" = $1 AND d."DeletedAt" IS NULL
            ORDER BY d."CanonicalIdentity"
            """, connection) { Parameters = { new NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var sets = new Dictionary<string, LiveSet>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            sets[reader.GetString(0)] = new LiveSet(reader.GetGuid(1), reader.GetGuid(2));
        }

        return sets;
    }

    /// <summary>The sets whose chunks a search of the document can return (<c>DeletedAt</c> null).</summary>
    private async Task<List<Guid>> SearchableSetsAsync(string identity)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT c."ChunkSetId" FROM content."Chunks" c JOIN content."Documents" d ON d."Id" = c."DocumentId"
            WHERE d."CanonicalIdentity" = $1 AND c."DeletedAt" IS NULL
            """, connection) { Parameters = { new NpgsqlParameter { Value = identity } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var sets = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            sets.Add(reader.GetGuid(0));
        }

        return sets;
    }

    private async Task<string?> StateAsync(Guid chunkSetId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "State" FROM content."ChunkSets" WHERE "Id" = $1""", connection)
        {
            Parameters = { new NpgsqlParameter { Value = chunkSetId } },
        };
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    /// <summary>The run's <c>parse</c> and <c>embed</c> jobs, of one document when <paramref name="identity"/> is given.</summary>
    private Task<long> ContentJobsAsync(Guid runId, string? identity = null) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Jobs" j
            WHERE j."RunId" = $1 AND j."Class" IN ('parse', 'embed')
              AND ($2::text IS NULL OR (j."Payload" ->> 'documentId')::uuid = (SELECT "Id" FROM content."Documents" WHERE "CanonicalIdentity" = $2))
            """, runId, (object?)identity ?? DBNull.Value);

    private async Task<List<ReportJob>> PublishReportsAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "State", "Payload"::text, "DedupKey" FROM content."Jobs" WHERE "Class" = 'eval' AND "Payload" ->> 'runId' = $1
            """, connection) { Parameters = { new NpgsqlParameter { Value = runId.ToString("D") } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var jobs = new List<ReportJob>();
        while (await reader.ReadAsync(Ct))
        {
            reader.GetString(2).ShouldBe(JobDedupKeys.EvalReport(runId));
            using var json = JsonDocument.Parse(reader.GetString(1));
            var payload = ContractJson.Deserialize<EvalPublishReportPayload>(json.RootElement);
            payload.RunId.ShouldBe(runId);
            jobs.Add(new ReportJob(reader.GetString(0), payload.CollectionId, payload.PipelineVersionId));
        }

        return jobs;
    }

    /// <summary>A preview job as the admin enqueues it.</summary>
    private async Task<Guid> EnqueuePreviewAsync<T>(SourceSeed seed, T payload)
    {
        var job = new JobRequest(JobClasses.Preview, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload));
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        return job.Id;
    }

    private async Task<(string State, string? LastError)> TerminalAsync(Guid jobId)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var row = await Sql.JobAsync(database.Connection(null), jobId);
            if (row.State is JobStates.Succeeded or JobStates.Failed or JobStates.Dead)
            {
                return (row.State, row.LastError);
            }

            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), $"preview job {jobId} is still {row.State}");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<T> ResultAsync<T>(Guid jobId)
    {
        (await TerminalAsync(jobId)).ShouldBe((JobStates.Succeeded, (string?)null));
        var row = await Sql.JobAsync(database.Connection(null), jobId);
        using var json = JsonDocument.Parse(row.Result!);
        return ContractJson.Deserialize<T>(json.RootElement);
    }
}
