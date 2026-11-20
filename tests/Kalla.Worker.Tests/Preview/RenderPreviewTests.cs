using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.TestKit;
using Kalla.Worker.Connectors.Render;
using Kalla.Worker.Preview;
using Kalla.Worker.Tests.Connectors;
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Preview;

/// <summary>
/// The renderer in the previews on a fake renderer (connectors design §8.1; admin design §4.7, §5.5.3): the preflight renders
/// the first HTML pages through the production fetch path and compares their raw and rendered main-text words, suggesting the
/// browser when the raw page holds under half and warning when the rendered page has (almost) no main text; without a renderer
/// it says so once; the stage preview shows the fetch mode.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class RenderPreviewTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly Dictionary<string, string?> Renderer = new()
    {
        ["Worker:SchedulerSeconds"] = "600",
        ["Render:Endpoint"] = RenderTestKit.Options().Endpoint,
    };

    /// <summary>A page whose main text is 61 words: a heading and 60 words of body.</summary>
    private static readonly string FullPage =
        $"<!doctype html><html lang=\"sv\"><head><title>Bygglov</title></head><body><main><h1>Bygglov</h1><p>{string.Join(' ', Enumerable.Repeat("bygglov", 60))}</p></main></body></html>";

    /// <summary>A client-rendered shell: the content arrives by script.</summary>
    private const string Shell =
        "<!doctype html><html lang=\"sv\"><head><title>Bygglov</title><script src=\"/app.js\"></script></head><body><main><div id=\"app\">Laddar</div></main></body></html>";

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-render-preview-blobs-");
    private readonly FakeRenderer _renderer = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _renderer.Render = (request, _) => Task.FromResult(request.Url.AbsolutePath == "/p/slow" ? FakeRenderer.NotReady() : FakeRenderer.Page(FullPage));
    }

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_url_list_preflight_compares_raw_and_rendered_words_and_suggests_the_browser_for_a_shell()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(name == "shell" ? Shell : FullPage)));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            string[] urls = [site.Url("/p/shell").AbsoluteUri, site.Url("/p/full").AbsoluteUri, site.Url("/p/slow").AbsoluteUri, site.Url("/p/fourth").AbsoluteUri];
            var result = await ResultAsync<PreflightResult>(await EnqueueAsync(seed, new PreflightPayload
            {
                CollectionId = seed.CollectionId,
                SourceType = "url-list",
                Settings = JsonSerializer.SerializeToElement(new { urls, readySelector = "#app h1", scrollToLoad = true }),
            }));

            var render = result.Checks.Where(c => c.Check == Preflight.Render).ToList();
            render.Select(c => c.Target).ShouldBe([urls[0], urls[1], urls[2]], "the first three HTML pages are rendered, no more");
            var shell = render[0];
            (shell.Code, shell.State).ShouldBe((Preflight.RenderSuggested, PreflightState.Warn), "the form still fetches with http");
            shell.Facts!.Value.GetProperty("rawWords").GetInt32().ShouldBeLessThan(5);
            shell.Facts.Value.GetProperty("renderedWords").GetInt32().ShouldBe(61);
            var full = render[1];
            (full.Code, full.State).ShouldBe((Preflight.RenderCompared, PreflightState.Pass));
            (full.Facts!.Value.GetProperty("rawWords").GetInt32(), full.Facts.Value.GetProperty("renderedWords").GetInt32()).ShouldBe((61, 61));
            (render[2].Code, render[2].State, render[2].Facts).ShouldBe((RenderReasons.Timeout, PreflightState.Pass, (JsonElement?)null));
            result.State.ShouldBe(PreflightState.Warn);
            result.Checks.ShouldContain(c => c.Check == "reachability" && c.State == PreflightState.Pass);

            // The render goes through the production path with the form's readySelector and scrollToLoad.
            var shellRender = _renderer.Requests.First(r => r.Url.AbsolutePath == "/p/shell");
            (shellRender.ReadySelector, shellRender.ScrollToLoad).ShouldBe(("#app h1", true));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_render_with_an_empty_main_text_warns_to_name_the_loaded_content()
    {
        // A-13: a single-page app declared ready before its data arrived rendered an empty <main>, reported as render.compared.
        _renderer.Render = (_, _) => Task.FromResult(FakeRenderer.Page(
            "<!doctype html><html lang=\"sv\"><head><title>Bygglov</title></head><body><main></main></body></html>"));
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(Shell)));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            string[] startUrls = [site.Url("/p/app").AbsoluteUri];
            var result = await ResultAsync<PreflightResult>(await EnqueueAsync(seed, new PreflightPayload
            {
                CollectionId = seed.CollectionId,
                SourceType = "crawl",
                Settings = JsonSerializer.SerializeToElement(new { startUrls, fetchMode = "browser" }),
            }));

            var render = result.Checks.Where(c => c.Check == Preflight.Render).ShouldHaveSingleItem();
            (render.Code, render.State, render.Target).ShouldBe((Preflight.RenderEmpty, PreflightState.Warn, startUrls[0]));
            (render.Facts!.Value.GetProperty("rawWords").GetInt32(), render.Facts.Value.GetProperty("renderedWords").GetInt32()).ShouldBe((1, 0));
            result.State.ShouldBe(PreflightState.Warn);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_ready_selector_the_browser_refuses_or_never_finds_is_the_render_entrys_reason()
    {
        // #488, #490: render.timeout sent the admin to the site; the reason names the setting.
        _renderer.Render = (request, _) => Task.FromResult(FakeRenderer.NotReady(request.Url.AbsolutePath == "/p/bad" ? RenderReasons.BadSelector : RenderReasons.SelectorNotFound));
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(Shell)));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            string[] urls = [site.Url("/p/bad").AbsoluteUri, site.Url("/p/none").AbsoluteUri];
            var result = await ResultAsync<PreflightResult>(await EnqueueAsync(seed, new PreflightPayload
            {
                CollectionId = seed.CollectionId,
                SourceType = "url-list",
                Settings = JsonSerializer.SerializeToElement(new { urls, fetchMode = "browser", readySelector = "<h1>" }),
            }));

            result.Checks.Where(c => c.Check == Preflight.Render).Select(c => (c.Code, c.State, c.Target, c.Facts)).ShouldBe([
                (RenderReasons.BadSelector, PreflightState.Pass, urls[0], (JsonElement?)null),
                (RenderReasons.SelectorNotFound, PreflightState.Pass, urls[1], (JsonElement?)null),
            ]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_browser_crawl_preflight_does_not_suggest_and_without_a_renderer_the_comparison_says_so_once()
    {
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(Shell)));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        string[] startUrls = [site.Url("/p/one").AbsoluteUri, site.Url("/p/two").AbsoluteUri];
        PreflightPayload Payload(string fetchMode) => new()
        {
            CollectionId = seed.CollectionId,
            SourceType = "crawl",
            Settings = JsonSerializer.SerializeToElement(new { startUrls, fetchMode }),
        };

        await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
                         services: s => s.AddSingleton<IRenderer>(_renderer)))
        {
            var browser = await ResultAsync<PreflightResult>(await EnqueueAsync(seed, Payload("browser")));
            var render = browser.Checks.Where(c => c.Check == Preflight.Render).ToList();
            render.Count.ShouldBe(2);
            render.ShouldAllBe(c => c.Code == Preflight.RenderSuggested && c.State == PreflightState.Pass, "the form already fetches with a browser");
        }

        await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: new Dictionary<string, string?> { ["Worker:SchedulerSeconds"] = "600" }))
        {
            var result = await ResultAsync<PreflightResult>(await EnqueueAsync(seed, Payload("http")));
            var render = result.Checks.Where(c => c.Check == Preflight.Render).ShouldHaveSingleItem();
            (render.Code, render.State, render.Target, render.Facts).ShouldBe((RenderReasons.NotConfigured, PreflightState.Pass, (string?)null, (JsonElement?)null));
        }

        await EngineKit.PauseAsync(database, seed);
    }

    [Fact]
    public async Task The_stage_preview_shows_the_fetch_mode_of_the_page()
    {
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(Shell)));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = $2::jsonb WHERE "Id" = $1""", seed.SourceId,
            JsonSerializer.Serialize(new { urls = new[] { site.Url("/p/a").AbsoluteUri }, fetchMode = "browser" }));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: Renderer,
            services: s => s.AddSingleton<IRenderer>(_renderer));
        try
        {
            var result = await ResultAsync<StagePreviewResult>(await EnqueueAsync(seed, new StagePreviewPayload
            {
                PipelineVersionId = seed.VersionId,
                CollectionId = seed.CollectionId,
                SourceId = seed.SourceId,
                Urls = [site.Url("/p/b").AbsoluteUri],
                UntilStage = "parse",
            }));

            var fetch = result.Documents.ShouldHaveSingleItem().Stages.Single(s => s.Stage == "fetch");
            fetch.State.ShouldBe(PreviewStageState.Succeeded);
            fetch.Facts!.Value.GetProperty("fetchMode").GetString().ShouldBe(FetchModes.Browser);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>A preview job as the admin enqueues it (no dedup).</summary>
    private async Task<Guid> EnqueueAsync<T>(SourceSeed seed, T payload)
    {
        var job = new JobRequest(JobClasses.Preview, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload));
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        return job.Id;
    }

    private async Task<T> ResultAsync<T>(Guid jobId)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var row = await Sql.JobAsync(database.Connection(null), jobId);
            if (row.State is JobStates.Succeeded or JobStates.Failed or JobStates.Dead)
            {
                (row.State, row.LastError).ShouldBe((JobStates.Succeeded, (string?)null));
                using var json = JsonDocument.Parse(row.Result!);
                return ContractJson.Deserialize<T>(json.RootElement);
            }

            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90), $"preview job {jobId} is still {row.State}");
            await Task.Delay(100, Ct);
        }
    }
}
