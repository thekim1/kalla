using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Pipelines.Crawl;
using Ume.Rag.Worker.Connectors.Hosts;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>The host budget of connectors design §6.3–§6.4: interval, slots, back-off, pause, breaker, connection cap.</summary>
public sealed class HostBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 23, 0, 0, TimeSpan.Zero);

    private static CrawlHostState Host(HostKind kind = HostKind.External) =>
        new() { Host = "example.org", Kind = kind, RequestsPerSecond = kind == HostKind.Municipal ? 10 : 1, MaxConnections = kind == HostKind.Municipal ? 4 : 1 };

    [Fact]
    public void Budgets_are_10_per_second_and_4_connections_municipal_1_and_1_external_and_overridable()
    {
        var policy = new CrawlPolicy(["umea.se", "*.umea.se"], new Dictionary<string, HostPoliteness> { ["example.org"] = new() { RequestsPerSecond = 2 } });
        Budget(policy, "www.umea.se").ShouldBe((10.0, 4));
        Budget(policy, "other.example").ShouldBe((1.0, 1));
        // Overrides are keyed by host name: a port does not matter.
        Budget(policy, "example.org:8443").ShouldBe((2.0, 1));
        policy.KindOf("umea.se").ShouldBe(HostKind.Municipal);
        policy.KindOf("www.umea.se:8443").ShouldBe(HostKind.Municipal);
        policy.KindOf("notumea.se").ShouldBe(HostKind.External);
        policy.KindOf("umea.se.evil.example").ShouldBe(HostKind.External);
    }

    [Fact]
    public void The_interval_is_the_largest_of_rate_crawl_delay_and_latency_times_the_backoff()
    {
        var municipal = PolitenessProfile.Municipal;
        var polite = PolitenessProfile.Polite;
        HostBudgetPolicy.Interval(Host(HostKind.Municipal), municipal).ShouldBe(TimeSpan.FromMilliseconds(100));
        HostBudgetPolicy.Interval(Host(), polite).ShouldBe(TimeSpan.FromSeconds(1));
        // Crawl-delay only lowers the rate, and counts up to 60 s.
        HostBudgetPolicy.Interval(Host() with { CrawlDelaySeconds = 5 }, polite).ShouldBe(TimeSpan.FromSeconds(5));
        HostBudgetPolicy.Interval(Host() with { CrawlDelaySeconds = 600 }, polite).ShouldBe(TimeSpan.FromSeconds(60));
        // Latency: a quarter on municipal hosts, all of it on external ones.
        HostBudgetPolicy.Interval(Host(HostKind.Municipal) with { LatencyEwmaMs = 2000 }, municipal).ShouldBe(TimeSpan.FromMilliseconds(500));
        HostBudgetPolicy.Interval(Host() with { LatencyEwmaMs = 2000 }, polite).ShouldBe(TimeSpan.FromSeconds(2));
        HostBudgetPolicy.Interval(Host() with { BackoffFactor = 4 }, polite).ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Slots_are_spaced_by_the_interval_so_no_second_holds_more_than_the_rate()
    {
        var state = Host(HostKind.Municipal);
        var slots = new List<DateTimeOffset>();
        for (var i = 0; i < 100; i++)
        {
            // A wait long enough for all 100 (10 s).
            (state, var slot) = HostBudgetPolicy.TakeSlot(state, HostBudgetPolicy.Interval(state, PolitenessProfile.Municipal), Start, TimeSpan.FromSeconds(10));
            slot!.Taken.ShouldBeTrue();
            slots.Add(slot.At);
        }

        slots[0].ShouldBe(Start);
        slots.Zip(slots.Skip(1)).ShouldAllBe(p => p.Second - p.First == TimeSpan.FromMilliseconds(100));
        // Negative proof of the rate: in any one-second window at most 10 requests start.
        foreach (var slot in slots)
        {
            slots.Count(s => s >= slot && s < slot.AddSeconds(1)).ShouldBeLessThanOrEqualTo(10);
        }
    }

    [Fact]
    public void A_blocked_or_paused_host_gets_no_slot_and_an_idle_one_starts_now()
    {
        var second = TimeSpan.FromSeconds(1);
        HostBudgetPolicy.TakeSlot(Host() with { BlockedAt = Start }, second, Start, second).Slot.ShouldBeNull();
        HostBudgetPolicy.TakeSlot(Host() with { PausedUntil = Start.AddMinutes(1) }, second, Start, second).Slot.ShouldBeNull();
        HostBudgetPolicy.TakeSlot(Host() with { PausedUntil = Start.AddMinutes(-1), NextAllowedAt = Start.AddHours(-1) }, second, Start, second)
            .Slot.ShouldBe(new SlotTake(Start, Taken: true));
    }

    [Fact]
    public void A_slot_further_away_than_the_wait_is_not_taken_and_the_row_stays_as_it_was()
    {
        var second = TimeSpan.FromSeconds(1);
        var busy = Host() with { NextAllowedAt = Start.AddSeconds(10) };

        var (state, slot) = HostBudgetPolicy.TakeSlot(busy, second, Start, second);

        slot.ShouldBe(new SlotTake(Start.AddSeconds(10), Taken: false));
        state.ShouldBe(busy);
        // Exactly the wait away: taken, and the next free time moves by one interval.
        (state, slot) = HostBudgetPolicy.TakeSlot(Host() with { NextAllowedAt = Start.AddSeconds(1) }, second, Start, second);
        slot.ShouldBe(new SlotTake(Start.AddSeconds(1), Taken: true));
        state.NextAllowedAt.ShouldBe(Start.AddSeconds(2));
    }

    [Fact]
    public void A_rate_limit_waits_for_retry_after_and_doubles_the_backoff_to_16_and_successes_decay_it()
    {
        var state = Host();
        state = HostBudgetPolicy.Apply(state, new HostResponse(HostResponseKind.RateLimited, null, TimeSpan.FromSeconds(30)), Start);
        state.NextAllowedAt.ShouldBe(Start.AddSeconds(30));
        state.BackoffFactor.ShouldBe(2);
        state = HostBudgetPolicy.Apply(state, new HostResponse(HostResponseKind.RateLimited, null), Start);
        state.NextAllowedAt.ShouldBe(Start.AddSeconds(60));
        for (var i = 0; i < 10; i++)
        {
            state = HostBudgetPolicy.Apply(state, new HostResponse(HostResponseKind.RateLimited, null), Start);
        }

        state.BackoffFactor.ShouldBe(16);
        state.PausedUntil.ShouldBeNull();

        state = HostBudgetPolicy.Apply(state, new HostResponse(HostResponseKind.Success, TimeSpan.FromMilliseconds(100)), Start);
        state.BackoffFactor.ShouldBe(16 * 0.95, 1e-9);
        for (var i = 0; i < 200; i++)
        {
            state = HostBudgetPolicy.Apply(state, new HostResponse(HostResponseKind.Success, TimeSpan.FromMilliseconds(100)), Start);
        }

        state.BackoffFactor.ShouldBe(1);
        state.LatencyEwmaMs.ShouldBe(100);
    }

    [Fact]
    public void A_retry_after_above_one_hour_pauses_the_host()
    {
        var state = HostBudgetPolicy.Apply(Host(), new HostResponse(HostResponseKind.RateLimited, null, TimeSpan.FromHours(2)), Start);

        state.PausedUntil.ShouldBe(Start.AddHours(2));
    }

    [Fact]
    public async Task A_slot_more_than_a_second_away_requeues_at_the_slot_without_waiting()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryCrawlHostStore(time);
        var budget = new HostBudget(store, new FixedCrawlPolicy(), time);
        await budget.EnsureHostAsync("example.org", TestContext.Current.CancellationToken);
        store["example.org"] = store["example.org"] with { CrawlDelaySeconds = 5 };

        var first = (await budget.AcquireAsync("example.org", TestContext.Current.CancellationToken)).ShouldBeOfType<HostSlot.Granted>();
        await first.Lease.CompleteAsync(new HostResponse(HostResponseKind.Neutral, null), TestContext.Current.CancellationToken);
        first.Lease.Dispose();

        (await budget.AcquireAsync("example.org", TestContext.Current.CancellationToken))
            .ShouldBe(new HostSlot.Later(TimeSpan.FromSeconds(5), HostBudget.Politeness));

        time.Advance(TimeSpan.FromSeconds(10));
        var third = (await budget.AcquireAsync("example.org", TestContext.Current.CancellationToken)).ShouldBeOfType<HostSlot.Granted>();
        third.Lease.Dispose();
    }

    [Fact]
    public async Task Blocked_and_paused_hosts_are_refused()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryCrawlHostStore(time);
        var budget = new HostBudget(store, new FixedCrawlPolicy(), time);
        await budget.EnsureHostAsync("a.example", TestContext.Current.CancellationToken);
        await budget.EnsureHostAsync("b.example", TestContext.Current.CancellationToken);
        store["a.example"] = store["a.example"] with { BlockedAt = Start };
        store["b.example"] = store["b.example"] with { PausedUntil = Start.AddMinutes(5) };

        (await budget.AcquireAsync("a.example", TestContext.Current.CancellationToken)).ShouldBe(new HostSlot.Refused(HostBudget.Blocked, null));
        (await budget.AcquireAsync("b.example", TestContext.Current.CancellationToken)).ShouldBe(new HostSlot.Refused(HostBudget.Paused, Start.AddMinutes(5)));
    }

    [Fact]
    public async Task Fifty_failures_in_the_last_hundred_responses_pause_the_host_for_30_minutes()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryCrawlHostStore(time);
        var policy = new FixedCrawlPolicy();
        policy.Overrides["example.org"] = FixedCrawlPolicy.Budget(1000);
        var budget = new HostBudget(store, policy, time);
        HostVerdict verdict = HostVerdict.Running;
        for (var i = 0; i < 99 && verdict.PausedUntil is null; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var lease = (await budget.AcquireAsync("example.org", TestContext.Current.CancellationToken)).ShouldBeOfType<HostSlot.Granted>().Lease;
            // Every other response fails: the 50th failure is response 99.
            verdict = await lease.CompleteAsync(new HostResponse(i % 2 == 0 ? HostResponseKind.Failure : HostResponseKind.Success, TimeSpan.FromMilliseconds(10)), TestContext.Current.CancellationToken);
            lease.Dispose();
            if (i < 98)
            {
                verdict.PausedUntil.ShouldBeNull($"response {i + 1}");
            }
        }

        verdict.ShouldBe(new HostVerdict(time.GetUtcNow().AddMinutes(30), HostBudget.BreakerPause));
        (await budget.AcquireAsync("example.org", TestContext.Current.CancellationToken)).ShouldBeOfType<HostSlot.Refused>().Reason.ShouldBe(HostBudget.Paused);
    }

    [Fact]
    public async Task Never_more_than_the_connection_cap_is_in_flight()
    {
        // Real time: a 1000 req/s override makes the slot waits a millisecond; the gates below decide the overlap.
        var store = new InMemoryCrawlHostStore(TimeProvider.System);
        var policy = new FixedCrawlPolicy("kommun.example");
        policy.Overrides["kommun.example"] = new HostPoliteness { RequestsPerSecond = 1000 };
        var budget = new HostBudget(store, policy, TimeProvider.System);
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            var lease = (await budget.AcquireAsync("kommun.example", TestContext.Current.CancellationToken)).ShouldBeOfType<HostSlot.Granted>().Lease;
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            await gate.Task;
            Interlocked.Decrement(ref inFlight);
            lease.Dispose();
        }, TestContext.Current.CancellationToken)).ToList();

        while (Volatile.Read(ref inFlight) < HostBudgetPolicy.MunicipalConnections)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        // Four hold their connection; the other eight wait for one.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Volatile.Read(ref inFlight).ShouldBe(HostBudgetPolicy.MunicipalConnections);
        gate.SetResult();
        await Task.WhenAll(requests);
        maxInFlight.ShouldBe(HostBudgetPolicy.MunicipalConnections);
    }

    [Fact]
    public void The_platform_crawl_settings_group_is_read()
    {
        var (egress, crawl) = PlatformCrawlSettings.Parse("""
            { "allowedPorts": [8443], "blockedHosts": ["bad.example"], "municipalHosts": ["*.umea.se"],
              "hostOverrides": { "www.umea.se": { "requestsPerSecond": 2.5, "maxConnections": 2 } }, "freshnessHours": 12,
              "incompleteRunsAlert": 5 }
            """);

        egress.AllowedPorts.ShouldBe([8443]);
        egress.BlockedHosts.ShouldContain("BAD.example");
        crawl.KindOf("www.umea.se").ShouldBe(HostKind.Municipal);
        Budget(crawl, "www.umea.se").ShouldBe((2.5, 2));
        crawl.IncompleteRunsAlert.ShouldBe(5);
        PlatformCrawlSettings.Parse("{}").Crawl.KindOf("www.umea.se").ShouldBe(HostKind.External);
        PlatformCrawlSettings.Parse("{}").Crawl.IncompleteRunsAlert.ShouldBe(3);
    }

    private static (double Rate, int Connections) Budget(CrawlPolicy policy, string host)
    {
        var profile = policy.ProfileOf(host);
        return (profile.RequestsPerSecond, profile.MaxConnections);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
