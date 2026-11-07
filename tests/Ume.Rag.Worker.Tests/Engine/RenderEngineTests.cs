using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors.Render;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// <c>browser</c> sources through the engine on a fake renderer (connectors design §8, §12.3; data-model §2.2): a document's
/// <c>FetchMode</c> says how its content was fetched, a <c>fetchMode</c> change makes the scheduler start a sync that fetches
/// every listed URL again in the new mode, and a crawl's report counts its rendered pages, those with an empty main text among them.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class RenderEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly Dictionary<string, string?> Renderer = new()
    {
        ["Worker:SchedulerSeconds"] = "600",
        ["Render:Endpoint"] = RenderTestKit.Options().Endpoint,
    };

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-render-blobs-");
    private readonly string _tag = Guid.NewGuid().ToString("N");
    private readonly FakeRenderer _renderer = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _renderer.Render = (request, _) => Task.FromResult(FakeRenderer.Page(EngineKit.Page($"renderad {request.Url.AbsolutePath} {_tag}")));
    }

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_fetch_mode_change_fetches_every_listed_url_again_and_stores_fetch_mode_browser()
    {
        var hits = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await using var site = await EngineKit.SiteAsync((name, _) =>
        {
            hits.AddOrUpdate(name, 1, (_, n) => n + 1);
            return Task.FromResult(EngineKit.Html(EngineKit.Page($"skal {name} {_tag}")));
        });
        string[] urls = [site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri];
        var seed = await EngineKit.SeedUrlListAsync(database, urls.Select(u => new Uri(u)));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            (await FetchModesAsync(seed)).ShouldBe([FetchModes.Http, FetchModes.Http]);
            _renderer.Requests.ShouldBeEmpty();
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = $2::jsonb WHERE "Id" = $1""", seed.SourceId,
                JsonSerializer.Serialize(new { urls, fetchMode = "browser" }));

            var run = await EngineKit.WaitForRunAsync(database, await ConfigChangeRunAsync(seed));

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 2, 0, 0, 0, 0));
            (hits["a"], hits["b"]).ShouldBe((2, 2), "a config-change run re-fetches every listed URL when fetchMode changed");
            _renderer.Requests.Select(r => r.Url.AbsolutePath).Order(StringComparer.Ordinal).ShouldBe(["/p/a", "/p/b"]);
            (await FetchModesAsync(seed)).ShouldBe([FetchModes.Browser, FetchModes.Browser]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_browser_crawl_reports_its_rendered_pages()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"skal {name} {_tag}"))));
        var seed = await EngineKit.SeedCrawlAsync(database, new
        {
            startUrls = new[] { site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri },
            exclude = Array.Empty<string>(),
            crawlWindow = "00:00-00:00",
            fetchMode = "browser",
        });
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            var runId = await EngineKit.SyncNowAsync(database, seed);
            (await EngineKit.WaitForRunAsync(database, runId)).Added.ShouldBe(2);

            (await FetchModesAsync(seed)).ShouldBe([FetchModes.Browser, FetchModes.Browser]);
            using var report = JsonDocument.Parse(await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Report"::text FROM content."Runs" WHERE "Id" = $1""", runId));
            var render = report.RootElement.GetProperty("render");
            (render.GetProperty("rendered").GetInt32(), render.GetProperty("empty").GetInt32(), render.GetProperty("failures").GetInt32(),
                    render.GetProperty("keptFromLastGood").GetInt32())
                .ShouldBe((2, 0, 0, 0));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_browser_crawl_counts_a_ready_render_with_an_empty_main_text_as_rendered_and_empty()
    {
        // A-13: a single-page app declared ready before its data arrived stored an empty <main> (skipped parse.empty), and
        // render.rendered left it out.
        _renderer.Render = (request, _) => Task.FromResult(FakeRenderer.Page(request.Url.AbsolutePath == "/p/b"
            ? "<!doctype html><html lang=\"sv\"><head><title>Företag</title></head><body><main></main></body></html>"
            : EngineKit.Page($"renderad {request.Url.AbsolutePath} {_tag}")));
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"skal {name} {_tag}"))));
        var seed = await EngineKit.SeedCrawlAsync(database, new
        {
            startUrls = new[] { site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri },
            exclude = Array.Empty<string>(),
            crawlWindow = "00:00-00:00",
            fetchMode = "browser",
        });
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            var runId = await EngineKit.SyncNowAsync(database, seed);
            (await EngineKit.WaitForRunAsync(database, runId)).Added.ShouldBe(1);

            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Outcome" || ' ' || "Reason" FROM content."RunItems" WHERE "RunId" = $1 AND "Identity" LIKE '%/p/b'
                """, runId)).ShouldBe("skipped parse.empty");
            using var report = JsonDocument.Parse(await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Report"::text FROM content."Runs" WHERE "Id" = $1""", runId));
            var render = report.RootElement.GetProperty("render");
            (render.GetProperty("rendered").GetInt32(), render.GetProperty("empty").GetInt32(), render.GetProperty("failures").GetInt32(),
                    render.GetProperty("keptFromLastGood").GetInt32())
                .ShouldBe((2, 1, 0, 0));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_slow_render_does_not_raise_the_hosts_latency()
    {
        // A-13: the render's time (subrequests, their pacing, the ready rule) is not the host's response time; fed into
        // LatencyEwmaMs it lengthened every later subrequest's interval until each page ran past Render:PageTimeout.
        _renderer.Render = async (request, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
            return FakeRenderer.Page(EngineKit.Page($"renderad {request.Url.AbsolutePath} {_tag}"));
        };
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"skal {name} {_tag}"))));
        var seed = await EngineKit.SeedCrawlAsync(database, new
        {
            startUrls = new[] { site.Url("/p/a").AbsoluteUri, site.Url("/p/b").AbsoluteUri },
            exclude = Array.Empty<string>(),
            crawlWindow = "00:00-00:00",
            fetchMode = "browser",
        });
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);

            _renderer.Requests.Count.ShouldBe(2);
            var host = site.Url("/").Authority;
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."CrawlHosts" WHERE "Host" = $1""", host)).ShouldBe(1);
            var latency = await Sql.ScalarAsync<int?>(database.Connection(null), """SELECT "LatencyEwmaMs" FROM content."CrawlHosts" WHERE "Host" = $1""", host);
            (latency ?? 0).ShouldBeLessThan(1000, "the host's latency is its responses' time, not the 1.5 s renders");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private async Task<string[]> FetchModesAsync(SourceSeed seed) =>
        [.. (await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg("FetchMode", ',' ORDER BY "CanonicalIdentity") FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL
            """, seed.SourceId)).Split(',')];

    /// <summary>The scheduler's pass enqueues the source's config-change sync; returns its run (the job's id).</summary>
    private async Task<Guid> ConfigChangeRunAsync(SourceSeed seed)
    {
        var db = database.Database(RagDatabase.Worker);
        var scheduler = new SyncScheduler(db, database.Queue(RagDatabase.Worker), new EngineSettings(db, TimeProvider.System, NullLogger<EngineSettings>.Instance),
            Options.Create(new WorkerOptions()), TimeProvider.System, NullLogger<SyncScheduler>.Instance);
        (await scheduler.TickAsync(Ct)).ShouldBe(1);
        return await Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Jobs"
            WHERE "Class" = 'sync' AND "RunId" IS NULL AND "Payload" ->> 'sourceId' = $1 AND "Payload" ->> 'trigger' = 'config-change'
            ORDER BY "Id" DESC LIMIT 1
            """, seed.SourceId.ToString());
    }
}
