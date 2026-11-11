using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Pipelines.Crawl;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Http;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// Politeness profiles per host (connectors design §6.6): presets and members, the deployment's ceilings, and each member's
/// effect on the host budget (rate, <c>Crawl-delay</c>, latency, back-off caps, the circuit breaker).
/// </summary>
public sealed class PolitenessProfileTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_host_without_its_own_user_agent_gets_the_configured_one()
    {
        var policy = new CrawlPolicy([], new Dictionary<string, HostPoliteness>(StringComparer.OrdinalIgnoreCase)
        {
            ["shop.example"] = new() { UserAgent = "Mozilla/5.0 (X11; Linux x86_64)" },
        })
        { UserAgent = "ExampleBot/2.0 (+https://www.example.org/bot)" };

        policy.UserAgentOf("www.example.org").ShouldBe("ExampleBot/2.0 (+https://www.example.org/bot)");
        policy.UserAgentOf("shop.example").ShouldBe("Mozilla/5.0 (X11; Linux x86_64)");
        policy.ProductToken.ShouldBe("ExampleBot"); // robots.txt keeps naming the crawler, whatever a host is sent
        CrawlPolicy.Default.UserAgentOf("www.example.org").ShouldBe(CrawlerIdentity.DefaultUserAgent);
        CrawlPolicy.Default.ProductToken.ShouldBe("UmeaKommunBot");
    }

    [Fact]
    public void Presets_fill_the_members_and_explicit_members_override_them()
    {
        PolitenessProfile.Resolve(HostKind.External, null, PolitenessCeilings.Default).ShouldBe(PolitenessProfile.Polite);
        PolitenessProfile.Resolve(HostKind.Municipal, null, PolitenessCeilings.Default).ShouldBe(PolitenessProfile.Municipal);

        var fast = PolitenessProfile.Resolve(HostKind.External, new HostPoliteness { Preset = "fast" }, PolitenessCeilings.Default);
        (fast.RequestsPerSecond, fast.MaxConnections, fast.HonourCrawlDelay, fast.AdaptToLatency, fast.AnyTime).ShouldBe((10.0, 4, false, false, true));
        // Back-off, breaker, robots.txt and the User-Agent are never part of a preset.
        fast.Backoff.ShouldBe(BackoffLimits.Default);
        fast.Breaker.ShouldBe(BreakerRule.Default);
        fast.IgnoresRobots.ShouldBeFalse();
        fast.UserAgent.ShouldBeNull(); // the crawler's own

        var custom = PolitenessProfile.Resolve(HostKind.Municipal, new HostPoliteness
        {
            Preset = "fast",
            RequestsPerSecond = 20,
            CrawlWindow = "source",
            Backoff = new HostBackoff { MaxRetryAfterSeconds = 600, MaxFactor = 4 },
            CircuitBreaker = new HostCircuitBreaker { Threshold = 0.8 },
            Robots = new HostRobots { Mode = "ignore", Reason = " own site, web team consent 2026-11-10 " },
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64)",
            RenderPerHost = 2,
        }, PolitenessCeilings.Default);
        custom.ShouldBe(PolitenessProfile.Fast with
        {
            RequestsPerSecond = 20,
            AnyTime = false,
            Backoff = new BackoffLimits(TimeSpan.FromMinutes(10), 4),
            Breaker = new BreakerRule(0.8, TimeSpan.FromMinutes(30)),
            RobotsIgnoredReason = "own site, web team consent 2026-11-10",
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64)",
            RenderPerHost = 2,
        });
        custom.IsDefaultFor(HostKind.Municipal).ShouldBeFalse();

        // custom starts from the host kind's defaults; robots ignore without a reason and a bad User-Agent keep the defaults.
        var careless = PolitenessProfile.Resolve(HostKind.External, new HostPoliteness
        {
            Preset = "custom",
            Robots = new HostRobots { Mode = "ignore", Reason = " " },
            UserAgent = "Bot\r\nX-Injected: 1",
        }, PolitenessCeilings.Default);
        careless.ShouldBe(PolitenessProfile.Polite with { Preset = "custom" });
    }

    [Fact]
    public void Ceilings_clamp_a_profile_written_past_the_admin()
    {
        var policy = new CrawlPolicy([], new Dictionary<string, HostPoliteness>
        {
            ["www.example.org"] = new() { RequestsPerSecond = 500, MaxConnections = 64, RenderPerHost = 9 },
            ["slow.example.org"] = new() { RequestsPerSecond = 0.001, MaxConnections = 0 },
        });

        var profile = policy.ProfileOf("www.example.org");
        (profile.RequestsPerSecond, profile.MaxConnections, profile.RenderPerHost).ShouldBe((50.0, 16, 4));
        var lowered = (policy with { Ceilings = new PolitenessCeilings(20, 8, 1) }).ProfileOf("www.example.org");
        (lowered.RequestsPerSecond, lowered.MaxConnections, lowered.RenderPerHost).ShouldBe((20.0, 8, 1));
        var slow = policy.ProfileOf("slow.example.org");
        (slow.RequestsPerSecond, slow.MaxConnections).ShouldBe((0.1, 1));

        // The settings source passes the deployment's ceilings (Crawl:MaxHostRate, Crawl:MaxHostConnections).
        var (_, parsed) = PlatformCrawlSettings.Parse("""{ "hostOverrides": { "www.example.org": { "requestsPerSecond": 40, "maxConnections": 12 } } }""",
            new PolitenessCeilings(30, 6, 2));
        var clamped = parsed.ProfileOf("www.example.org");
        (clamped.RequestsPerSecond, clamped.MaxConnections).ShouldBe((30.0, 6));
    }

    [Fact]
    public void The_widened_settings_are_read_and_an_unknown_member_is_refused()
    {
        var (_, crawl) = PlatformCrawlSettings.Parse("""
            { "municipalHosts": ["*.umea.se"], "hostOverrides": { "www.umea.se": {
                "preset": "custom", "requestsPerSecond": 5, "maxConnections": 2, "crawlDelay": "ignore", "adaptToLatency": false,
                "crawlWindow": "any", "backoff": { "maxRetryAfterSeconds": 120, "maxFactor": 2 }, "circuitBreaker": "off",
                "robots": { "mode": "ignore", "reason": "own site" }, "userAgent": "Mozilla/5.0", "renderPerHost": 1 } } }
            """);
        crawl.ProfileOf("www.umea.se").ShouldBe(new PolitenessProfile
        {
            Preset = "custom",
            RequestsPerSecond = 5,
            MaxConnections = 2,
            HonourCrawlDelay = false,
            AdaptToLatency = false,
            AnyTime = true,
            Backoff = new BackoffLimits(TimeSpan.FromMinutes(2), 2),
            Breaker = null,
            RobotsIgnoredReason = "own site",
            UserAgent = "Mozilla/5.0",
            RenderPerHost = 1,
        });
        var (_, breaker) = PlatformCrawlSettings.Parse("""{ "hostOverrides": { "a.example": { "circuitBreaker": { "threshold": 0.25, "pauseMinutes": 5 } } } }""");
        breaker.ProfileOf("a.example").Breaker.ShouldBe(new BreakerRule(0.25, TimeSpan.FromMinutes(5)));

        Should.Throw<JsonException>(() => PlatformCrawlSettings.Parse("""{ "hostOverrides": { "a.example": { "requestPerSecond": 5 } } }"""));
        Should.Throw<JsonException>(() => PlatformCrawlSettings.Parse("""{ "hostOverrides": { "a.example": { "circuitBreaker": "on" } } }"""));
    }

    [Fact]
    public async Task A_fast_host_is_paced_at_10_requests_per_second_whatever_its_crawl_delay_and_latency()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryCrawlHostStore(time);
        var policy = new FixedCrawlPolicy();
        policy.Overrides["fast.example"] = new HostPoliteness { Preset = "fast" };
        var budget = new HostBudget(store, policy, time);
        await budget.EnsureHostAsync("fast.example", Ct);
        await budget.EnsureHostAsync("polite.example", Ct);
        foreach (var host in new[] { "fast.example", "polite.example" })
        {
            // robots.txt asks for 5 s between requests and the server answers in 2 s.
            store[host] = store[host] with { CrawlDelaySeconds = 5, LatencyEwmaMs = 2000 };
        }

        for (var i = 0; i < 10; i++)
        {
            var slot = (await budget.AcquireAsync("fast.example", Ct)).ShouldBeOfType<HostSlot.Granted>($"request {i + 1}");
            slot.Lease.Dispose();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        // Ten requests in one second, and the eleventh is not early: the next slot is 100 ms after the tenth.
        store["fast.example"].NextAllowedAt.ShouldBe(Start.AddSeconds(1));
        (store["fast.example"].RequestsPerSecond, store["fast.example"].MaxConnections).ShouldBe((10.0, 4));

        // Negative proof: the default profile keeps to the Crawl-delay.
        (await budget.AcquireAsync("polite.example", Ct)).ShouldBeOfType<HostSlot.Granted>().Lease.Dispose();
        (await budget.AcquireAsync("polite.example", Ct)).ShouldBe(new HostSlot.Later(TimeSpan.FromSeconds(5), HostBudget.Politeness));
    }

    [Fact]
    public void Crawl_delay_ignore_and_adapt_to_latency_false_drop_their_terms_of_the_interval()
    {
        var host = new CrawlHostState { Host = "example.org", Kind = HostKind.External, RequestsPerSecond = 1, MaxConnections = 1, CrawlDelaySeconds = 5, LatencyEwmaMs = 3000 };
        var polite = PolitenessProfile.Polite;

        HostBudgetPolicy.Interval(host, polite).ShouldBe(TimeSpan.FromSeconds(5));
        HostBudgetPolicy.Interval(host, polite with { HonourCrawlDelay = false }).ShouldBe(TimeSpan.FromSeconds(3));
        HostBudgetPolicy.Interval(host, polite with { AdaptToLatency = false }).ShouldBe(TimeSpan.FromSeconds(5));
        HostBudgetPolicy.Interval(host, polite with { HonourCrawlDelay = false, AdaptToLatency = false }).ShouldBe(TimeSpan.FromSeconds(1));
        // A factor above the profile's maxFactor counts only up to it.
        HostBudgetPolicy.Interval(host with { BackoffFactor = 16 }, polite with { HonourCrawlDelay = false, AdaptToLatency = false, Backoff = new BackoffLimits(TimeSpan.FromHours(1), 2) })
            .ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_back_off_caps_bound_the_wait_and_the_factor_but_a_retry_after_above_an_hour_still_pauses()
    {
        var host = new CrawlHostState { Host = "example.org", Kind = HostKind.External, RequestsPerSecond = 1, MaxConnections = 1 };
        var limits = new BackoffLimits(TimeSpan.FromSeconds(10), 4);
        var state = host;
        for (var i = 0; i < 5; i++)
        {
            state = HostBudgetPolicy.Apply(state, new HostResponse(HostResponseKind.RateLimited, null, TimeSpan.FromSeconds(30)) { Backoff = limits }, Start);
        }

        state.NextAllowedAt.ShouldBe(Start.AddSeconds(10));
        state.BackoffFactor.ShouldBe(4);
        state.PausedUntil.ShouldBeNull();

        HostBudgetPolicy.Apply(host, new HostResponse(HostResponseKind.RateLimited, null, TimeSpan.FromHours(2)) { Backoff = limits }, Start)
            .PausedUntil.ShouldBe(Start.AddHours(2));
    }

    [Fact]
    public async Task The_circuit_breaker_follows_the_profile_and_can_be_off()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryCrawlHostStore(time);
        var policy = new FixedCrawlPolicy();
        policy.Overrides["off.example"] = FixedCrawlPolicy.Budget(1000) with { CircuitBreaker = new HostCircuitBreaker { Off = true } };
        policy.Overrides["strict.example"] = FixedCrawlPolicy.Budget(1000) with { CircuitBreaker = new HostCircuitBreaker { Threshold = 0.2, PauseMinutes = 5 } };
        var budget = new HostBudget(store, policy, time);

        for (var i = 0; i < 150; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var lease = (await budget.AcquireAsync("off.example", Ct)).ShouldBeOfType<HostSlot.Granted>($"request {i + 1}").Lease;
            (await lease.CompleteAsync(new HostResponse(HostResponseKind.Failure, TimeSpan.FromMilliseconds(10)), Ct)).ShouldBe(HostVerdict.Running);
            lease.Dispose();
        }

        HostVerdict verdict = HostVerdict.Running;
        var failures = 0;
        while (verdict.PausedUntil is null)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var lease = (await budget.AcquireAsync("strict.example", Ct)).ShouldBeOfType<HostSlot.Granted>().Lease;
            verdict = await lease.CompleteAsync(new HostResponse(HostResponseKind.Failure, TimeSpan.FromMilliseconds(10)), Ct);
            lease.Dispose();
            failures++;
        }

        failures.ShouldBe(20);
        verdict.ShouldBe(new HostVerdict(time.GetUtcNow().AddMinutes(5), HostBudget.BreakerPause));
    }

    [Fact]
    public void Render_per_host_is_capped_at_the_hosts_connections()
    {
        // A rendered page holds one of the host's connections for its whole render (A-13 follow-up): renderPerHost 4 on
        // an external host (1 connection) renders one page at a time, and with 4 connections four.
        var ceilings = new PolitenessCeilings(50, 16, 4);
        PolitenessProfile.Resolve(HostKind.External, new HostPoliteness { RenderPerHost = 4 }, ceilings).RenderPerHost.ShouldBe(1);
        PolitenessProfile.Resolve(HostKind.External, new HostPoliteness { RenderPerHost = 4, MaxConnections = 4 }, ceilings).RenderPerHost.ShouldBe(4);
        PolitenessProfile.Resolve(HostKind.External, new HostPoliteness { RenderPerHost = 4, MaxConnections = 2 }, ceilings).RenderPerHost.ShouldBe(2);
    }
}
