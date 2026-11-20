using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Crawl;
using Kalla.TestKit;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Render;
using Kalla.Worker.Jobs;
using Kalla.Worker.Tests.Connectors;
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Scale;

/// <summary>Every virtual host of the probe on one politeness preset (<c>Settings.Crawl.HostOverrides</c> as the admin sets it).</summary>
internal sealed class PresetCrawlPolicy(IEnumerable<string> hosts, string preset) : ICrawlPolicySource
{
    public CrawlPolicy Current { get; } = new([],
        hosts.ToDictionary(h => h, _ => new HostPoliteness { Preset = preset }, StringComparer.OrdinalIgnoreCase));
}

/// <summary>The probe's knobs, from the environment (defaults in brackets).</summary>
/// <param name="Pages">Pages per host (<c>UME_RAG_SCALE_PAGES</c>, 20).</param>
/// <param name="Latency">Each request's latency at the site (<c>UME_RAG_SCALE_LATENCY_MS</c>, 50).</param>
/// <param name="RenderTime">Each page's render on the fake renderer (<c>UME_RAG_SCALE_RENDER_MS</c>, 1500).</param>
/// <param name="FetchLanes"><c>Worker:Concurrency:fetch</c> (<c>UME_RAG_SCALE_FETCH</c>, the default 8).</param>
/// <param name="RenderSlots"><c>Render:MaxConcurrent</c> (<c>UME_RAG_SCALE_RENDER_MAX</c>, the default 2).</param>
/// <param name="MaxHosts">Scenarios with more hosts are skipped (<c>UME_RAG_SCALE_MAX_HOSTS</c>, none).</param>
internal sealed record ProbeKnobs(int Pages, TimeSpan Latency, TimeSpan RenderTime, int FetchLanes, int RenderSlots, int MaxHosts)
{
    public static ProbeKnobs FromEnvironment() => new(
        Read("UME_RAG_SCALE_PAGES", 20),
        TimeSpan.FromMilliseconds(Read("UME_RAG_SCALE_LATENCY_MS", 50)),
        TimeSpan.FromMilliseconds(Read("UME_RAG_SCALE_RENDER_MS", 1500)),
        Read("UME_RAG_SCALE_FETCH", JobClassPolicy.For(JobClasses.Fetch).DefaultConcurrency),
        Read("UME_RAG_SCALE_RENDER_MAX", new RenderOptions().MaxConcurrent),
        Read("UME_RAG_SCALE_MAX_HOSTS", int.MaxValue));

    private static int Read(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
}

/// <summary>
/// The worker's own <c>rag.jobs.requeued</c> (by reason) and <c>rag.jobs.duration</c> (executions) of <c>fetch</c> jobs,
/// filtered to its <see cref="IMeterFactory"/>: a process-wide listener would count other tests' workers too (#431).
/// </summary>
internal sealed class FetchJobMeters : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _requeued = new(StringComparer.Ordinal);
    private long _executions;

    public FetchJobMeters(IMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name is "rag.jobs.requeued" or "rag.jobs.duration")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (Tag(tags, "class") == JobClasses.Fetch && Tag(tags, "reason") is { } reason)
            {
                _requeued.AddOrUpdate(reason, value, (_, n) => n + value);
            }
        });
        _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (Tag(tags, "class") == JobClasses.Fetch)
            {
                Interlocked.Increment(ref _executions);
            }
        });
        _listener.Start();
    }

    /// <summary>Fetch jobs run to an outcome (a requeue without an attempt included).</summary>
    public long Executions => Interlocked.Read(ref _executions);

    public long Requeued(string reason) => _requeued.GetValueOrDefault(reason);

    public void Dispose() => _listener.Dispose();

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value as string;
            }
        }

        return null;
    }
}

/// <summary>One sample: fetch jobs running in the worker, requests in flight at the site, renders in progress.</summary>
internal readonly record struct LaneSample(long At, int Fetches, int Requests, int Renders);

/// <summary>Samples the worker's running <c>fetch</c> jobs (its <see cref="RunningJobs"/>), the site's requests in flight and the render slots in use every 20 ms.</summary>
internal sealed class LaneSampler : IAsyncDisposable
{
    private readonly List<LaneSample> _samples = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LaneSampler(RunningJobs running, RenderSlots renders, ScaleSite site, IReadOnlyList<Guid> departments)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    _samples.Add(new LaneSample(Stopwatch.GetTimestamp(), departments.Sum(d => running.CountFor(JobClasses.Fetch, d)), site.InFlight, renders.InUse));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Stopped.
            }
        });
    }

    /// <summary>Stops sampling and returns the samples between <paramref name="from"/> and <paramref name="to"/> (timestamps).</summary>
    public async Task<IReadOnlyList<LaneSample>> StopAsync(long from, long to)
    {
        await _stop.CancelAsync();
        await _loop;
        return [.. _samples.Where(s => s.At >= from && s.At <= to)];
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}

/// <summary>One scenario's numbers (connectors design §6.0, §6.3, §8.3; performance pass §2.3).</summary>
internal sealed record ProbeResult(
    int Hosts, string Preset, int BrowserPercent, int BrowserHosts, int Pages, TimeSpan Wall, TimeSpan Span, int MaxInFlight, int Cap,
    double FetchesMean, double LanesFullShare, double RequestsMean, double RendersMean, long Executions, long PolitenessRequeues, long RenderBusyRequeues,
    double HostBound, double RenderBound, string Binding, ProbeKnobs Knobs)
{
    public const string Header =
        "| hosts | profile | browser | pages | wall s | crawl s | pages/s | max in flight/host (cap) | fetch lanes busy | all lanes busy | requests in flight | renders | fetch runs/page | politeness requeues | render.busy requeues | host bound s | render bound s | binding |\n" +
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    public double PagesPerSecond => Span > TimeSpan.Zero ? Pages / Span.TotalSeconds : 0;

    public string Row() => string.Create(CultureInfo.InvariantCulture,
        $"| {Hosts} | {Preset} | {BrowserPercent} % ({BrowserHosts}) | {Pages} | {Wall.TotalSeconds:F1} | {Span.TotalSeconds:F1} | {PagesPerSecond:F1} | {MaxInFlight} ({Cap}) | " +
        $"{FetchesMean:F1} of {Knobs.FetchLanes} | {LanesFullShare:P0} | {RequestsMean:F1} | {RendersMean:F2} of {Knobs.RenderSlots} | {(double)Executions / Math.Max(1, Pages):F1} | " +
        $"{PolitenessRequeues} | {RenderBusyRequeues} | {HostBound:F1} | {RenderBound:F1} | {Binding} |");
}

/// <summary>
/// A-18, the crawl concurrency probe: how parallel one worker's crawler is and what binds first with many sites. N crawl
/// sources, one per virtual host of <see cref="ScaleSite"/>, synced at once through the engine (EngineKit, the FakeGateway,
/// the test resolver) with every host on the real <c>polite</c> (1 req/s, 1 connection) or <c>fast</c> (10 req/s,
/// 4 connections) preset, and a share of them <c>browser</c> sources on <see cref="FakeRenderer"/>. It records pages/s, the
/// most requests one host had in flight (never above its <c>MaxConnections</c>), the fetch lanes' and render slots' use
/// (sampled in process), the politeness and <c>render.busy</c> requeues (the worker's own meters) and the limit that bound
/// the scenario, to the test output and to <see cref="ReportPath"/>. Trait <see cref="Category"/>, and skipped unless
/// <see cref="Switch"/> is <c>1</c>, so neither CI nor a plain <c>dotnet test</c> runs it.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
[Trait(TestCategory.Name, Category)]
public sealed class CrawlConcurrencyProbeTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>
{
    public const string Category = "Scale";

    /// <summary>The environment variable that turns the probe on.</summary>
    public const string Switch = "UME_RAG_SCALE";

    private static readonly Lock ReportLock = new();
    private static bool _reportStarted;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The Markdown report of a run with <paramref name="knobs"/>, under the test's output directory; rewritten by each test process.</summary>
    internal static string ReportPath(ProbeKnobs knobs) => Path.Combine(AppContext.BaseDirectory, "scale", string.Create(CultureInfo.InvariantCulture,
        $"crawl-concurrency-p{knobs.Pages}-l{knobs.Latency.TotalMilliseconds:F0}-r{knobs.RenderTime.TotalMilliseconds:F0}-f{knobs.FetchLanes}-s{knobs.RenderSlots}.md"));

    [Theory]
    [InlineData(10, PolitenessValues.Polite, 0)]
    [InlineData(10, PolitenessValues.Polite, 5)]
    [InlineData(10, PolitenessValues.Fast, 0)]
    [InlineData(10, PolitenessValues.Fast, 5)]
    [InlineData(50, PolitenessValues.Polite, 0)]
    [InlineData(50, PolitenessValues.Polite, 5)]
    [InlineData(50, PolitenessValues.Fast, 0)]
    [InlineData(50, PolitenessValues.Fast, 5)]
    [InlineData(100, PolitenessValues.Polite, 0)]
    [InlineData(100, PolitenessValues.Polite, 5)]
    [InlineData(100, PolitenessValues.Fast, 0)]
    [InlineData(100, PolitenessValues.Fast, 5)]
    public async Task Crawl_concurrency_probe(int hosts, string preset, int browserPercent)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Switch) != "1", $"{Switch}=1 runs the crawl concurrency probe (A-18).");
        var knobs = ProbeKnobs.FromEnvironment();
        Assert.SkipWhen(hosts > knobs.MaxHosts, $"UME_RAG_SCALE_MAX_HOSTS is {knobs.MaxHosts}.");
        await database.ResetAsync();

        await using var site = await ScaleSite.StartAsync(hosts, knobs.Pages, knobs.Latency);
        var policy = new PresetCrawlPolicy(site.Hosts, preset);
        var browserHosts = browserPercent == 0 ? 0 : Math.Max(1, (int)Math.Round(hosts * browserPercent / 100.0, MidpointRounding.AwayFromZero));
        // The browser sources spread evenly over the hosts.
        var browser = Enumerable.Range(0, hosts).Where(i => browserHosts > 0 && i % (hosts / browserHosts) == 0).Take(browserHosts).ToHashSet();
        var seeds = new List<SourceSeed>();
        for (var i = 0; i < hosts; i++)
        {
            var start = new[] { site.StartUrl(site.Hosts[i]).AbsoluteUri };
            seeds.Add(await EngineKit.SeedCrawlAsync(database, browser.Contains(i)
                ? new { startUrls = start, exclude = Array.Empty<string>(), crawlWindow = "00:00-00:00", fetchMode = "browser" }
                : (object)new { startUrls = start, exclude = Array.Empty<string>(), crawlWindow = "00:00-00:00" }));
        }

        await EngineKit.PauseOtherSourcesAsync(database, [.. seeds]);
        var renderer = new FakeRenderer
        {
            Render = async (request, _) =>
            {
                await Task.Delay(knobs.RenderTime, Ct);
                return FakeRenderer.Page(site.PageAt(request.Url));
            },
        };
        var blobs = Directory.CreateTempSubdirectory("ume-rag-scale-blobs-");
        try
        {
            await using var worker = await EngineKit.StartWorkerAsync(database, site.Site, gateway, blobs, settings: Settings(knobs), services: s =>
            {
                s.AddSingleton<IDnsResolver>(site.Resolver());
                s.AddSingleton<ICrawlPolicySource>(policy);
                s.AddSingleton<IRenderer>(renderer);
            });
            using var meters = new FetchJobMeters(worker.Services.GetRequiredService<IMeterFactory>());
            await using var sampler = new LaneSampler(worker.Services.GetRequiredService<RunningJobs>(), worker.Services.GetRequiredService<RenderSlots>(), site,
                [.. seeds.Select(s => s.DepartmentId)]);

            var clock = Stopwatch.StartNew();
            var runIds = new List<Guid>();
            foreach (var seed in seeds)
            {
                runIds.Add(await EngineKit.SyncNowAsync(database, seed));
            }

            var runs = new List<RunCounters>();
            foreach (var runId in runIds)
            {
                runs.Add(await EngineKit.WaitForRunAsync(database, runId, TimeSpan.FromMinutes(20)));
            }

            var wall = clock.Elapsed;
            var samples = await sampler.StopAsync(site.FirstPageAt ?? 0, site.LastPageAt ?? long.MaxValue);

            var result = Result(hosts, preset, browserPercent, browser.Count, site, policy, knobs, wall, samples, meters);
            Report(result);

            runs.ShouldAllBe(r => r.State == RunStates.Succeeded && r.Failed == 0);
            runs.Sum(r => r.Added).ShouldBe(hosts * knobs.Pages, "every page of every host is a new document");
            renderer.Requests.Count.ShouldBe(browser.Count * knobs.Pages);
            foreach (var host in site.Hosts)
            {
                site.Load(host).MaxInFlight.ShouldBeLessThanOrEqualTo(policy.Current.ProfileOf(host).MaxConnections, $"{host} had more requests in flight than its connections");
            }
        }
        finally
        {
            foreach (var seed in seeds)
            {
                await EngineKit.PauseAsync(database, seed);
            }

            blobs.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The worker as deployed but for the renderer: the production heartbeat and reaper timings (the kit's short
    /// ones are for fast tests; a 1 s heartbeat under this load could reap the worker's own jobs), no scheduler ticks.
    /// </summary>
    private static Dictionary<string, string?> Settings(ProbeKnobs knobs) => new()
    {
        ["Render:Endpoint"] = RenderTestKit.Options().Endpoint,
        ["Render:MaxConcurrent"] = Invariant(knobs.RenderSlots),
        ["Worker:Concurrency:fetch"] = Invariant(knobs.FetchLanes),
        ["Worker:HeartbeatSeconds"] = Invariant(new WorkerOptions().HeartbeatSeconds),
        ["Worker:InstanceTimeoutSeconds"] = Invariant(new WorkerOptions().InstanceTimeoutSeconds),
        ["Worker:ReaperSeconds"] = Invariant(new WorkerOptions().ReaperSeconds),
        ["Worker:SchedulerSeconds"] = "600",
    };

    /// <summary>
    /// The scenario's numbers and the limit that bound it. The host bound is the slowest host alone: its pages (and the
    /// soft-404 probe of an HTTP source) at the preset's interval, a browser host one page at a time (<c>renderPerHost</c> 1,
    /// the host's connection held through the render). The render bound is every browser page on <c>Render:MaxConcurrent</c>
    /// slots. The largest bound binds when the crawl took at most 1.25 × it; otherwise the fetch lanes when all were busy in at
    /// least half the samples (a lane also holds a job waiting up to 1 s in process for its host slot); otherwise, without
    /// requeues, the worker's own throughput (its database pool of 10, the machine's CPU); otherwise the time went to requeue
    /// delays (<c>render.busy</c> 3 s, politeness, the 2 s poll).
    /// </summary>
    private static ProbeResult Result(int hosts, string preset, int browserPercent, int browserHosts, ScaleSite site, PresetCrawlPolicy policy,
        ProbeKnobs knobs, TimeSpan wall, IReadOnlyList<LaneSample> samples, FetchJobMeters meters)
    {
        var profile = policy.Current.ProfileOf(site.Hosts[0]);
        var interval = 1 / Math.Min(profile.RequestsPerSecond, profile.MaxConnections / knobs.Latency.TotalSeconds);
        var httpBound = browserHosts == hosts ? 0 : (knobs.Pages + 1) * interval;
        var browserBound = browserHosts == 0 ? 0 : knobs.Pages * Math.Max(1 / profile.RequestsPerSecond, (knobs.RenderTime + knobs.Latency).TotalSeconds) / profile.RenderPerHost;
        var hostBound = Math.Max(httpBound, browserBound);
        var renderBound = browserHosts * knobs.Pages * knobs.RenderTime.TotalSeconds / knobs.RenderSlots;
        var span = site.PageSpan;
        var fetchesMean = samples.Count == 0 ? 0 : samples.Average(s => s.Fetches);
        var lanesFull = samples.Count == 0 ? 0 : samples.Count(s => s.Fetches >= knobs.FetchLanes) / (double)samples.Count;
        var (name, bound) = renderBound > hostBound ?("Render:MaxConcurrent", renderBound)
            : browserBound >= httpBound ? ("browser host pacing (renderPerHost 1)", browserBound)
            : ("host pacing", httpBound);
        var requeues = meters.Requeued(HostBudget.Politeness) + meters.Requeued(RenderReasons.Busy);
        var binding = span.TotalSeconds <= 1.25 * bound ? name
            : lanesFull >= 0.5 ? "fetch lanes"
            : requeues == 0 ? "worker throughput (lanes not full, no requeues: database pool or CPU)"
            : string.Create(CultureInfo.InvariantCulture, $"requeue delays ({span.TotalSeconds / bound:F1} × {name})");

        return new ProbeResult(hosts, preset, browserPercent, browserHosts, site.PagesServed, wall, span,
            site.Hosts.Max(h => site.Load(h).MaxInFlight), profile.MaxConnections, fetchesMean, lanesFull,
            samples.Count == 0 ? 0 : samples.Average(s => s.Requests), samples.Count == 0 ? 0 : samples.Average(s => s.Renders),
            meters.Executions, meters.Requeued(HostBudget.Politeness), meters.Requeued(RenderReasons.Busy), hostBound, renderBound, binding, knobs);
    }

    private static void Report(ProbeResult result)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(ProbeResult.Header + "\n" + result.Row());
        lock (ReportLock)
        {
            var path = ReportPath(result.Knobs);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!_reportStarted)
            {
                File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"# Crawl concurrency probe (A-18)\n\n{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}; {result.Knobs.Pages} pages per host, {result.Knobs.Latency.TotalMilliseconds:F0} ms " +
                    $"latency, {result.Knobs.RenderTime.TotalMilliseconds:F0} ms render, Worker:Concurrency:fetch {result.Knobs.FetchLanes}, Render:MaxConcurrent " +
                    $"{result.Knobs.RenderSlots}, Crawl:FetchBatchPerHost default. Pages/s over the crawl (first page request to last answer); lanes, " +
                    $"requests in flight and renders are means of 20 ms samples over it.\n\n{ProbeResult.Header}\n"));
                _reportStarted = true;
            }

            File.AppendAllText(path, result.Row() + "\n");
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
