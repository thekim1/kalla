using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Render;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// The image named by <c>KALLA_TEST_RENDERER_IMAGE</c> (built from <c>images/renderer</c>), run as Compose runs it (user
/// 10002, read-only root, <c>/tmp</c> as tmpfs, no capabilities, no new privileges, <c>images/renderer/seccomp.json</c>,
/// an init process) with a secret WebSocket path, on a network of its own (10.215.0.0/24) that holds nothing else: the
/// fixture site is this process's loopback, which the browser cannot reach, so every request it makes has to come through
/// the worker. Without the variable nothing starts and the tests skip. Use a name with a registry part that has a dot (for
/// example <c>kalla.local/renderer:ci</c>), or one that exists locally: Testcontainers prefixes other names with the
/// Docker Hub mirror CI sets.
/// </summary>
public sealed class RendererContainer : IAsyncLifetime
{
    public const string ImageVariable = "KALLA_TEST_RENDERER_IMAGE";
    public const string Subnet = "10.215.0.0/24";
    private const int Port = 3000;

    private INetwork? _network;
    private IContainer? _container;

    public static string? Image => Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : null;

    /// <summary>The secret path, as <c>init-deployment.sh</c> makes one.</summary>
    public string WsPath { get; } = "/" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public string Endpoint => $"ws://{_container!.Hostname}:{_container.GetMappedPublicPort(Port)}{WsPath}";

    public void RequireImage() => Assert.SkipWhen(Image is null, $"{ImageVariable} is not set (build images/renderer and name the image).");

    public async ValueTask InitializeAsync()
    {
        if (Image is not { } image)
        {
            return;
        }

        var seccomp = await File.ReadAllTextAsync(Path.Join(Processing.Fixtures.Root, "images", "renderer", "seccomp.json"), TestContext.Current.CancellationToken);
        _network = new NetworkBuilder()
            .WithName("kalla-render-test-" + Guid.NewGuid().ToString("N")[..8])
            .WithCreateParameterModifier(parameters => parameters.IPAM = new IPAM { Config = [new IPAMConfig { Subnet = Subnet }] })
            .Build();
        await _network.CreateAsync(TestContext.Current.CancellationToken);
        _container = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithPortBinding(Port, true)
            .WithEnvironment("KALLA_RENDER_WS_PATH", WsPath)
            .WithTmpfsMount("/tmp")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.User = "10002:10002";
                parameters.HostConfig ??= new();
                parameters.HostConfig.ReadonlyRootfs = true;
                parameters.HostConfig.CapDrop = ["ALL"];
                // The CLI's --security-opt seccomp=<file> sends the file's content.
                parameters.HostConfig.SecurityOpt = ["no-new-privileges", "seccomp=" + seccomp];
                parameters.HostConfig.Init = true;
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }
}

/// <summary>
/// A fixture SPA rendered by the real <c>kalla-renderer</c> through the worker (connectors design §8.3, A-11 done-when): the
/// page's script, its data API and its cookie come through the worker's guard, a request to a private address is refused
/// with <c>Purpose = Render</c>, images are not fetched, and the API's text is in the stored raw HTML without scripts.
/// </summary>
public sealed class RendererContainerTests(RendererContainer renderer) : IClassFixture<RendererContainer>, IAsyncLifetime
{
    private const string Shell = """
        <!doctype html><html><head><title>Nyheter</title></head><body><main><div id="app">Laddar…</div><img src="/bild.png"></main>
        <script src="/app.js"></script></body></html>
        """;

    private const string App = """
        fetch('http://10.215.0.9/hemligt').catch(() => {});
        fetch('/api/news').then(r => r.json()).then(news => {
          document.getElementById('app').innerHTML = '<article><h1>' + news.title + '</h1><a href="/nyhet/1">Läs mer</a></article>';
        });
        """;

    private readonly ConcurrentQueue<string> _hits = new();
    private TestSite _site = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _site = await TestSite.StartAsync(Map);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_fixture_spa_is_rendered_through_the_workers_guard_and_its_api_text_is_stored()
    {
        renderer.RequireImage();
        var blocked = new ConcurrentQueue<(string Reason, string Purpose)>();
        using var listener = Listen(blocked);
        var options = new RenderOptions { Endpoint = renderer.Endpoint };
        await using var playwright = new PlaywrightRenderer(Options.Create(options), TimeProvider.System, NullLogger<PlaywrightRenderer>.Instance);
        await using var path = _site.Fetch(renderer: playwright, render: options);
        var settings = ContractJson.ToElement(new UrlListSettings { Urls = [_site.Url("/").AbsoluteUri], FetchMode = FetchMode.Browser });
        var url = _site.Url("/");

        var outcome = await path.Connector.FetchAsync(new FetchContext(Guid.NewGuid(), "public", Guid.NewGuid(), settings, Revalidate: false),
            new WorkItem(IdentityKind.Url, url.AbsoluteUri, url, 0, WorkHints.None), Ct);

        var content = outcome.ShouldBeOfType<FetchOutcome.Content>();
        content.Facts.FetchMode.ShouldBe(FetchModes.Browser);
        await using var stream = await path.Blobs.OpenReadAsync(content.Raw.Key, null, Ct);
        using var reader = new StreamReader(stream!);
        var html = await reader.ReadToEndAsync(Ct);
        html.ShouldContain("Nyheter från API");
        html.ShouldNotContain("Laddar");
        html.ShouldNotContain("<script");
        _hits.ShouldContain("GET /app.js");
        _hits.ShouldContain("GET /api/news");
        _hits.ShouldNotContain("GET /bild.png", "images are not fetched");
        blocked.ShouldContain((EgressGuard.PrivateAddress, "render"), "the page's request to a private address is refused by the guard (5101, Purpose = Render)");
    }

    private static MeterListener Listen(ConcurrentQueue<(string Reason, string Purpose)> blocked)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "rag.egress.blocked")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(t => t.Key, t => t.Value as string);
            blocked.Enqueue((values["reason"]!, values["purpose"]!));
        });
        listener.Start();
        return listener;
    }

    private void Map(WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            _hits.Enqueue($"{http.Request.Method} {http.Request.Path}");
            await next();
        });
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nAllow: /\n"));
        app.MapGet("/", (HttpContext http) =>
        {
            http.Response.Headers.SetCookie = "session=1; Path=/";
            return Results.Content(Shell, "text/html; charset=utf-8");
        });
        app.MapGet("/app.js", () => Results.Text(App, "text/javascript"));
        // The data API needs the shell's cookie: the browser's Cookie header comes through the worker (§8.3).
        app.MapGet("/api/news", (HttpContext http) => http.Request.Cookies["session"] == "1"
            ? Results.Json(new { title = "Nyheter från API" })
            : Results.StatusCode(StatusCodes.Status401Unauthorized));
        app.MapGet("/bild.png", () => Results.Bytes([0x89, 0x50], "image/png"));
    }
}
