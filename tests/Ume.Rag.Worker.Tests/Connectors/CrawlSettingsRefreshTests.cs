using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// A <c>Settings.Crawl</c> change reaches the fetch path of a running worker within one refresh (issue #360): the worker as
/// Program.cs builds it, with only the settings source's clock replaced by a fake one and the test site's name resolved to
/// loopback (admitted by <c>Crawl:AllowedPrivateRanges</c>).
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class CrawlSettingsRefreshTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const int SentinelPort = 7001;

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly CapturingLoggerProvider _logs = new();
    private TestSite _site = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nAllow: /\n"));
            app.MapGet("/webbplats/", () => Results.Content("<html><body><p>Testwebbplatsen</p></body></html>", "text/html"));
        });
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_changed_allowed_port_reaches_the_guard_of_a_running_worker_within_one_refresh()
    {
        await using var worker = await StartAsync();
        var guard = worker.Services.GetRequiredService<IEgressGuard>();

        await SetCrawlAsync($$"""{ "allowedPorts": [{{_site.Port}}] }""");
        await RefreshUntilAsync(() => Allows(guard, _site.Url("/webbplats/")));

        Allows(guard, new Uri($"http://{TestSite.Host}:{SentinelPort}/")).ShouldBeFalse();
        _logs.Logged(typeof(PlatformCrawlSettingsSource).FullName!, "could not be read").ShouldBeFalse(_logs.Text);
    }

    [Fact]
    public async Task A_robots_txt_the_guard_refused_does_not_outlive_the_settings_change_that_allows_its_port()
    {
        await using var worker = await StartAsync();
        var guard = worker.Services.GetRequiredService<IEgressGuard>();
        var robots = worker.Services.GetRequiredService<RobotsCache>();
        var fetcher = worker.Services.GetRequiredService<HttpFetcher>();
        var page = _site.Url("/webbplats/");

        // A crawl preflight's listing asks for robots.txt of every start host, whether the guard allows the start URL or not.
        var refused = await robots.GetAsync(page, Ct);
        (refused.Status, refused.EgressRefusal).ShouldBe((RobotsStatus.Unreachable, "egress." + EgressGuard.Port));

        await SetCrawlAsync($$"""{ "allowedPorts": [{{_site.Port}}] }""");
        await RefreshUntilAsync(() => Allows(guard, page));

        // The next preflight's probe of the start URL, as Preflight sends it.
        var result = await fetcher.FetchAsync(
            new HttpFetchRequest(page, EgressPurpose.Preflight, null, null, CanonicalizerOptions.Default, robots.CheckAsync), Ct);

        var ok = result.ShouldBeOfType<HttpFetchResult.Ok>(result.ToString());
        await ok.Body.DisposeAsync();
        (await robots.GetAsync(page, Ct)).Status.ShouldBe(RobotsStatus.Ok);
    }

    /// <summary>
    /// The worker with <c>Settings.Crawl</c> allowing only <see cref="SentinelPort"/>, after the settings source's first read
    /// (the guard allows the sentinel port).
    /// </summary>
    private async Task<WorkerHost> StartAsync()
    {
        await SetCrawlAsync($$"""{ "allowedPorts": [{{SentinelPort}}] }""");
        var worker = await WorkerHost.StartAsync(database, services =>
        {
            services.AddSingleton<IDnsResolver>(_site.Resolver());
            services.Replace(ServiceDescriptor.Singleton(sp => new PlatformCrawlSettingsSource(sp.GetRequiredService<JobDatabase>(),
                sp.GetRequiredService<IOptions<CrawlOptions>>(), _clock, sp.GetRequiredService<ILogger<PlatformCrawlSettingsSource>>())));
        }, new Dictionary<string, string?> { ["Crawl:AllowedPrivateRanges"] = "127.0.0.1/32" }, _logs, engine: true);
        var guard = worker.Services.GetRequiredService<IEgressGuard>();
        await Commands.WaitUntilAsync(() => Task.FromResult(Allows(guard, new Uri($"http://{TestSite.Host}:{SentinelPort}/"))), TimeSpan.FromSeconds(60));
        return worker;
    }

    /// <summary>Moves the settings source's clock on by one refresh interval at a time until <paramref name="condition"/> holds.</summary>
    private Task RefreshUntilAsync(Func<bool> condition) =>
        Commands.WaitUntilAsync(() =>
        {
            _clock.Advance(PlatformCrawlSettingsSource.RefreshInterval);
            return Task.FromResult(condition());
        }, TimeSpan.FromSeconds(60));

    private static bool Allows(IEgressGuard guard, Uri url) => guard.CheckUrl(url, EgressPurpose.Fetch).IsAllowed;

    private async Task SetCrawlAsync(string crawl)
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator));
        var row = await db.Settings.FindAsync([PlatformSettings.SingletonId], Ct);
        if (row is null)
        {
            row = new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow };
            db.Settings.Add(row);
        }

        row.Crawl = crawl;
        await db.SaveChangesAsync(Ct);
    }
}
