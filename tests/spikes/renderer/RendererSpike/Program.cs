using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using RendererSpike;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Egress;

// Renderer spike (A-9). Commands: checks | measure [concurrency] | lightpanda | versions. See README.md.
var command = args.FirstOrDefault() ?? "checks";
var endpoint = Environment.GetEnvironmentVariable("RENDER_ENDPOINT") ?? "ws://renderer:3000/spike-7f3c9a";
var launchOptions = Environment.GetEnvironmentVariable("RENDER_LAUNCH_OPTIONS") ?? """{"chromiumSandbox":true}""";

var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
var guard = new EgressGuard(
    Options.Create(new CrawlOptions { AllowedPrivateRanges = Environment.GetEnvironmentVariable("SPIKE_ALLOWED_RANGES") }),
    new SystemDnsResolver(),
    new StaticPolicy(),
    new ConnectorMetrics(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
using var client = new CrawlHttpClient(guard);
var fetcher = new RenderFetcher(client, guard);
var cache = new AssetCache();
var renderer = new PageRenderer(fetcher, cache);

switch (command)
{
    case "versions":
        await VersionsAsync();
        return;
    case "checks":
        await ChecksAsync();
        return;
    case "measure":
        await MeasureAsync(args.Skip(1).Select(int.Parse).DefaultIfEmpty(1).ToArray());
        return;
    case "smoke":
        await SmokeAsync();
        return;
    case "lightpanda":
        await LightpandaAsync();
        return;
    default:
        Console.Error.WriteLine("unknown command " + command);
        Environment.ExitCode = 2;
        return;
}

async Task<IBrowser> ConnectAsync(IPlaywright playwright)
{
    var clock = Stopwatch.StartNew();
    var browser = await playwright.Chromium.ConnectAsync(endpoint, new BrowserTypeConnectOptions
    {
        Headers = new Dictionary<string, string> { ["x-playwright-launch-options"] = launchOptions },
        Timeout = 30_000,
    });
    Console.WriteLine($"connected to {endpoint} (browser {browser.Version}) in {clock.ElapsedMilliseconds} ms, launch options {launchOptions}");
    return browser;
}

async Task<SubResponse> MainAsync(Uri url) =>
    await fetcher.FetchAsync(new SubRequest("GET", url, [KeyValuePair.Create("accept", "text/html")], null, "document"), CancellationToken.None);

async Task<RenderResult> RenderUrlAsync(IBrowser browser, string url, RenderOptions options, bool newContext = true)
{
    var uri = new Uri(url);
    return await renderer.RenderAsync(browser, uri, await MainAsync(uri), options, newContext);
}

static void Check(string name, bool pass, string evidence) =>
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}: {evidence}");

static string Has(string text, params string[] needles) =>
    string.Join(", ", needles.Select(n => (text.Contains(n, StringComparison.Ordinal) ? "+" : "-") + n));

static bool HasAll(string text, params string[] needles) => needles.All(n => text.Contains(n, StringComparison.Ordinal));

async Task VersionsAsync()
{
    // The server compares major.minor of the client's "Playwright/x.y.z" user agent (playwrightServer.ts) and answers 428.
    foreach (var ua in new[] { "Playwright/1.64.1 (x64; ubuntu 24.04) dotnet/10.0", "Playwright/1.64.9 (spike)", "Playwright/1.63.0 (spike)", "Mozilla/5.0 (a page)" })
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", ua);
        try
        {
            await ws.ConnectAsync(new Uri(endpoint), CancellationToken.None);
            Console.WriteLine($"UA '{ua}': upgrade accepted ({ws.State})");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (WebSocketException e)
        {
            Console.WriteLine($"UA '{ua}': refused ({e.Message})");
        }
    }

    using var wrongPath = new ClientWebSocket();
    try
    {
        await wrongPath.ConnectAsync(new Uri(new Uri(endpoint), "/"), CancellationToken.None);
        Console.WriteLine("path '/': upgrade accepted");
    }
    catch (WebSocketException e)
    {
        Console.WriteLine($"path '/': refused ({e.Message})");
    }
}

async Task ChecksAsync()
{
    using var playwright = await Playwright.CreateAsync();
    var browser = await ConnectAsync(playwright);

    // 2. Every resource type routed and fulfilled; redirects followed in the worker.
    var full = new RenderOptions { BlockIframes = false, Block = false, UseCache = false };
    var r = await RenderUrlAsync(browser, "http://site.test/spa/1.html", full);
    var types = r.Stats.Log.Where(l => !l.StartsWith("ws", StringComparison.Ordinal) && !l.StartsWith("refused", StringComparison.Ordinal))
        .GroupBy(l => l.Split(' ')[0] + " " + l.Split(' ')[1]).Select(g => $"{g.Key}×{g.Count()}");
    Console.WriteLine("routed: " + string.Join(", ", types));
    foreach (var line in r.Stats.Log)
    {
        Console.WriteLine("  " + line);
    }

    Check("2 route-all: document, classic + module scripts, dynamic import(), stylesheet, fetch, XHR, POST, cross-origin API redirect (worker follows)",
        r.Error is null && HasAll(r.Text, "API-TITLE-1", "API-TEXT-1", "API-TITLE-101", "DYNAMIC-IMPORT-OK", "CLASSIC-OK", "API-REDIRECT-OK", "POST-OK"),
        Has(r.Text, "API-TITLE-1", "API-TITLE-101", "DYNAMIC-IMPORT-OK", "CLASSIC-OK", "API-REDIRECT-OK", "POST-OK") + $" error={r.Error}");
    Check("2 redirected module script with a relative import (worker follows, fulfils at the original URL)", r.Text.Contains("REDIRECTED-MODULE-OK REL-IMPORT-OK", StringComparison.Ordinal),
        Has(r.Text, "REDIRECTED-MODULE-OK REL-IMPORT-OK", "REDIRECTED-MODULE-MISSING") + " (the import resolves against the original URL: " + string.Join(", ", r.Stats.Log.Where(l => l.Contains("rel.js", StringComparison.Ordinal))) + ")");
    Check("2 CORS preflight answered inside the renderer by Playwright, never routed to the worker", !r.Stats.Log.Any(l => l.Contains(" OPTIONS ", StringComparison.Ordinal)) && r.Text.Contains("POST-OK", StringComparison.Ordinal),
        "routed methods: " + string.Join(",", r.Stats.Log.Select(l => l.Split(' ').ElementAtOrDefault(1)).Distinct()) + "; the preflighted POST and the x-spike fetch succeeded");
    Check("2 iframe document and its fetch routed", r.Stats.Log.Any(l => l.Contains("frame.html", StringComparison.Ordinal)) && r.Stats.Log.Any(l => l.Contains("/api/page/99", StringComparison.Ordinal)), "frame.html and api/page/99 in the route log");

    var redirectBrowserSide = new PageRenderer(new RenderFetcher(client, guard) { FollowRedirects = false }, new AssetCache());
    var rr = await redirectBrowserSide.RenderAsync(browser, new Uri("http://site.test/spa/1.html"), await MainAsync(new Uri("http://site.test/spa/1.html")), full);
    Console.WriteLine("route log with 30x handed to the browser:");
    foreach (var line in rr.Stats.Log)
    {
        Console.WriteLine("  " + line);
    }

    Check("2 fulfilled 30x, the browser follows (expected to fail: Playwright continues redirect hops unrouted)", HasAll(rr.Text, "API-REDIRECT-OK", "REDIRECTED-MODULE-OK"), Has(rr.Text, "API-TITLE-1", "API-REDIRECT-OK", "API-REDIRECT-FAIL", "REDIRECTED-MODULE-OK REL-IMPORT-OK", "REDIRECTED-MODULE-MISSING", "BUNDLE-ERROR") + $" error={rr.Error}");

    // 3. Bypass probes, with and without WebSocket routing.
    foreach (var routeWs in new[] { true, false })
    {
        var path = new Uri(endpoint).AbsolutePath;
        var b = await RenderUrlAsync(browser, "http://site.test/bypass.html?wspath=" + Uri.EscapeDataString(path), new RenderOptions { RouteWebSockets = routeWs, Ready = ReadyMode.Selector, ReadySelector = "#results:has-text('done')", TimeoutMs = 60_000 });
        Console.WriteLine($"bypass probes (RouteWebSocketAsync {(routeWs ? "on" : "off")}), ready={b.Ready} error={b.Error}:");
        Console.WriteLine("  " + b.Text);
        foreach (var line in b.Stats.Log)
        {
            Console.WriteLine("  route: " + line);
        }

        Console.WriteLine("  seen: " + string.Join(" | ", b.Stats.Seen));
    }

    foreach (var policy in new[] { ServiceWorkerPolicy.Block, ServiceWorkerPolicy.Allow })
    {
        var sw = await RenderUrlAsync(browser, "https://site.test/sw.html", new RenderOptions { Ready = ReadyMode.Selector, ReadySelector = "#r:has-text('done')", ServiceWorkers = policy });
        Console.WriteLine($"3 service worker ({policy}) on https://site.test/sw.html: '{sw.Text}' error={sw.Error}; routed [{string.Join(", ", sw.Stats.Log.Where(l => !l.Contains("event", StringComparison.Ordinal)))}]");
    }

    var nav = await RenderUrlAsync(browser, "http://site.test/nav.html", new RenderOptions { QuietMs = 300 });
    Check("3 main-frame navigation by script intercepted, document kept (204)", nav.Stats.NavigationsAborted == 1 && nav.Text.Contains("NAV-PAGE-TEXT", StringComparison.Ordinal), $"intercepted={nav.Stats.NavigationsAborted} text='{nav.Text}' error={nav.Error}");

    // 11. Navigation by script, meta refresh, popups and downloads: all arrive as main-frame navigations in the route.
    foreach (var noContent in new[] { false, true })
    {
        foreach (var page in new[] { "nav.html", "refresh.html", "popup.html" })
        {
            var n = await RenderUrlAsync(browser, "http://site.test/" + page, new RenderOptions { QuietMs = 300, NavigationNoContent = noContent });
            Console.WriteLine($"11 {page} ({(noContent ? "204" : "abort")}): ready={n.Ready} error={n.Error} text='{n.Text}' intercepted navigations [{string.Join(", ", n.Stats.Navigations)}]; events [{string.Join(", ", n.Stats.Log.Where(l => l.Contains("event", StringComparison.Ordinal) || l.StartsWith("route-error", StringComparison.Ordinal)))}]");
            Console.WriteLine("    seen: " + string.Join(" | ", n.Stats.Seen));
        }
    }

    // 12. A cookie set by the shell and required by the data API.
    foreach (var pass in new[] { false, true })
    {
        var cookieRenderer = new PageRenderer(new RenderFetcher(client, guard) { PassCookies = pass }, new AssetCache());
        var uri = new Uri("http://site.test/cookie.html");
        var mainDoc = await new RenderFetcher(client, guard) { PassCookies = pass }.FetchAsync(new SubRequest("GET", uri, [], null, "document"), CancellationToken.None);
        var c = await cookieRenderer.RenderAsync(browser, uri, mainDoc, new RenderOptions { Ready = ReadyMode.Selector, ReadySelector = "#app:not(:has-text('Laddar'))" });
        Console.WriteLine($"12 cookie (pass Set-Cookie/Cookie through: {pass}): main doc set-cookie {(mainDoc.Headers.ContainsKey("set-cookie") ? "kept" : "dropped")}; text '{c.Text}'; {string.Join(" | ", c.Stats.Log.Where(l => l.StartsWith("cookie", StringComparison.Ordinal)))}");
    }

    // 5. Readiness: quiet rule vs selector vs networkidle.
    foreach (var (name, options) in new (string, RenderOptions)[]
    {
        ("quiet 100 ms", new RenderOptions()),
        ("selector #app article", new RenderOptions { Ready = ReadyMode.Selector, ReadySelector = "#app article" }),
        ("selector #app (holds 'Laddar…')", new RenderOptions { Ready = ReadyMode.Selector, ReadySelector = "#app" }),
        ("networkidle", new RenderOptions { Ready = ReadyMode.NetworkIdle }),
    })
    {
        var times = new List<double>();
        var complete = 0;
        for (var i = 2; i <= 6; i++)
        {
            var x = await RenderUrlAsync(browser, $"http://site.test/spa/{i}.html", options);
            times.Add(x.ReadyMs);
            complete += x.Text.Contains($"API-TEXT-{i}", StringComparison.Ordinal) ? 1 : 0;
        }

        Console.WriteLine($"5 ready '{name}': ready ms {string.Join(", ", times.Select(t => t.ToString("F0")))}; API text present in {complete}/5");
    }

    // 9. DOM output.
    var o = await RenderUrlAsync(browser, "http://site.test/spa/1.html", new RenderOptions());
    var stripped = !o.Html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !o.Html.Contains("<noscript", StringComparison.OrdinalIgnoreCase)
        && !o.Html.Contains("<style", StringComparison.OrdinalIgnoreCase) && !o.Html.Contains("<template", StringComparison.OrdinalIgnoreCase)
        && !o.Html.Contains("<iframe", StringComparison.OrdinalIgnoreCase) && !o.Html.Contains("onclick", StringComparison.OrdinalIgnoreCase);
    Check("9 DOM output stripped, API text present", stripped && o.Text.Contains("API-TEXT-1", StringComparison.Ordinal) && !o.Text.Contains("NOSCRIPT-TEXT", StringComparison.Ordinal),
        $"{o.Html.Length} chars, serialise+clean {o.SerializeMs:F1} ms; text starts '{o.Text[..Math.Min(160, o.Text.Length)]}'");
    await File.WriteAllTextAsync("/results/spa-1.rendered.html", o.Html);

    // 10. Content that loads on scroll.
    foreach (var (name, options) in new (string, RenderOptions)[]
    {
        ("quiet only, 1280x720", new RenderOptions()),
        ("scroll up to 10 steps, 1280x720", new RenderOptions { ScrollSteps = 10 }),
        ("quiet only, 1280x10000", new RenderOptions { ViewportHeight = 10_000 }),
        ("scroll up to 10 steps, 1280x10000", new RenderOptions { ScrollSteps = 10, ViewportHeight = 10_000 }),
    })
    {
        var s = await RenderUrlAsync(browser, "http://site.test/scroll.html", options);
        var items = System.Text.RegularExpressions.Regex.Count(s.Html, "<li>SCROLL-ITEM-");
        Console.WriteLine($"10 scroll '{name}': ready={s.Ready} lazy={(s.Text.Contains("LAZY-API-TITLE-42", StringComparison.Ordinal) ? "loaded" : "MISSING")} items={items}/40 total {s.RenderMs:F0} ms (ready {s.ReadyMs:F0}, scroll {s.ScrollMs:F0} ms, +{s.ScrollRequests} requests), routed {s.Stats.Routed}");
    }

    await browser.CloseAsync();
}

async Task MeasureAsync(int[] concurrencies)
{
    using var playwright = await Playwright.CreateAsync();
    var report = new List<object>();
    foreach (var concurrency in concurrencies)
    {
        foreach (var (scenario, urls, options) in Scenarios())
        {
            cache.Clear();
            var browser = await ConnectAsync(playwright);
            var results = new System.Collections.Concurrent.ConcurrentBag<(int Index, double TotalMs, double MainMs, RenderResult R)>();
            var queue = new System.Collections.Concurrent.ConcurrentQueue<(int, string)>(urls.Select((u, i) => (i, u)));
            var wall = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
            {
                while (queue.TryDequeue(out var item))
                {
                    var clock = Stopwatch.StartNew();
                    var uri = new Uri(item.Item2);
                    var main = await MainAsync(uri);
                    var mainMs = clock.Elapsed.TotalMilliseconds;
                    var r = options is null
                        ? new RenderResult("", "", true, 0, 0, 0, 0, 0, new RenderStats { Fetched = 1 }, null)
                        : await renderer.RenderAsync(browser, uri, main, options);
                    results.Add((item.Item1, clock.Elapsed.TotalMilliseconds, mainMs, r));
                }
            }));
            var wallMs = wall.Elapsed.TotalMilliseconds;
            await browser.CloseAsync();
            var ordered = results.OrderBy(x => x.Index).ToList();
            var first = ordered[0];
            var warm = ordered.Skip(concurrency).Select(x => x.TotalMs).Order().ToList();
            var row = new
            {
                scenario,
                concurrency,
                pages = ordered.Count,
                failed = ordered.Count(x => x.R.Error is not null || !x.R.Ready),
                firstMs = Math.Round(first.TotalMs),
                p50 = Math.Round(Percentile(warm, 0.50)),
                p95 = Math.Round(Percentile(warm, 0.95)),
                pagesPerMin = Math.Round(ordered.Count / wallMs * 60_000, 1),
                fetched = Math.Round(ordered.Average(x => x.R.Stats.Fetched + (options is null ? 0 : 1)), 1),
                cached = Math.Round(ordered.Average(x => x.R.Stats.Cached), 1),
                blocked = Math.Round(ordered.Average(x => x.R.Stats.Blocked), 1),
                errors = ordered.Where(x => x.R.Error is not null).Select(x => x.R.Error).Distinct().Take(2),
            };
            report.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    // A change behind an unchanged shell, picked up by the next render.
    var b2 = await ConnectAsync(playwright);
    var before = await RenderUrlAsync(b2, "http://site.test/shell.html", new RenderOptions());
    var shellBefore = (await MainAsync(new Uri("http://site.test/shell.html"))).Body;
    await client.Http.GetStringAsync(new Uri("http://site.test/__bump"));
    var after = await RenderUrlAsync(b2, "http://site.test/shell.html", new RenderOptions());
    var shellAfter = (await MainAsync(new Uri("http://site.test/shell.html"))).Body;
    Check("7b change behind an unchanged shell", shellBefore.SequenceEqual(shellAfter) && before.Text != after.Text,
        $"shell bytes equal={shellBefore.SequenceEqual(shellAfter)}; before '{before.Text[..Math.Min(40, before.Text.Length)]}…' after '{after.Text[..Math.Min(40, after.Text.Length)]}…'");
    await b2.CloseAsync();

    Console.WriteLine($"worker: {WorkerMemory()}");
    await File.WriteAllTextAsync($"/results/measure-{DateTime.UtcNow:HHmmss}.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

IEnumerable<(string Name, string[] Urls, RenderOptions? Options)> Scenarios()
{
    var spa = Enumerable.Range(1, 20).Select(i => $"http://site.test/spa/{i}.html").ToArray();
    var stat = Enumerable.Range(1, 20).Select(i => $"http://site.test/static/{i}.html").ToArray();
    yield return ("static, HTTP only (no render)", stat, null);
    yield return ("static, rendered", stat, new RenderOptions());
    yield return ("spa, rendered, cache+block", spa, new RenderOptions());
    yield return ("spa, rendered, no cache, no block", spa, new RenderOptions { UseCache = false, Block = false, BlockIframes = false });
    yield return ("spa, networkidle, cache+block", spa, new RenderOptions { Ready = ReadyMode.NetworkIdle });
}

static double Percentile(List<double> sorted, double p) =>
    sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

static string WorkerMemory()
{
    var parts = new List<string>();
    foreach (var dir in Directory.GetDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _)))
    {
        try
        {
            var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')[0];
            var status = File.ReadAllLines(Path.Combine(dir, "status"));
            string Field(string f) => status.First(l => l.StartsWith(f, StringComparison.Ordinal)).Split(':')[1].Trim();
            parts.Add($"{Path.GetFileName(cmd)} rss {Field("VmRSS")} peak {Field("VmHWM")}");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
        }
    }

    var peak = File.Exists("/sys/fs/cgroup/memory.peak") ? long.Parse(File.ReadAllText("/sys/fs/cgroup/memory.peak").Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1024 / 1024 + " MB" : "n/a";
    return string.Join("; ", parts) + $"; container memory.peak {peak}";
}

async Task SmokeAsync()
{
    using var playwright = await Playwright.CreateAsync();
    try
    {
        var browser = await ConnectAsync(playwright);
        var r = await RenderUrlAsync(browser, "http://site.test/spa/1.html", new RenderOptions());
        Console.WriteLine($"smoke: ready={r.Ready} {r.RenderMs:F0} ms error={r.Error} {Has(r.Text, "API-TEXT-1")}");
        if (args.Contains("hold"))
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        await browser.CloseAsync();
    }
    catch (Exception e)
    {
        Console.WriteLine("smoke failed: " + e.GetType().Name + ": " + string.Join(" | ", e.Message.Split('\n').Where(l => l.Trim().Length > 0).Take(6)));
    }
}

async Task LightpandaAsync()
{
    using var playwright = await Playwright.CreateAsync();
    var lp = Environment.GetEnvironmentVariable("LIGHTPANDA_ENDPOINT") ?? "ws://lightpanda:9222/";
    IBrowser browser;
    try
    {
        browser = await playwright.Chromium.ConnectOverCDPAsync(lp, new BrowserTypeConnectOverCDPOptions { Timeout = 30_000 });
        Console.WriteLine($"lightpanda connected: {browser.Version}, contexts {browser.Contexts.Count}");
    }
    catch (Exception e)
    {
        Console.WriteLine("lightpanda connect failed: " + e.Message.Split('\n')[0]);
        return;
    }

    foreach (var newContext in new[] { true, false })
    {
        var r = await RenderUrlAsync(browser, "http://site.test/spa/1.html", new RenderOptions(), newContext);
        Console.WriteLine($"lightpanda spa/1 (new context {newContext}): ready={r.Ready} {r.RenderMs:F0} ms error={r.Error}; {Has(r.Text, "API-TITLE-1", "API-TEXT-1", "DYNAMIC-IMPORT-OK", "CLASSIC-OK", "API-REDIRECT-OK", "POST-OK", "REDIRECTED-MODULE-OK")}; routed {r.Stats.Routed}, fetched {r.Stats.Fetched}");
        foreach (var line in r.Stats.Log.Take(30))
        {
            Console.WriteLine("  " + line);
        }

        if (r.Error is null)
        {
            var times = new List<double>();
            for (var i = 2; i <= 21; i++)
            {
                var x = await RenderUrlAsync(browser, $"http://site.test/spa/{i}.html", new RenderOptions(), newContext);
                times.Add(x.Error is null && x.Text.Contains($"API-TEXT-{i}", StringComparison.Ordinal) ? x.RenderMs : double.NaN);
            }

            var ok = times.Where(t => !double.IsNaN(t)).Order().ToList();
            Console.WriteLine($"lightpanda spa x20: ok {ok.Count}/20, p50 {Percentile(ok, 0.5):F0} ms, p95 {Percentile(ok, 0.95):F0} ms");
            var s = await RenderUrlAsync(browser, "http://site.test/static/1.html", new RenderOptions(), newContext);
            Console.WriteLine($"lightpanda static/1: {s.RenderMs:F0} ms, text ok {s.Text.Contains("STATIC-TEXT-1", StringComparison.Ordinal)}, error {s.Error}");
            break;
        }
    }

    await browser.CloseAsync();
}
