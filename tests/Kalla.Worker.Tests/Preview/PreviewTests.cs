using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.TestKit;
using Kalla.Worker.Jobs;
using Kalla.Worker.Preview;
using Kalla.Worker.Tests.Connectors;
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Preview;

/// <summary>
/// P1b item 5's done-when (P1b README §3; engine design §19): a <c>stage-preview</c> over a document of a live source leaves
/// every <c>content</c> table but <c>Jobs</c>, <c>CrawlHosts</c> and the instance heartbeat, and every live blob, unchanged
/// (row counts and checksums before and after), returns the stage outputs up to <c>untilStage</c>, and its blobs are swept
/// after 24 h; a second preview with <c>basePreviewJobId</c> reuses the first one's fetch and unchanged stages; six
/// documents are refused; a URL-list preflight reports a robots-disallowed and an egress-blocked URL with their reasons.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class PreviewTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Tables a preview may change (engine design §11, §19) and the instance heartbeat, which moves every second.</summary>
    private static readonly string[] Unwatched = ["Jobs", "CrawlHosts", "WorkerInstances"];

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-preview-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_stage_preview_of_a_live_document_changes_no_live_row_or_blob_and_its_blobs_are_swept_after_24_hours()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            var documentId = await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);
            var draft = await DraftAsync(seed, chunkTokens: 200);
            // Cache rows last used two days ago: a preview that touched them would move LastUsedAt.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."NodeOutputs" SET "LastUsedAt" = now() - interval '2 days'""");
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."EmbeddingCache" SET "LastUsedAt" = now() - interval '2 days'""");
            var before = await StableSnapshotAsync();
            var liveBlobs = LiveBlobs();

            var jobId = await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = draft,
                CollectionId = seed.CollectionId,
                SourceId = seed.SourceId,
                DocumentIds = [documentId],
                PiiSpans = true,
            });
            var result = await ResultAsync<StagePreviewResult>(jobId);

            (await SnapshotAsync()).ShouldBe(before, "a preview changes no content row but its job and the host budget");
            LiveBlobs().ShouldBe(liveBlobs, "a preview writes no blob outside previews/{jobId}/");
            var document = result.Documents.ShouldHaveSingleItem();
            document.DocumentId.ShouldBe(documentId);
            document.Stages.Select(s => s.Stage).ShouldBe(["fetch", "detect", "parse", "clean", "chunk", "personal-data"]);
            document.Stages.ShouldAllBe(s => s.State == PreviewStageState.Succeeded);
            document.Stages.Where(s => s.Stage is "detect" or "parse" or "clean").ShouldAllBe(s => s.CacheHit, "read from the live cache");
            document.Stages.Single(s => s.Stage == "chunk").CacheHit.ShouldBeFalse("the draft's chunk size gives a new key");
            document.Stages.Single(s => s.Stage == "fetch").Facts!.Value.GetProperty("fetchMode").GetString().ShouldBe(FetchModes.Http);
            // The preview's outputs are encrypted by the source's level (data-model §7); the admin reads them with it.
            var blobs = worker.Services.GetRequiredService<IBlobStore>().ForLevel(seed.Classification);
            (await TextAsync(blobs, document.Stages.Single(s => s.Stage == "parse").Output!)).ShouldContain("bygglov a");
            (await TextAsync(blobs, document.Stages.Single(s => s.Stage == "chunk").Output!)).ShouldContain("\"chunks\"");
            (await TextAsync(blobs, document.Stages.Single(s => s.Stage == "personal-data").Output!)).ShouldContain("\"spans\"");
            document.SiteProfile.ShouldNotBeNull();
            document.SiteProfile.Origin.ShouldBe(SiteProfileOrigin.Preset);
            document.SiteProfile.PresetId.ShouldBe("generic");
            (await TextAsync(blobs, document.SiteProfile.Before)).ShouldContain("bygglov a");
            result.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));

            var prefix = PreviewBlobs.PrefixOf(jobId);
            (await CountAsync(blobs, prefix)).ShouldBeGreaterThan(0);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(23));
            var sweep = new PreviewBlobSweep(blobs, Options.Create(new WorkerOptions()), clock, NullLogger<PreviewBlobSweep>.Instance);
            (await sweep.RunAsync(Ct)).ShouldBe(0, "younger than 24 hours");
            (await CountAsync(blobs, prefix)).ShouldBeGreaterThan(0);
            clock.Advance(TimeSpan.FromHours(2));
            (await sweep.RunAsync(Ct)).ShouldBe(1);
            (await CountAsync(blobs, prefix)).ShouldBe(0);
            LiveBlobs().ShouldBe(liveBlobs, "the sweep removes preview blobs only");

            // Parity with production (engine design §11): under the live version the preview builds every stage key as the
            // segment did, inputs and options included, so each cached stage is read from the live cache.
            var live = await ResultAsync<StagePreviewResult>(await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = seed.VersionId,
                CollectionId = seed.CollectionId,
                SourceId = seed.SourceId,
                DocumentIds = [documentId],
            }));
            var liveStages = live.Documents.ShouldHaveSingleItem().Stages;
            liveStages.Select(s => s.Stage).ShouldBe(["fetch", "detect", "parse", "clean", "chunk", "personal-data"]);
            liveStages.Where(s => s.Stage != "fetch").ShouldAllBe(s => s.State == PreviewStageState.Succeeded && s.CacheHit,
                "the live version reads every stage from the live cache");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_preview_dates_the_chunks_as_production_does_from_a_reliable_lastmod_with_the_same_keys()
    {
        // Issue #435 (processing design §7): /p/a is in the sitemap (lastmod 2026-01-15), the root is not; both pages
        // send Last-Modified.
        await using var site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", (HttpContext context) => Results.Text(
                $"User-agent: *\nAllow: /\nSitemap: http://{TestSite.Host}:{context.Request.Host.Port}/sitemap.xml\n",
                "text/plain"));
            app.MapGet("/sitemap.xml", (HttpContext context) => Results.Content(
                """<?xml version="1.0" encoding="UTF-8"?>""" +
                """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""" +
                $"<url><loc>http://{TestSite.Host}:{context.Request.Host.Port}/p/a</loc>" +
                "<lastmod>2026-01-15</lastmod></url></urlset>", "application/xml"));
            app.MapGet("/", (HttpContext context) => DatedPage(context, "startsida"));
            app.MapGet("/p/{name}", (string name, HttpContext context) => DatedPage(context, "bygglov " + name));
        });
        var seed = await EngineKit.SeedCrawlAsync(database, new { startUrls = new[] { site.Url("/").AbsoluteUri } });
        await ApplySiteProfileAsync(seed,
            new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main", LastmodReliable = true });
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var documentId = await Sql.ScalarAsync<Guid>(database.Connection(null), """
                SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "Url" LIKE '%/p/a'
                """, seed.SourceId);
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT to_char("SitemapLastMod" AT TIME ZONE 'UTC', 'YYYY-MM-DD') || ' ' ||
                       to_char("HttpLastModified" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI')
                FROM content."Documents" WHERE "Id" = $1
                """, documentId)).ShouldBe("2026-01-15 2026-03-01 08:00", "the fetch stores both dates");
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT string_agg(DISTINCT (d."Id" = $2)::text || ' ' ||
                                  to_char(c."SourceModifiedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD'), ',')
                FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
                WHERE d."SourceId" = $1 AND c."DeletedAt" IS NULL
                """, seed.SourceId, documentId)).ShouldBe("false 2026-03-01,true 2026-01-15",
                "the reliable lastmod dates /p/a; the root, not in the sitemap, falls back to Last-Modified");

            var result = await ResultAsync<StagePreviewResult>(await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = seed.VersionId,
                CollectionId = seed.CollectionId,
                SourceId = seed.SourceId,
                DocumentIds = [documentId],
            }));
            var stages = result.Documents.ShouldHaveSingleItem().Stages;
            stages.Select(s => s.Stage).ShouldBe(["fetch", "detect", "parse", "clean", "chunk", "personal-data"]);
            stages.Where(s => s.Stage != "fetch").ShouldAllBe(s => s.State == PreviewStageState.Succeeded && s.CacheHit,
                "the preview builds the chunk stage's metadata input, dates included, as the segment did");
            var blobs = worker.Services.GetRequiredService<IBlobStore>().ForLevel(seed.Classification);
            (await TextAsync(blobs, stages.Single(s => s.Stage == "chunk").Output!))
                .ShouldContain("\"sourceModifiedAt\":\"2026-01-15\"");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            await Sql.ExecuteAsync(database.Connection(null), """
                DELETE FROM content."SiteProfiles" WHERE "Classification" = $1 AND "Host" = $2
                """, seed.Classification, TestSite.Host);
        }
    }

    [Fact]
    public async Task A_preview_with_a_base_preview_reuses_its_fetch_and_runs_only_the_stages_whose_key_changed()
    {
        var requests = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await using var site = await EngineKit.SiteAsync((name, _) =>
        {
            requests.AddOrUpdate(name, 1, (_, n) => n + 1);
            return Task.FromResult(EngineKit.Html(EngineKit.Page("förskola " + name)));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/other")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var url = site.Url("/p/new").AbsoluteUri;
            var first = await EnqueueAsync(seed, new StagePreviewPayload { PipelineVersionId = seed.VersionId, CollectionId = seed.CollectionId, Urls = [url] });
            var firstResult = await ResultAsync<StagePreviewResult>(first);
            firstResult.Documents.ShouldHaveSingleItem().Stages.ShouldAllBe(s => s.State == PreviewStageState.Succeeded);
            firstResult.Documents[0].Stages.Single(s => s.Stage == "chunk").CacheHit.ShouldBeFalse();
            requests["new"].ShouldBe(1);

            var draft = await DraftAsync(seed, chunkTokens: 200);
            var second = await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = draft,
                CollectionId = seed.CollectionId,
                Urls = [url],
                BasePreviewJobId = first,
                UntilStage = "index",
            });
            var stages = (await ResultAsync<StagePreviewResult>(second)).Documents.ShouldHaveSingleItem().Stages;

            requests["new"].ShouldBe(1, "the base preview's raw content is reused");
            firstResult.Documents[0].Stages.Single(s => s.Stage == "fetch").Facts!.Value.GetProperty("fetchMode").GetString().ShouldBe(FetchModes.Http);
            stages.Single(s => s.Stage == "fetch").Facts!.Value.GetProperty("fetchMode").GetString().ShouldBe(FetchModes.Http, "read back with the reused content");
            stages.Select(s => s.Stage).ShouldBe(["fetch", "detect", "parse", "clean", "chunk", "personal-data", "embed", "index"]);
            stages.Where(s => s.Stage is "fetch" or "detect" or "parse" or "clean").ShouldAllBe(s => s.CacheHit && s.State == PreviewStageState.Succeeded);
            stages.Single(s => s.Stage == "chunk").CacheHit.ShouldBeFalse("the draft's chunk size gives the chunk stage a new key");
            stages.Single(s => s.Stage == "personal-data").CacheHit.ShouldBeTrue("the short page chunks the same, so personal-data keeps its key");
            stages.Single(s => s.Stage == "chunk").Output!.ShouldStartWith(PreviewBlobs.PrefixOf(second).Value + "/");
            var embed = stages.Single(s => s.Stage == "embed");
            embed.Facts!.Value.GetProperty("embedded").GetBoolean().ShouldBeFalse();
            embed.Facts.Value.GetProperty("chunks").GetInt32().ShouldBeGreaterThan(0);
            var index = stages.Single(s => s.Stage == "index");
            index.State.ShouldBe(PreviewStageState.Skipped);
            index.Facts!.Value.GetProperty("wouldWrite").GetInt32().ShouldBe(embed.Facts.Value.GetProperty("chunks").GetInt32());
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>
    /// The preview's embedding calls carry the job's id as <c>x-fors-run-id</c> and are counted under the payload's collection
    /// with the job's completion (engine design §10.6), the one usage write a preview makes.
    /// </summary>
    [Fact]
    public async Task Embed_true_embeds_through_the_gateway_and_writes_no_embedding_cache_row()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"parkering {name} {marker}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        var recorder = new GatewayCallRecorder();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs,
            services: s => s.AddHttpClient(Worker.Processing.Embedding.EmbeddingClient.HttpClientName).AddHttpMessageHandler(recorder.Handler));
        try
        {
            var cached = await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."EmbeddingCache" """);
            var job = await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = seed.VersionId,
                CollectionId = seed.CollectionId,
                Urls = [site.Url("/p/embedded").AbsoluteUri],
                UntilStage = "embed",
                Embed = true,
            });
            var embed = (await ResultAsync<StagePreviewResult>(job)).Documents.ShouldHaveSingleItem().Stages.Single(s => s.Stage == "embed");

            embed.State.ShouldBe(PreviewStageState.Succeeded);
            embed.Facts!.Value.GetProperty("embedded").GetBoolean().ShouldBeTrue();
            embed.Facts.Value.GetProperty("dimensions").GetInt32().ShouldBe(EngineKit.Dimensions);
            embed.Facts.Value.GetProperty("embeddingTokens").GetInt64().ShouldBeGreaterThan(0);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."EmbeddingCache" """)).ShouldBe(cached);

            var calls = recorder.Answered;
            calls.ShouldNotBeEmpty();
            calls.ShouldAllBe(c => c.RunId == job.ToString() && c.Step == "embed");
            var usage = (await EngineKit.DailyUsageAsync(database, seed.CollectionId)).ShouldHaveSingleItem();
            usage.Key.ShouldBe("embed");
            (usage.Value.Calls, usage.Value.InputTokens, usage.Value.CostUsd).ShouldBe((calls.Count, calls.Sum(c => c.InputTokens), calls.Sum(c => c.CostUsd!.Value)));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task The_personal_data_state_shown_follows_the_source_pii_mode()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"kontakt {name} {marker}",
            "Frågor besvaras av anna.svensson@gmail.com."))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/other")], Kalla.Domain.Classification.Public);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            async Task<JsonElement> PersonalDataAsync()
            {
                var job = await EnqueueAsync(seed, new StagePreviewPayload
                {
                    PipelineVersionId = seed.VersionId,
                    CollectionId = seed.CollectionId,
                    SourceId = seed.SourceId,
                    Urls = [site.Url("/p/contact").AbsoluteUri],
                });
                return (await ResultAsync<StagePreviewResult>(job)).Documents.ShouldHaveSingleItem().Stages.Single(s => s.Stage == "personal-data").Facts!.Value;
            }

            var held = await PersonalDataAsync();
            held.GetProperty("state").GetString().ShouldBe("held");
            held.GetProperty("flagged").GetBoolean().ShouldBeTrue();

            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = "Settings" || '{"piiMode":"report"}'::jsonb WHERE "Id" = $1""",
                seed.SourceId);
            var reported = await PersonalDataAsync();
            reported.GetProperty("state").GetString().ShouldBe("live", "report mode: an external e-mail address does not hold");
            reported.GetProperty("flagged").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Each_document_lists_the_tags_its_path_rules_give_it()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var draft = await DraftAsync(seed, """
                {"pathRules":[{"match":"/p/bygglov-*","on":"path","tags":["bygglov","bygga-bo"]},{"match":"/p/bygglov-ansok","on":"path","tags":["bygglov","ansokan"]}]}
                """);
            StagePreviewPayload Payload(string untilStage) => new()
            {
                PipelineVersionId = draft,
                CollectionId = seed.CollectionId,
                SourceId = seed.SourceId,
                Urls = [site.Url("/p/bygglov-ansok").AbsoluteUri, site.Url("/p/other").AbsoluteUri],
                UntilStage = untilStage,
            };

            var documents = (await ResultAsync<StagePreviewResult>(await EnqueueAsync(seed, Payload("chunk")))).Documents;
            documents.Select(d => d.Tags).ShouldBe([["bygglov", "bygga-bo", "ansokan"], []]);

            var cleaned = (await ResultAsync<StagePreviewResult>(await EnqueueAsync(seed, Payload("clean")))).Documents;
            cleaned.ShouldAllBe(d => d.Tags == null, "chunk did not run");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Six_documents_are_refused()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page(name))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var six = await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = seed.VersionId,
                CollectionId = seed.CollectionId,
                DocumentIds = [.. Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7())],
            });

            (await TerminalAsync(six)).ShouldBe(new JobOutcome(JobStates.Failed, StagePreview.LimitDocuments));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_url_list_preflight_reports_a_robots_disallowed_and_an_egress_blocked_url_with_their_reasons()
    {
        await using var site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /p/secret\n"));
            app.MapGet("/p/{name}", (string name) => EngineKit.Html(EngineKit.Page(name)));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var open = site.Url("/p/open").AbsoluteUri;
            var disallowed = site.Url("/p/secret").AbsoluteUri;
            const string Private = "http://10.20.30.40/p/inside";
            var job = await EnqueueAsync(seed, new PreflightPayload
            {
                CollectionId = seed.CollectionId,
                SourceType = "url-list",
                Settings = JsonSerializer.SerializeToElement(new { urls = new[] { open, disallowed, Private } }),
            });
            var result = await ResultAsync<PreflightResult>(job);

            result.State.ShouldBe(PreflightState.Warn);
            result.Checks.ShouldContain(c => c.Check == "robots" && c.State == PreflightState.Warn && c.Code == "robots.disallow" && c.Target == disallowed);
            result.Checks.ShouldContain(c => c.Check == "egress" && c.State == PreflightState.Warn && c.Code == "egress.private_address" && c.Target == Private);
            result.Checks.ShouldContain(c => c.Check == "reachability" && c.State == PreflightState.Pass);
            result.Checks.ShouldContain(c => c.Check == "robots" && c.Code == "robots.ok");
            result.Checks.ShouldNotContain(c => c.Target == open && c.State != PreflightState.Pass);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_crawl_preflight_counts_the_sitemap_pages_against_max_pages()
    {
        TestSite? self = null;
        await using var site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", () => Results.Text($"User-agent: *\nAllow: /\nSitemap: {self!.Url("/sitemap.xml")}\n"));
            app.MapGet("/sitemap.xml", () => Results.Text($"""
                <?xml version="1.0" encoding="UTF-8"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>{self!.Url("/p/one")}</loc></url>
                  <url><loc>{self!.Url("/p/two")}</loc></url>
                  <url><loc>{self!.Url("/p/three")}</loc></url>
                </urlset>
                """, "application/xml"));
            app.MapGet("/p/{name}", (string name) => EngineKit.Html(EngineKit.Page(name)));
        });
        self = site;
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var job = await EnqueueAsync(seed, new PreflightPayload
            {
                CollectionId = seed.CollectionId,
                SourceType = "crawl",
                Settings = JsonSerializer.SerializeToElement(new { startUrls = new[] { site.Url("/p/start").AbsoluteUri }, maxPages = 2 }),
            });
            var result = await ResultAsync<PreflightResult>(job);

            result.EstimatedPages.ShouldBe(4);
            result.State.ShouldBe(PreflightState.Warn);
            result.Checks.ShouldContain(c => c.Check == "pages" && c.State == PreflightState.Warn && c.Code == "crawl.limit_pages");
            result.Checks.ShouldContain(c => c.Check == "sitemaps" && c.State == PreflightState.Pass && c.Code == "sitemap.found");
            result.Checks.ShouldContain(c => c.Check == "reachability" && c.State == PreflightState.Pass && c.Code == "http.200");
            result.Checks.ShouldContain(c => c.Check == "robots" && c.Code == "robots.ok");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private sealed record JobOutcome(string State, string? LastError);

    private Task<Guid> DraftAsync(SourceSeed seed, int chunkTokens) => DraftAsync(seed, $$"""{"chunkTokens":{{chunkTokens}}}""");

    private async Task<Guid> DraftAsync(SourceSeed seed, string parameters)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."PipelineVersions" ("Id", "PipelineId", "Number", "State", "Definition")
            VALUES ($1, $2, (SELECT coalesce(max("Number"), 0) + 1 FROM config."PipelineVersions" WHERE "PipelineId" = $2), 'draft', $3::jsonb)
            """, id, seed.PipelineId, $$"""{"schema":"pipeline/v2","template":"url-list","templateVersion":1,"params":{{parameters}}}""");
        return id;
    }

    /// <summary>A page that sends <c>Last-Modified: Sun, 01 Mar 2026 08:00:00 GMT</c>.</summary>
    private static IResult DatedPage(HttpContext context, string title)
    {
        context.Response.Headers.LastModified = "Sun, 01 Mar 2026 08:00:00 GMT";
        return EngineKit.Html(EngineKit.Page(title));
    }

    /// <summary>Applies <paramref name="profile"/> to the test site's host at the seed's level.</summary>
    private async Task ApplySiteProfileAsync(SourceSeed seed, Pipelines.SiteProfiles.SiteProfile profile)
    {
        var profileId = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "PresetId", "UpdatedAt")
            VALUES ($1, $2, $3, NULL, now())
            """, profileId, seed.Classification, TestSite.Host);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "Profile", "Origin", "State",
                                                       "CreatedBy", "CreatedAt", "AppliedAt", "AppliedBy")
            VALUES ($1, $2, 1, $3::jsonb, 'manual', 'applied', 'test', now(), now(), 'test')
            """, version, profileId, ContractJson.Serialize(profile));
        await Sql.ExecuteAsync(database.Connection(null), """
            UPDATE content."SiteProfiles" SET "AppliedVersionId" = $1 WHERE "Id" = $2
            """, version, profileId);
    }

    /// <summary>A preview job as the admin enqueues it (no dedup).</summary>
    private async Task<Guid> EnqueueAsync<T>(SourceSeed seed, T payload)
    {
        var job = new JobRequest(JobClasses.Preview, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload));
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        return job.Id;
    }

    private async Task<JobOutcome> TerminalAsync(Guid jobId)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var row = await Sql.JobAsync(database.Connection(null), jobId);
            if (row.State is JobStates.Succeeded or JobStates.Failed or JobStates.Dead)
            {
                return new JobOutcome(row.State, row.LastError);
            }

            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90), $"preview job {jobId} is still {row.State}");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<T> ResultAsync<T>(Guid jobId)
    {
        (await TerminalAsync(jobId)).ShouldBe(new JobOutcome(JobStates.Succeeded, null));
        var row = await Sql.JobAsync(database.Connection(null), jobId);
        using var json = JsonDocument.Parse(row.Result!);
        return ContractJson.Deserialize<T>(json.RootElement);
    }

    /// <summary>Row count and an MD5 over the rows' text of every <c>content</c> table but <see cref="Unwatched"/>.</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'content' AND c.relkind = 'r' ORDER BY c.relname
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var lines = new List<string>();
        foreach (var table in tables.Except(Unwatched, StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand(
                $"""SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') FROM content."{table}" t""", connection);
            lines.Add($"{table} {await command.ExecuteScalarAsync(Ct)}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The snapshot once two reads a second apart agree (the run's last writes have settled).</summary>
    private async Task<string> StableSnapshotAsync()
    {
        var previous = await SnapshotAsync();
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000, Ct);
            var current = await SnapshotAsync();
            if (current == previous)
            {
                return current;
            }

            previous = current;
        }

        throw new TimeoutException("The content tables did not settle.");
    }

    /// <summary>Every blob outside <c>previews/</c> and <c>tmp/</c>, with its length.</summary>
    private string[] LiveBlobs() =>
    [
        .. _blobs.EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(_blobs.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f.Length))
            .Where(f => BlobKey.TryCreate(f.Path, out var key) && key.Area is not ("previews" or "tmp"))
            .Select(f => $"{f.Path} {f.Length}")
            .Order(StringComparer.Ordinal),
    ];

    private static async Task<string> TextAsync(IBlobStore blobs, string path)
    {
        await using var stream = await blobs.OpenReadAsync(new BlobKey(path), null, Ct);
        stream.ShouldNotBeNull($"{path} is missing");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    private static async Task<int> CountAsync(IBlobStore blobs, BlobKey prefix)
    {
        var count = 0;
        await foreach (var _ in blobs.ListAsync(prefix, Ct))
        {
            count++;
        }

        return count;
    }
}
