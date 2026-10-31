using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// Source settings-change effects (connectors design §12.3, §12.4): the scheduler compares an idle source's settings hashes
/// with its newest sync run's and enqueues a <c>config-change</c> sync for a scope or identity change, nothing for a
/// behaviour or cosmetic one. Narrowing a crawl's scope (its <c>exclude</c>, or a site profile's <c>excludedTypes</c>)
/// removes the out-of-scope documents <c>scope-narrowed</c> without a request and outside the guard's ratio; widening
/// traverses now; an identity change re-syncs and removes the identities not found again <c>identity-changed</c>; lowered
/// limits delete nothing; a URL list's change claims only the added URLs.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class SettingsEffectsTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-settings-blobs-");
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentDictionary<string, string[]> _links = new(StringComparer.Ordinal);
    private readonly string _tag = Guid.NewGuid().ToString("N");
    private volatile string[] _sitemap = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The worker's own scheduler stays out of the way: the tests run its pass themselves.
    private static readonly Dictionary<string, string?> QuietScheduler = new() { ["Worker:SchedulerSeconds"] = "600" };

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Narrowing_a_crawls_exclude_removes_the_documents_without_a_request_and_widening_traverses_now()
    {
        _links["/"] = ["/p/1", "/p/2", "/p/3", "/p/4", "/p/5", "/p/6", "/q/1"];
        _sitemap = ["/p/1", "/p/2"];
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Crawl(site, exclude: ["**/q/*"]));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).ShouldBe(new RunCounters(RunStates.Succeeded, 7, 0, 0, 0, 0, 0));
            _requests.ShouldNotContain(r => r.Contains("/q/1", StringComparison.Ordinal));
            _requests.Clear();
            await SetSettingsAsync(seed, Crawl(site, exclude: ["**/q/*", "**/p/*"]));

            var narrowing = await ConfigChangeRunAsync(seed);
            var run = await EngineKit.WaitForRunAsync(database, narrowing);

            // Six of seven documents leave the scope: far over the guard's ratio, but exempt, so applied at once.
            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 1, 6, 0, 0));
            _requests.ShouldNotContain(r => r.Contains("/p/", StringComparison.Ordinal), "no request to a now out-of-scope URL, not even a removal check");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(1);
            (await ProposalsAsync(narrowing)).ShouldBe("scope-narrowed:True:applied:6");
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Kind" || '/' || "Trigger" FROM content."Runs" WHERE "Id" = $1""", narrowing))
                .ShouldBe("sync/config-change");

            _requests.Clear();
            await SetSettingsAsync(seed, Crawl(site, exclude: []));
            var widening = await EngineKit.WaitForRunAsync(database, await ConfigChangeRunAsync(seed));

            widening.State.ShouldBe(RunStates.Succeeded);
            _requests.ShouldContain(r => r.EndsWith(" /q/1", StringComparison.Ordinal), "a widened scope is traversed now");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(8);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_site_profile_that_excludes_a_type_removes_the_hosts_documents_of_that_type_without_a_request()
    {
        _links["/"] = ["/p/1", "/filer/a.txt", "/filer/b.txt"];
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Crawl(site, exclude: []));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(4);
            _requests.Clear();
            var edit = new EditSiteProfileCommand
            {
                Host = TestSite.Host,
                Profile = new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main", ExcludedTypes = ["image/*", "video/*", "text/plain"] },
                DecidedBy = "admin-oid",
            };
            var command = new Domain.Ports.JobRequest(JobClasses.Command, seed.DepartmentId, seed.Classification, ContractJson.Serialize(edit));
            (await database.Queue(RagDatabase.Admin).EnqueueAsync([command], null, Ct)).ShouldContain(command.Id);

            var followUp = await RunOfAsync(seed, RunTriggers.SiteProfile);
            var run = await EngineKit.WaitForRunAsync(database, followUp);

            run.Deleted.ShouldBe(2);
            run.ProposedDeletions.ShouldBe(0);
            _requests.ShouldNotContain(r => r.Contains("/filer/", StringComparison.Ordinal), "a document of a newly excluded type is removed without a request");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(2);
            (await ProposalsAsync(followUp)).ShouldBe("scope-narrowed:True:applied:2");
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Kind" FROM content."Runs" WHERE "Id" = $1""", followUp)).ShouldBe(RunKinds.Sync);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            // The key has no port, so the profile would reach every later test on the test site.
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."SiteProfiles" WHERE "Classification" = $1 AND "Host" = $2""",
                seed.Classification, TestSite.Host);
        }
    }

    [Fact]
    public async Task An_identity_change_re_syncs_and_removes_the_identities_not_found_again_as_identity_changed()
    {
        _links["/"] = [.. Enumerable.Range(1, 6).Select(i => $"/p/{i}?visning={i}")];
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Crawl(site, exclude: []));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(7);
            await SetSettingsAsync(seed, Crawl(site, exclude: [], ignoreQueryParams: ["visning"]));

            var runId = await ConfigChangeRunAsync(seed);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            // The six pages come back under their new identities; the old ones are exempt removals, not a tripped guard.
            run.ShouldBe(new RunCounters(RunStates.Succeeded, 6, 0, 1, 6, 0, 0));
            (await ProposalsAsync(runId)).ShouldBe("identity-changed:True:applied:6");
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL AND "CanonicalIdentity" LIKE '%visning%'
                """, seed.SourceId)).ShouldBe(0);
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Report" -> 'mode' ->> 'revalidate' FROM content."Runs" WHERE "Id" = $1""", runId))
                .ShouldBe("true", "an identity re-sync fetches every URL");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Lowering_max_depth_or_max_pages_deletes_nothing()
    {
        _links["/"] = ["/p/1", "/p/2", "/p/3"];
        _links["/p/1"] = ["/d/deep"];
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Crawl(site, exclude: []));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(5);
            await SetSettingsAsync(seed, Crawl(site, exclude: [], maxDepth: 1));

            // A complete traversal that no longer reaches the deeper page.
            var depth = await EngineKit.WaitForRunAsync(database, await ConfigChangeRunAsync(seed));

            depth.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 4, 0, 0, 0));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(5);

            await SetSettingsAsync(seed, Crawl(site, exclude: [], maxDepth: 1, maxPages: 2));
            var pages = await EngineKit.WaitForRunAsync(database, await ConfigChangeRunAsync(seed));

            pages.Deleted.ShouldBe(0);
            pages.ProposedDeletions.ShouldBe(0);
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(5);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_behaviour_or_cosmetic_change_enqueues_nothing_and_a_scope_change_enqueues_one_config_change_sync()
    {
        var seed = await EngineKit.SeedCrawlAsync(database, new { startUrls = new[] { "https://www.example.org/" } });
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        try
        {
            var run = await CommandSeeds.RunAsync(database, seed, RunStates.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-5));
            await Sql.ExecuteAsync(database.Connection(null), """
                UPDATE content."Runs" r SET "SettingsHashes" = $2::jsonb WHERE r."Id" = $1
                """, run, SettingsEffects.HashesJson(SourceTypes.Crawl, JsonSerializer.SerializeToElement(new { startUrls = new[] { "https://www.example.org/" } }))!);
            var scheduler = Scheduler();
            (await scheduler.TickAsync(Ct)).ShouldBe(0, "unchanged settings");

            // noiseSelectors and revalidateEvery are behaviour, demote and crawlWindow cosmetic.
            await SetSettingsAsync(seed, new
            {
                startUrls = new[] { "https://www.example.org/" },
                noiseSelectors = new[] { ".banner" },
                revalidateEvery = 7,
                demote = new[] { new { pattern = "**/arkiv/**", factor = 0.5 } },
                crawlWindow = "20:00-05:00",
            });

            (await scheduler.TickAsync(Ct)).ShouldBe(0, "a behaviour or cosmetic change waits for the next sync");
            (await CommandSeeds.SyncJobsAsync(database, seed.SourceId)).ShouldBe(0);

            await SetSettingsAsync(seed, new { startUrls = new[] { "https://www.example.org/" }, exclude = new[] { "**/sok?*" } });

            (await scheduler.TickAsync(Ct)).ShouldBe(1);
            (await scheduler.TickAsync(Ct)).ShouldBe(0, "the queued sync holds the source's key");
            var payload = await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Payload"::text FROM content."Jobs" WHERE "Class" = 'sync' AND "Payload" ->> 'sourceId' = $1
                """, seed.SourceId.ToString());
            using var json = JsonDocument.Parse(payload);
            var sync = ContractJson.Deserialize<SyncPayload>(json.RootElement);
            (sync.Kind, sync.Trigger, sync.RunId).ShouldBe((SyncKind.Sync, RunTrigger.ConfigChange, (Guid?)null));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_url_list_change_claims_only_the_added_url_and_removes_the_removed_one_at_once()
    {
        var hits = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await using var site = await EngineKit.SiteAsync((name, _) =>
        {
            hits.AddOrUpdate(name, 1, (_, n) => n + 1);
            return Task.FromResult(EngineKit.Html(EngineKit.Page($"sophämtning {name} {_tag}")));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            await EngineKit.SetUrlsAsync(database, seed, [site.Url("/p/a"), site.Url("/p/c")]);

            var runId = await ConfigChangeRunAsync(seed);
            var run = await EngineKit.WaitForRunAsync(database, runId);

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 1, 0, 0));
            hits["a"].ShouldBe(1, "a URL still listed is not fetched again");
            hits["c"].ShouldBe(1);
            (await ProposalsAsync(runId)).ShouldBe("removed-from-list:True:applied:1");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(2);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private static object Crawl(TestSite site, string[] exclude, string[]? ignoreQueryParams = null, int maxDepth = 10, int maxPages = 100) => new
    {
        startUrls = new[] { site.Url("/").AbsoluteUri },
        exclude,
        ignoreQueryParams = ignoreQueryParams ?? [],
        maxDepth,
        maxPages,
        // Always open: config-change and site-profile runs keep the night window (§6.5).
        crawlWindow = "00:00-00:00",
    };

    private SyncScheduler Scheduler()
    {
        var db = database.Database(RagDatabase.Worker);
        return new SyncScheduler(db, database.Queue(RagDatabase.Worker), new EngineSettings(db, TimeProvider.System, NullLogger<EngineSettings>.Instance),
            Options.Create(new WorkerOptions()), TimeProvider.System, NullLogger<SyncScheduler>.Instance);
    }

    private Task SetSettingsAsync(SourceSeed seed, object settings) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = $2::jsonb WHERE "Id" = $1""", seed.SourceId,
            JsonSerializer.Serialize(settings));

    /// <summary>The scheduler's pass enqueues the source's config-change sync; returns its run (the job's id).</summary>
    private async Task<Guid> ConfigChangeRunAsync(SourceSeed seed)
    {
        (await Scheduler().TickAsync(Ct)).ShouldBe(1);
        return await Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Jobs"
            WHERE "Class" = 'sync' AND "RunId" IS NULL AND "Payload" ->> 'sourceId' = $1 AND "Payload" ->> 'trigger' = 'config-change'
            ORDER BY "Id" DESC LIMIT 1
            """, seed.SourceId.ToString());
    }

    /// <summary>The source's run with <paramref name="trigger"/>, once it was opened.</summary>
    private async Task<Guid> RunOfAsync(SourceSeed seed, string trigger)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            var id = await Sql.ScalarAsync<Guid>(database.Connection(null), """
                SELECT "Id" FROM content."Runs" WHERE "SourceId" = $1 AND "Trigger" = $2 ORDER BY "StartedAt" DESC LIMIT 1
                """, seed.SourceId, trigger);
            if (id != Guid.Empty)
            {
                return id;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"No {trigger} run of source {seed.SourceId} was opened.");
    }

    /// <summary>The run's deletion proposals as <c>reason:exempt:decision:count</c>, one group per line.</summary>
    private Task<string> ProposalsAsync(Guid runId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg(g, E'\n' ORDER BY g) FROM (
                SELECT "Reason" || ':' || CASE WHEN "Exempt" THEN 'True' ELSE 'False' END || ':' || "Decision" || ':' || count(*) AS g
                FROM content."DeletionProposals" WHERE "RunId" = $1 GROUP BY "Reason", "Exempt", "Decision") x
            """, runId);

    private async Task<TestSite> SiteAsync() => await TestSite.StartAsync(app =>
    {
        app.Use(async (context, next) =>
        {
            _requests.Enqueue(context.Request.Method + " " + context.Request.Path + context.Request.QueryString);
            await next(context);
        });
        app.MapGet("/robots.txt", (HttpContext context) =>
            Results.Text($"User-agent: *\nAllow: /\nSitemap: http://{TestSite.Host}:{context.Request.Host.Port}/sitemap.xml\n", "text/plain"));
        app.MapGet("/sitemap.xml", (HttpContext context) => Results.Content(
            """<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""" +
            string.Concat(_sitemap.Select(u => $"<url><loc>http://{TestSite.Host}:{context.Request.Host.Port}{u}</loc><lastmod>2026-01-15</lastmod></url>")) + "</urlset>",
            "application/xml"));
        app.MapGet("/", () => Page("/"));
        app.MapGet("/{**path}", (string path) => path.EndsWith(".txt", StringComparison.Ordinal)
            ? Results.Text($"Regler för {path} {_tag}\n\nAnsökan om bygglov görs i e-tjänsten. Handläggaren granskar ritningar och situationsplan.",
                "text/plain; charset=utf-8")
            : Page("/" + path));
    });

    private IResult Page(string path)
    {
        var links = string.Concat((_links.GetValueOrDefault(path) ?? []).Select(l => $"""<a href="{l}">Länk {l}</a> """));
        return EngineKit.Html(EngineKit.Page($"bygglov {path} {_tag}", links));
    }
}
