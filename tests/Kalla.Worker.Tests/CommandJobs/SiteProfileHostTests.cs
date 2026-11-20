using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Sources;
using Kalla.TestKit;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Connectors.Urls;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Tests.Engine;

namespace Kalla.Worker.Tests.CommandJobs;

/// <summary>
/// One site-profile host convention (P1a item 9 follow-up; connectors design §5.6): the key is the canonical host without a
/// port (<see cref="SiteProfileHost"/>), the only form <c>edit-site-profile</c> writes. A profile stored for a host is found
/// by the crawler (<see cref="LastmodReliability"/> over a frontier row's authority, <see cref="SiteProfileCanonicalRules"/>
/// over the scope hosts), by <c>SiteProfileFollowUp</c> (the edit's reprocess syncs) and by the applied-check of the
/// engine and preview (<see cref="SiteProfiles.AppliedAsync"/>), whether the URL has no port, the explicit default port or
/// another port.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class SiteProfileHostTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        // After the reset: it disposes the data sources the runner's queue and database use.
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task A_profile_stored_for_a_host_is_found_by_every_lookup_whatever_the_urls_port()
    {
        var host = $"h{Guid.NewGuid():N}.example.se";
        string[] urls = [$"https://{host}/a-{Guid.NewGuid():N}", $"https://{host}:443/b-{Guid.NewGuid():N}", $"http://{host}:8080/c-{Guid.NewGuid():N}"];
        var seeds = new List<SourceSeed>();
        try
        {
            // One source per URL form, and one with a page on a host that only ends like the profile's.
            foreach (var url in urls)
            {
                var seed = await EngineKit.SeedUrlListAsync(database, []);
                seeds.Add(seed);
                await CommandSeeds.DocumentAsync(database, seed, url);
            }

            var decoy = await EngineKit.SeedUrlListAsync(database, []);
            seeds.Add(decoy);
            await CommandSeeds.DocumentAsync(database, decoy, $"https://x{host}/d-{Guid.NewGuid():N}");

            var level = seeds[0].Classification;
            var edit = await _runner.RunAsync(new EditSiteProfileCommand
            {
                Host = host,
                PresetId = "sitevision",
                Profile = SitevisionPreset.Profile with { LastmodReliable = true, ExcludedTypes = ["application/pdf"] },
            }, seeds[0].DepartmentId, level);
            edit.Result.ShouldBeOfType<JobResult.Committed>();
            var versionId = (await CommandSeeds.ResultAsync(database, edit.Job.Id)).GetProperty("appliedVersionId").GetGuid();

            // SiteProfileFollowUp: a reprocess sync for each source with a page on the host, whatever its port.
            for (var i = 0; i < urls.Length; i++)
            {
                (await CommandSeeds.SyncJobsAsync(database, seeds[i].SourceId)).ShouldBe(1, urls[i]);
            }

            (await CommandSeeds.SyncJobsAsync(database, decoy.SourceId)).ShouldBe(0, "another host");

            // The applied-check of the engine and the preview.
            var applied = new SiteProfiles(database.Database(RagDatabase.Worker));
            foreach (var url in urls)
            {
                var found = (await applied.AppliedAsync(level, url, Ct)).ShouldNotBeNull(url);
                found.Id.ShouldBe(versionId, url);
                found.Profile.Host.ShouldBe(host);
            }

            (await applied.AppliedAsync(level, $"https://x{host}/", Ct)).ShouldBeNull();

            // The crawler: lastmod reliability by a frontier row's authority, the scope hosts' rules and excluded types.
            var reader = new PostgresSiteProfileReader(database.Database(RagDatabase.Worker));
            foreach (var url in urls)
            {
                (await new LastmodReliability(reader, TimeProvider.System).IsReliableAsync(level, new Uri(url).Authority, Ct)).ShouldBeTrue(url);
                (await new LastmodReliability(reader, TimeProvider.System).IsReliableAsync(level, ExplicitAuthority(url), Ct)).ShouldBeTrue(url);
                var scope = CrawlScope.Create(new CrawlSettings { StartUrls = [url] });
                var rules = await new SiteProfileCanonicalRules(reader, TimeProvider.System).ForHostsAsync(level, scope.Hosts, Ct);
                rules.ExcludedTypes.ShouldContainKey(host, url);
                rules.ExcludedTypes[host].ShouldBe(["application/pdf"]);
            }
        }
        finally
        {
            foreach (var seed in seeds)
            {
                await EngineKit.PauseAsync(database, seed);
            }
        }
    }

    [Theory]
    [InlineData("https://Www.Umea.SE/a", "www.umea.se")]
    [InlineData("https://www.umea.se:443/a", "www.umea.se")]
    [InlineData("http://www.umea.se:8080/a", "www.umea.se")]
    [InlineData("https://www.umea.se./a", "www.umea.se")]
    [InlineData("https://umeå.se/a", "xn--ume-wla.se")]
    [InlineData("http://[::1]:8080/a", "[::1]")]
    public void The_key_is_the_canonical_host_without_a_port(string url, string key)
    {
        SiteProfileHost.Of(url).ShouldBe(key);
        SiteProfileHost.OfAuthority(new Uri(url).Authority).ShouldBe(key);
    }

    /// <summary>The URL's authority with its port written out, default or not (<c>host:443</c>).</summary>
    private static string ExplicitAuthority(string url)
    {
        var uri = new Uri(url);
        return $"{uri.Host}:{uri.Port}";
    }
}
