using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// The rest of P1a item 9's done-when (P1 README §3; engine design §19): a crawl source syncs the Kestrel test site
/// (robots.txt, a sitemap, linked pages) into live chunks against the FakeGateway and a second run is unchanged; its
/// <c>Runs.Report</c> carries the crawl report's sections and <c>rag.crawl.incomplete_runs</c> is recorded; a worker killed
/// between a crawl fetch and the document's commit loses no document and duplicates none; <c>reprocess</c> after a new
/// published version re-processes every live document without a request (and only re-keys when no stage changed);
/// <c>retry-failed</c> fetches only what failed, for a crawl and a URL list; a fetch-phase continuation reconciles only
/// once the run's fetches are done.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class EngineCrawlTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly string LongText = string.Concat(Enumerable.Repeat(
        "Bygglov krävs för nya byggnader, tillbyggnader och vissa ändringar av fasaden. Handläggaren granskar ritningar och situationsplan. ", 40));

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-engine-blobs-");
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _status = new(StringComparer.Ordinal);
    private volatile bool _sitemapListsB = true;
    private volatile bool _withText;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_crawl_syncs_the_site_into_live_chunks_with_its_report_and_a_second_run_is_unchanged()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        // The gauge is the maximum over every active crawl source: another test's incomplete run must not count here.
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        using var gauge = new GaugeRecorder(worker.Services.GetRequiredService<IMeterFactory>(), "rag.crawl.incomplete_runs");
        try
        {
            var firstId = await EngineKit.SyncNowAsync(database, seed);
            var first = await EngineKit.WaitForRunAsync(database, firstId);

            // Seeds: the start page and the sitemap's two pages; /p/c is reached by a link from the start page.
            first.ShouldBe(new RunCounters(RunStates.Succeeded, 4, 0, 0, 0, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(4);
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(4);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await OpenRowsAsync(seed)).ShouldBe(0);
            using (var report = await ReportAsync(firstId))
            {
                var completeness = report.RootElement.GetProperty("completeness");
                completeness.GetProperty("complete").GetBoolean().ShouldBeTrue();
                completeness.GetProperty("seeds").GetInt32().ShouldBe(3);
                completeness.GetProperty("seedsResolved").GetInt32().ShouldBe(3);
                completeness.GetProperty("consecutiveIncomplete").GetInt32().ShouldBe(0);
                completeness.GetProperty("alert").GetBoolean().ShouldBeFalse();
                report.RootElement.GetProperty("removalChecks").GetProperty("checked").GetInt32().ShouldBe(0);
                var demote = report.RootElement.GetProperty("demote").EnumerateArray().Single();
                demote.GetProperty("pattern").GetString().ShouldBe("**/p/a");
                demote.GetProperty("documents").GetInt32().ShouldBe(1);
                report.RootElement.GetProperty("mode").GetProperty("incremental").GetBoolean().ShouldBeFalse();
                report.RootElement.GetProperty("verdict").GetProperty("runFailure").ValueKind.ShouldBe(JsonValueKind.Null);
            }

            gauge.Values.ShouldContain(0);
            var sets = await SetsAsync(seed);

            var second = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            second.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 4, 0, 0, 0));
            (await SetsAsync(seed)).ShouldBe(sets);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_url_that_leaves_the_sitemap_gets_the_removal_check_and_its_404_deletes_the_document()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(4);
            _sitemapListsB = false;
            _status["/p/b"] = StatusCodes.Status404NotFound;

            var runId = await EngineKit.SyncNowAsync(database, seed);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 3, 1, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(3);
            using var report = await ReportAsync(runId);
            var removal = report.RootElement.GetProperty("removalChecks");
            removal.GetProperty("checked").GetInt32().ShouldBe(1);
            removal.GetProperty("gone").GetInt32().ShouldBe(1);
            removal.GetProperty("goneByReason").GetProperty("http.404").GetInt32().ShouldBe(1);
            report.RootElement.GetProperty("completeness").GetProperty("seeds").GetInt32().ShouldBe(2, "a removal check is no seed");
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."CrawlFrontier" WHERE "SourceId" = $1 AND "RunId" = $2 AND "RemovalCheck" AND "Depth" = 0
                """, seed.SourceId, runId)).ShouldBe(1, "the removal check is flagged at depth 0, not at the old depth -1");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task An_incremental_run_is_recorded_on_crawl_runs_and_the_next_runs_history_reads_it_from_there()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        // An incremental run stays incremental only when the host's profile marks lastmod reliable.
        await ApplySiteProfileAsync(seed, TestSite.Host, new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main", LastmodReliable = true });
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var fullId = await EngineKit.SyncNowAsync(database, seed);
            (await EngineKit.WaitForRunAsync(database, fullId)).Added.ShouldBe(4);
            var incrementalId = await EngineKit.SyncNowAsync(database, seed, incremental: true);
            (await EngineKit.WaitForRunAsync(database, incrementalId)).State.ShouldBe(RunStates.Succeeded);
            var nextId = await EngineKit.SyncNowAsync(database, seed);
            await EngineKit.WaitForRunAsync(database, nextId);

            (await CrawlRunIncrementalAsync(fullId)).ShouldBeFalse();
            (await CrawlRunIncrementalAsync(incrementalId)).ShouldBeTrue();
            (await CrawlRunIncrementalAsync(nextId)).ShouldBeFalse();
            // Without the Report's mode the history still knows the incremental run: it reads CrawlRuns.Incremental.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Runs" SET "Report" = "Report" - 'mode' WHERE "Id" = $1""", incrementalId);
            var history = await worker.Services.GetRequiredService<RunStore>().CrawlHistoryAsync(seed.SourceId, nextId, Ct);
            history.Select(h => h.Incremental).ShouldBe([true, false]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."SiteProfiles" WHERE "Classification" = $1 AND "Host" = $2""",
                seed.Classification, TestSite.Host);
        }
    }

    [Fact]
    public async Task A_worker_killed_between_a_crawl_fetch_and_its_commit_loses_no_document_and_duplicates_none()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        var gate = new StageBlobGate();
        try
        {
            var first = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, services: services =>
                services.AddSingleton<IBlobStore>(sp => new GatedBlobStore(new EnvelopeBlobStore(sp.GetRequiredService<BlobEnvelope>(),
                    new FileSystemBlobStore(sp.GetRequiredService<IOptions<BlobStoreOptions>>(), sp.GetRequiredService<TimeProvider>())), gate)));
            var runId = await EngineKit.SyncNowAsync(database, seed);
            // A fetch job has fetched its page and stands in its segment, before the document's commit.
            await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var fetched = await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'fetch' AND "State" = 'running'
                """, runId);
            fetched.ShouldBeGreaterThan(0);
            await first.KillAsync();

            await using var second = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 4, 0, 0, 0, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(4, "every page is live");
            (await EngineKit.DocumentsAsync(database, seed, live: false)).ShouldBe(4, "no duplicate documents");
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await OpenRowsAsync(seed)).ShouldBe(0);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'fetch' AND "Reclaims" > 0 AND "State" = 'succeeded' AND "Attempts" = 1
                """, runId)).ShouldBeGreaterThan(0, "a fetch was reclaimed from the killed worker without losing an attempt");
        }
        finally
        {
            gate.Open();
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Reprocess_after_a_new_version_reprocesses_every_live_document_without_fetching()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(4);
            var requests = _hits.Values.Sum();
            var smaller = await EngineKit.PublishAsync(database, seed, "website", """{"chunkTokens":300}""");

            var runId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 4, 0, 0, 0, 0));
            _hits.Values.Sum().ShouldBe(requests, "a reprocess never fetches");
            (await LiveVersionsAsync(seed)).ShouldBe([smaller]);
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Kind" || '/' || "Trigger" FROM content."Runs" WHERE "Id" = $1""", runId))
                .ShouldBe("reprocess/publish");
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);

            // A version whose change no HTML stage reads (OCR is for the parser route): every set is only re-keyed.
            var sets = await SetsAsync(seed);
            var relabel = await EngineKit.PublishAsync(database, seed, "website", """{"chunkTokens":300,"ocr":"off"}""");
            var rekeyId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish);

            (await EngineKit.WaitForRunAsync(database, rekeyId)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 4, 0, 0, 0));
            (await SetsAsync(seed)).ShouldBe(sets);
            (await LiveVersionsAsync(seed)).ShouldBe([relabel]);
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."RunItems" WHERE "RunId" = $1 AND "Outcome" = 'unchanged' AND "Reason" = 'version.unaffected'
                """, rekeyId)).ShouldBe(4);
            _hits.Values.Sum().ShouldBe(requests);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_new_site_profile_version_reprocesses_only_the_hosts_html_documents()
    {
        _withText = true;
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(5);
            var before = await LiveSetsAsync(seed);
            var requests = _hits.Values.Sum();
            // What edit-site-profile applies (#115): a manual version of the host's profile that strips the contact line.
            const string host = TestSite.Host;
            var version = await ApplySiteProfileAsync(seed, host,
                new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main", Boilerplate = new() { Selectors = [".kontakt"] } });

            var runId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.SiteProfile);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            // Four HTML pages are extracted again with the new version; the text file is only re-keyed to it.
            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 4, 1, 0, 0, 0));
            _hits.Values.Sum().ShouldBe(requests, "a reprocess never fetches");
            var after = await LiveSetsAsync(seed);
            foreach (var (url, set) in after)
            {
                if (url.EndsWith(".txt", StringComparison.Ordinal))
                {
                    set.ShouldBe(before[url], "the text file keeps its set");
                }
                else
                {
                    set.ShouldNotBe(before[url], $"{url} gets a new chunk set");
                }
            }

            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
                WHERE d."SourceId" = $1 AND c."DeletedAt" IS NULL AND c."Text" LIKE '%Kontakta oss%'
                """, seed.SourceId)).ShouldBe(0, "the new profile's boilerplate is gone from the live chunks");
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id"
                WHERE d."SourceId" = $1 AND s."SiteProfileVersionId" = $2
                """, seed.SourceId, version)).ShouldBe(5);
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Kind" || '/' || "Trigger" FROM content."Runs" WHERE "Id" = $1""", runId))
                .ShouldBe("reprocess/site-profile");
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            // The key has no port (SiteProfileHost), so the profile would reach every later test on the test site.
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."SiteProfiles" WHERE "Classification" = $1 AND "Host" = $2""",
                seed.Classification, TestSite.Host);
        }
    }

    [Fact]
    public async Task A_path_include_on_a_host_with_an_identity_rule_keeps_its_node_id_documents_and_checks_one_that_left_the_sitemap()
    {
        // P1a item 20: include matches the path form, documents keep the node id; a node-id document is neither
        // scope-narrowed by the path include nor spared the removal check when it leaves the sitemap.
        var tag = Guid.NewGuid().ToString("N");
        var listsB = true;
        var bGone = false;
        await using var site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", (HttpContext context) =>
                Results.Text($"User-agent: *\nAllow: /\nSitemap: http://{TestSite.Host}:{context.Request.Host.Port}/sitemap.xml\n", "text/plain"));
            app.MapGet("/sitemap.xml", (HttpContext context) => Results.Content(
                """<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">"""
                + (listsB ? $"<url><loc>http://{TestSite.Host}:{context.Request.Host.Port}/sektion/sida-b.4.20000000000b.html</loc><lastmod>2026-01-15</lastmod></url>" : string.Empty)
                + $"<url><loc>http://{TestSite.Host}:{context.Request.Host.Port}/annan/sida-c.4.30000000000c.html</loc><lastmod>2026-01-15</lastmod></url></urlset>",
                "application/xml"));
            app.MapGet("/sektion", () => EngineKit.Html(EngineKit.Page($"sektion {tag}",
                """<a href="/sektion/sida-a.4.10000000000a.html">A</a> <a href="/sektion/under/sida-a.4.10000000000a.html">A igen</a> <a href="/annan/sida-d.4.40000000000d.html">D</a>""")));
            app.MapGet("/{node}", (string node) => node switch
            {
                "4.20000000000b.html" when bGone => Results.NotFound(),
                "4.10000000000a.html" or "4.20000000000b.html" or "4.30000000000c.html" or "4.40000000000d.html" => EngineKit.Html(EngineKit.Page($"nod {node} {tag}")),
                _ => Results.NotFound(),
            });
        });
        var section = $"{TestSite.Host}:{site.Port}/sektion";
        var seed = await EngineKit.SeedCrawlAsync(database, new { startUrls = new[] { site.Url("/sektion").AbsoluteUri }, include = new[] { section, section + "/**" } });
        await ApplySiteProfileAsync(seed, TestSite.Host, new Pipelines.SiteProfiles.SiteProfile
        {
            MainRoot = "main",
            IdentityRule = new() { Pattern = @"^/(?:.*/)?(?:[^/]+\.)?(?<id>[0-9]+\.[0-9a-f]{10,})\.html$", Replacement = "/${id}.html", Verified = true },
        });
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var first = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            first.ShouldBe(new RunCounters(RunStates.Succeeded, 3, 0, 0, 0, 0, 0), "the start page, A (two paths, one node) and B from the sitemap");
            (await IdentitiesAsync(seed)).ShouldBe([site.Url("/4.10000000000a.html").AbsoluteUri, site.Url("/4.20000000000b.html").AbsoluteUri, site.Url("/sektion").AbsoluteUri]);

            listsB = false;
            bGone = true;
            var runId = await EngineKit.SyncNowAsync(database, seed);
            var second = await EngineKit.WaitForRunAsync(database, runId);

            second.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 2, 1, 0, 0), "B left the sitemap: its removal check finds the 404");
            using var report = await ReportAsync(runId);
            report.RootElement.GetProperty("removalChecks").GetProperty("gone").GetInt32().ShouldBe(1);
            (await IdentitiesAsync(seed)).ShouldBe([site.Url("/4.10000000000a.html").AbsoluteUri, site.Url("/sektion").AbsoluteUri]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."SiteProfiles" WHERE "Classification" = $1 AND "Host" = $2""",
                seed.Classification, TestSite.Host);
        }
    }

    [Fact]
    public async Task Retry_failed_on_a_crawl_fetches_only_the_failed_page_and_the_incomplete_traversal_is_reported()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        using var gauge = new GaugeRecorder(worker.Services.GetRequiredService<IMeterFactory>(), "rag.crawl.incomplete_runs");
        try
        {
            _status["/p/b"] = StatusCodes.Status403Forbidden;
            var firstId = await EngineKit.SyncNowAsync(database, seed);
            var first = await EngineKit.WaitForRunAsync(database, firstId);

            first.ShouldBe(new RunCounters(RunStates.Degraded, 3, 0, 0, 0, 1, 0));
            using (var report = await ReportAsync(firstId))
            {
                var completeness = report.RootElement.GetProperty("completeness");
                completeness.GetProperty("complete").GetBoolean().ShouldBeFalse();
                completeness.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldContain("crawl.seeds_failed");
                completeness.GetProperty("seedsFailed").GetInt32().ShouldBe(1);
                completeness.GetProperty("consecutiveIncomplete").GetInt32().ShouldBe(1);
            }

            gauge.Values.ShouldContain(1);
            _status.Clear();
            var before = new Dictionary<string, int>(_hits, StringComparer.Ordinal);

            var retryId = await EngineKit.SyncNowAsync(database, seed, SyncKind.RetryFailed);
            var retry = await EngineKit.WaitForRunAsync(database, retryId);

            retry.ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(4);
            _hits["/p/b"].ShouldBe(before["/p/b"] + 1);
            foreach (var path in new[] { "/", "/p/a", "/p/c" })
            {
                _hits[path].ShouldBe(before[path], $"{path} did not fail and is not fetched again");
            }

            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Kind" FROM content."Runs" WHERE "Id" = $1""", retryId)).ShouldBe(RunKinds.RetryFailed);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Retry_failed_on_a_url_list_fetches_only_the_failed_url()
    {
        var hits = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failing = true;
        await using var site = await EngineKit.SiteAsync((name, _) =>
        {
            hits.AddOrUpdate(name, 1, (_, n) => n + 1);
            return Task.FromResult(name == "b" && Volatile.Read(ref failing) ? Results.StatusCode(StatusCodes.Status403Forbidden) : EngineKit.Html(EngineKit.Page("skola " + name)));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).ShouldBe(new RunCounters(RunStates.Degraded, 1, 0, 0, 0, 1, 0));
            Volatile.Write(ref failing, false);

            var retry = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.RetryFailed));

            retry.ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            hits["a"].ShouldBe(1, "the URL that did not fail is not fetched again");
            hits["b"].ShouldBe(2);
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(2);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_fetch_phase_continuation_while_the_runs_fetches_are_in_flight_does_not_reconcile_before_them()
    {
        // The finalizer can find the run with no job between a top-up's release and its enqueue; its fetch-phase continuation
        // may then run after the fetches were enqueued, and their dedup keys drop its enqueues. Reconciling then would see
        // the rows still open (an incomplete traversal) and miss the documents the run did not see.
        var tag = Guid.NewGuid().ToString("N");
        var pages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", (HttpContext context) =>
                Results.Text($"User-agent: *\nAllow: /\nSitemap: http://{TestSite.Host}:{context.Request.Host.Port}/sitemap.xml\n", "text/plain"));
            app.MapGet("/sitemap.xml", () => Results.Content(
                """<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"></urlset>""", "application/xml"));
            app.MapGet("/", () => EngineKit.Html(EngineKit.Page($"startsida {tag}", """<a href="/p/1">Ett</a> <a href="/p/2">Två</a>""")));
            app.MapGet("/p/{name}", async (string name) =>
            {
                reached.TrySetResult();
                await pages.Task;
                return EngineKit.Html(EngineKit.Page($"bygglov {name} {tag}"));
            });
        });
        var seed = await EngineKit.SeedCrawlAsync(database, new { startUrls = new[] { site.Url("/").AbsoluteUri } });
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var runId = await EngineKit.SyncNowAsync(database, seed);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var continuation = SyncJobs.Continuation(seed.SourceId, runId, seed.DepartmentId, seed.Classification, RunKinds.Sync, RunTriggers.Manual,
                SyncPhase.Fetch);
            (await database.Queue(RagDatabase.Worker).EnqueueAsync([continuation], null, Ct)).ShouldContain(continuation.Id);
            await WaitForJobAsync(continuation.Id);

            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Phase" FROM content."Runs" WHERE "Id" = $1""", runId))
                .ShouldBe(RunPhases.Fetch, "the run's fetches are still running");

            pages.SetResult();
            (await EngineKit.WaitForRunAsync(database, runId)).ShouldBe(new RunCounters(RunStates.Succeeded, 3, 0, 0, 0, 0, 0));
            using var report = await ReportAsync(runId);
            report.RootElement.GetProperty("completeness").GetProperty("complete").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            pages.TrySetResult();
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private static object Settings(TestSite site) => new
    {
        startUrls = new[] { site.Url("/").AbsoluteUri },
        demote = new[] { new { pattern = "**/p/a", factor = 0.5 } },
    };

    private async Task<TestSite> SiteAsync() => await TestSite.StartAsync(app =>
    {
        app.MapGet("/robots.txt", (HttpContext context) =>
            Results.Text($"User-agent: *\nAllow: /\nSitemap: http://{TestSite.Host}:{context.Request.Host.Port}/sitemap.xml\n", "text/plain"));
        app.MapGet("/sitemap.xml", (HttpContext context) => Results.Content(Sitemap(context.Request.Host.Port), "application/xml"));
        app.MapGet("/", () => Page("/", "startsida", """<a href="/p/c">Mer om bygglov</a>""" + (_withText ? """ <a href="/filer/regler.txt">Regler</a>""" : string.Empty)));
        app.MapGet("/p/{name}", (string name) => Page("/p/" + name, "bygglov " + name, string.Empty));
        app.MapGet("/filer/regler.txt", () =>
        {
            _hits.AddOrUpdate("/filer/regler.txt", 1, (_, n) => n + 1);
            return Results.Text("Regler för bygglov\n\n" + LongText, "text/plain; charset=utf-8");
        });
    });

    private IResult Page(string path, string title, string links)
    {
        _hits.AddOrUpdate(path, 1, (_, n) => n + 1);
        return _status.TryGetValue(path, out var status)
            ? Results.StatusCode(status)
            : EngineKit.Html(EngineKit.Page(title, links + """ <span class="kontakt">Kontakta oss på telefon 090-16 10 00.</span> """ + LongText));
    }

    private string Sitemap(int? port)
    {
        var urls = _sitemapListsB ? new[] { "/p/a", "/p/b" } : ["/p/a"];
        return """<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""" +
            string.Concat(urls.Select(u => $"<url><loc>http://{TestSite.Host}:{port}{u}</loc><lastmod>2026-01-15</lastmod></url>")) + "</urlset>";
    }

    private async Task<JsonDocument> ReportAsync(Guid runId) =>
        JsonDocument.Parse(await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Report"::text FROM content."Runs" WHERE "Id" = $1""", runId));

    /// <summary>Applies a new manual version of the (level, host) profile as <c>edit-site-profile</c> does; returns the version.</summary>
    private async Task<Guid> ApplySiteProfileAsync(SourceSeed seed, string host, Pipelines.SiteProfiles.SiteProfile profile)
    {
        var profileId = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "PresetId", "UpdatedAt") VALUES ($1, $2, $3, NULL, now())
            """, profileId, seed.Classification, host);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "Profile", "Origin", "State", "CreatedBy", "CreatedAt", "AppliedAt", "AppliedBy")
            VALUES ($1, $2, 1, $3::jsonb, 'manual', 'applied', 'test', now(), now(), 'test')
            """, version, profileId, Pipelines.Json.ContractJson.Serialize(profile));
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."SiteProfiles" SET "AppliedVersionId" = $1 WHERE "Id" = $2""", version, profileId);
        return version;
    }

    /// <summary>The source's live documents' identities, in order.</summary>
    private async Task<List<string>> IdentitiesAsync(SourceSeed seed)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "CanonicalIdentity" FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL ORDER BY "CanonicalIdentity"
            """, connection) { Parameters = { new Npgsql.NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var identities = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            identities.Add(reader.GetString(0));
        }

        return identities;
    }

    private async Task<Dictionary<string, Guid>> LiveSetsAsync(SourceSeed seed)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "CanonicalIdentity", "LiveChunkSetId" FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL AND "LiveChunkSetId" IS NOT NULL
            """, connection) { Parameters = { new Npgsql.NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var sets = new Dictionary<string, Guid>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            sets[reader.GetString(0)] = reader.GetGuid(1);
        }

        return sets;
    }

    private async Task WaitForJobAsync(Guid jobId)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."Jobs" WHERE "Id" = $1""", jobId) is "queued" or "running")
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException($"Job {jobId} did not finish.");
            }

            await Task.Delay(100, Ct);
        }
    }

    private Task<bool> CrawlRunIncrementalAsync(Guid runId) =>
        Sql.ScalarAsync<bool>(database.Connection(null), """SELECT "Incremental" FROM content."CrawlRuns" WHERE "RunId" = $1""", runId);

    private Task<long> OpenRowsAsync(SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."CrawlFrontier" WHERE "SourceId" = $1 AND "State" <> 'idle'""", seed.SourceId);

    private Task<long> SetsAsync(SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null),
            """SELECT count(*) FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId" WHERE d."SourceId" = $1""", seed.SourceId);

    private async Task<IReadOnlyList<Guid>> LiveVersionsAsync(SourceSeed seed)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT DISTINCT s."PipelineVersionId" FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id"
            WHERE d."SourceId" = $1 AND d."DeletedAt" IS NULL
            """, connection) { Parameters = { new Npgsql.NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var versions = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            versions.Add(reader.GetGuid(0));
        }

        return versions;
    }
}

/// <summary>The values a worker's meter records on one gauge (other hosts' meters are ignored).</summary>
internal sealed class GaugeRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<int> _values = new();

    public GaugeRecorder(IMeterFactory factory, string name)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == name && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((_, value, _, _) => _values.Enqueue(value));
        _listener.Start();
    }

    public IReadOnlyList<int> Values => [.. _values];

    public void Dispose() => _listener.Dispose();
}

/// <summary>Holds the first write of a stage output (a fetch job's segment, after its page was fetched) until opened.</summary>
internal sealed class StageBlobGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _open.TrySetResult();

    public async Task WaitAsync(BlobKey key, CancellationToken cancellationToken)
    {
        if (key.Area != "stages" || _open.Task.IsCompleted)
        {
            return;
        }

        Blocked.TrySetResult();
        await _open.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class GatedBlobStore(IBlobStore inner, StageBlobGate gate) : IBlobStore
{
    public async Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(key, cancellationToken);
        return await inner.PutAsync(key, content, length, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken cancellationToken) => inner.OpenReadAsync(key, range, cancellationToken);

    public Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);

    public Task<BlobWriteResult> PromoteAsync(BlobKey from, BlobKey to, CancellationToken cancellationToken) => inner.PromoteAsync(from, to, cancellationToken);

    public Task DeleteAsync(BlobKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public Task DeletePrefixAsync(BlobKey prefix, CancellationToken cancellationToken) => inner.DeletePrefixAsync(prefix, cancellationToken);

    public IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, CancellationToken cancellationToken) => inner.ListAsync(prefix, cancellationToken);
}
