using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// The nightly drift sweep (engine design §2.3, §16, §19 "worker restart loses no work"): a document whose job was lost
/// after a crash (no live set, no open job) is re-enqueued and ends live; with everything current the sweep enqueues
/// nothing and reports drift 0; after a publish it re-enqueues each drifted document once, and a second pair of concurrent
/// sweeps while those jobs are queued enqueues nothing more (the dedup key holds); a drifted document with a kept set of the
/// wanted key is swapped back with no job, as a <c>reprocess</c> run does (§12.3, §14 rule 1).
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class DriftSweepTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-drift-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_document_whose_job_was_lost_is_re_enqueued_by_the_sweep_and_ends_live()
    {
        // Text of its own: a page another test embedded is an embedding-cache hit, and the gate would never see a call.
        var tag = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"bygglov {name} {tag}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        try
        {
            var gate = new EmbeddingGate();
            gate.Arm();
            await using var first = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, gate);
            var runId = await EngineKit.SyncNowAsync(database, seed);
            await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await first.KillAsync();
            // The crash lost the job: its row is gone, so no reaper brings it back.
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'embed'""", runId);
            gate.Open();

            await using var second = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            await EngineKit.WaitForRunAsync(database, runId);
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(0, "the lost job left the document without a live set");

            var sweep = second.Services.GetRequiredService<DriftSweep>();
            (await sweep.RunAsync(Ct)).Enqueued.ShouldBe(1);
            var reprocess = await SweepRunAsync(seed);
            (await EngineKit.WaitForRunAsync(database, reprocess)).ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));

            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(1);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await sweep.RunAsync(Ct)).Enqueued.ShouldBe(0, "drift 0 once the document is current");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_current_index_has_no_drift_and_concurrent_sweeps_never_enqueue_a_document_twice()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("förskola " + name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        try
        {
            await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs))
            {
                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
                var current = await worker.Services.GetRequiredService<DriftSweep>().RunAsync(Ct);
                current.Enqueued.ShouldBe(0, "everything is current: drift 0");
                current.Checked.ShouldBeGreaterThanOrEqualTo(2);
            }

            var version = await PublishNewVersionAsync(seed);

            // A worker that runs no parse or embed jobs, so the sweep's jobs stay queued while the next sweeps run.
            await using (var idle = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs,
                settings: new Dictionary<string, string?> { ["Worker:Classes"] = JobClasses.Command }))
            {
                var sweep = idle.Services.GetRequiredService<DriftSweep>();
                (await sweep.RunAsync(Ct)).Enqueued.ShouldBe(2, "both documents drifted to the new version");
                var again = await Task.WhenAll(sweep.RunAsync(Ct), sweep.RunAsync(Ct));
                again.Sum(r => r.Enqueued).ShouldBe(0, "the queued jobs hold their dedup keys");
                (await OpenParseJobsAsync(seed)).ShouldBe(2);
            }

            await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs))
            {
                // The short pages chunk alike under the new version, so both live sets are re-keyed (unchanged, version.unaffected).
                (await EngineKit.WaitForRunAsync(database, await SweepRunAsync(seed))).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 2, 0, 0, 0));
                (await Sql.ScalarAsync<long>(database.Connection(null), """
                    SELECT count(*) FROM content."Documents" d JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId"
                    WHERE d."SourceId" = $1 AND s."PipelineVersionId" = $2
                    """, seed.SourceId, version)).ShouldBe(2);
                (await worker.Services.GetRequiredService<DriftSweep>().RunAsync(Ct)).Enqueued.ShouldBe(0);
            }

            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task An_html_document_reprocessed_by_a_parse_job_keeps_its_hosts_site_profile()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("avfall " + name))));
        var url = site.Url("/p/a");
        var seed = await EngineKit.SeedUrlListAsync(database, [url]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        var profile = await ApplySiteProfileAsync(seed.Classification, $"{url.IdnHost}:{url.Port}");
        try
        {
            await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            (await SiteProfileVersionsAsync(seed)).ShouldBe((profile, profile), "the fetch path applies the host's profile");

            // A publish drifts the page; the sweep's parse job re-runs it with the host's profile, so the set stays current.
            await PublishNewVersionAsync(seed);
            var sweep = worker.Services.GetRequiredService<DriftSweep>();
            (await sweep.RunAsync(Ct)).Enqueued.ShouldBe(1);
            await EngineKit.WaitForRunAsync(database, await SweepRunAsync(seed));
            (await SiteProfileVersionsAsync(seed)).ShouldBe((profile, profile), "the parse job applies the host's profile too");
            (await sweep.RunAsync(Ct)).Enqueued.ShouldBe(0, "no endless drift on the site-profile version");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_drifted_document_with_a_kept_set_of_the_wanted_key_is_swapped_back_without_a_job_and_one_without_is_re_processed()
    {
        // Pages long enough that a smaller chunkTokens gives new sets; unique text, as the embedding cache is shared. Documents
        // stay across tests and a site's port can come again, so every lookup is by source too.
        var tag = Guid.NewGuid().ToString("N");
        var text = string.Concat(Enumerable.Repeat(
            $"Bygglov {tag} krävs för nya byggnader, tillbyggnader och vissa ändringar av fasaden. Handläggaren granskar ritningar och situationsplan. ", 40));
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"bygglov {name} {tag}", text))));
        var (a, b) = (site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri);
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        try
        {
            await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs,
                settings: new Dictionary<string, string?> { ["Worker:Classes"] = "preview,command,sync,fetch,parse,embed" });
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var v1 = (A: await LiveSetAsync(seed, a), B: await LiveSetAsync(seed, b));
            var v2 = await EngineKit.PublishAsync(database, seed, "url-list", """{"chunkTokens":200}""");
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish))).Updated.ShouldBe(2);
            var v2Sets = (A: await LiveSetAsync(seed, a), B: await LiveSetAsync(seed, b));
            v2Sets.A.ShouldNotBe(v1.A);

            // b's v1 set is past the 14-day window and purged; a's is kept.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."ChunkSets" SET "SupersededAt" = now() - interval '15 days' WHERE "Id" = $1""", v1.B);
            await new IndexWriter(database.Database(RagDatabase.Worker)).SweepOrphansAsync(DateTimeOffset.UtcNow, Ct);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ChunkSets" WHERE "Id" = ANY($1)""", new[] { v1.A, v1.B }))
                .ShouldBe(1, "only a's v1 set is kept");

            // v1 published again without a run (its rollback run was lost): both live sets drifted.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."PipelineVersions" SET "State" = 'superseded' WHERE "Id" = $1""", v2);
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."PipelineVersions" SET "State" = 'published' WHERE "Id" = $1""", seed.VersionId);
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Pipelines" SET "PublishedVersionId" = $1 WHERE "Id" = $2""", seed.VersionId,
                seed.PipelineId);
            var setsOfA = await SetsAsync(seed, a);

            var sweep = worker.Services.GetRequiredService<DriftSweep>();
            var result = await sweep.RunAsync(Ct);
            var runId = await SweepRunAsync(seed);

            result.SwappedBack.ShouldBe(1, "a's kept v1 set has the wanted key");
            result.Enqueued.ShouldBe(1, "b has no kept set of the wanted key");
            (await LiveSetAsync(seed, a)).ShouldBe(v1.A, "the kept set is live again");
            (await SetsAsync(seed, a)).ShouldBe(setsOfA, "a swap back writes no chunk set");
            (await ContentJobsAsync(seed, runId, a)).ShouldBe(0, "a swap back enqueues no parse or embed job");
            (await ContentJobsAsync(seed, runId, b)).ShouldBeGreaterThan(0);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."RunItems" WHERE "RunId" = $1 AND "Outcome" = 'updated' AND "Reason" = $2
                """, runId, ChunkSetSwap.SwappedBackReason)).ShouldBe(1);

            (await EngineKit.WaitForRunAsync(database, runId)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 2, 0, 0, 0, 0));
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ChunkSets" WHERE "Id" = $1 AND "State" = 'superseded'""",
                v2Sets.A)).ShouldBe(1);
            var b1 = await LiveSetAsync(seed, b);
            b1.ShouldNotBe(v2Sets.B);
            (await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "PipelineVersionId" FROM content."ChunkSets" WHERE "Id" = $1""", b1))
                .ShouldBe(seed.VersionId, "b is rebuilt under v1 from the caches");
            var again = await sweep.RunAsync(Ct);
            (again.Enqueued, again.SwappedBack).ShouldBe((0L, 0L), "drift 0 once both documents are current");
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private Task<Guid> LiveSetAsync(SourceSeed seed, string identity) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "LiveChunkSetId" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $2
            """, seed.SourceId, identity);

    /// <summary>The ids of every chunk set of the document, in id order.</summary>
    private Task<string> SetsAsync(SourceSeed seed, string identity) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg(s."Id"::text, ',' ORDER BY s."Id") FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId"
            WHERE d."SourceId" = $1 AND d."CanonicalIdentity" = $2
            """, seed.SourceId, identity);

    /// <summary>The run's <c>parse</c> and <c>embed</c> jobs of one document.</summary>
    private Task<long> ContentJobsAsync(SourceSeed seed, Guid runId, string identity) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Jobs" j
            WHERE j."RunId" = $2 AND j."Class" IN ('parse', 'embed')
              AND (j."Payload" ->> 'documentId')::uuid = (SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $3)
            """, seed.SourceId, runId, identity);

    /// <summary>A manual site profile applied to (level, host).</summary>
    private async Task<Guid> ApplySiteProfileAsync(string level, string host)
    {
        var profileId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "UpdatedAt") VALUES ($1, $2, $3, now())
            """, profileId, level, host);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "Profile", "Origin", "State", "CreatedBy")
            VALUES ($1, $2, 1, $3::jsonb, 'manual', 'applied', 'system:worker')
            """, versionId, profileId, Pipelines.Json.ContractJson.Serialize(new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main" }));
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."SiteProfiles" SET "AppliedVersionId" = $1 WHERE "Id" = $2""", versionId, profileId);
        return versionId;
    }

    /// <summary>The document's and its live set's site-profile versions.</summary>
    private async Task<(Guid? Document, Guid? Set)> SiteProfileVersionsAsync(SourceSeed seed) =>
        (await Sql.ScalarAsync<Guid?>(database.Connection(null), """SELECT "SiteProfileVersionId" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId),
         await Sql.ScalarAsync<Guid?>(database.Connection(null), """
            SELECT s."SiteProfileVersionId" FROM content."Documents" d JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId" WHERE d."SourceId" = $1
            """, seed.SourceId));

    private Task<Guid> SweepRunAsync(SourceSeed seed) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Runs" WHERE "SourceId" = $1 AND "Kind" = 'reprocess' AND "Trigger" = 'schedule' ORDER BY "StartedAt" DESC LIMIT 1
            """, seed.SourceId);

    private Task<long> OpenParseJobsAsync(SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Jobs" j JOIN content."Documents" d ON j."Payload" ->> 'documentId' = d."Id"::text
            WHERE d."SourceId" = $1 AND j."Class" = 'parse' AND j."State" IN ('queued', 'running')
            """, seed.SourceId);

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
}
