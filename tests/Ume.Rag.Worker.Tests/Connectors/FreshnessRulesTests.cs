using System.Diagnostics.Metrics;
using System.Text.Json;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Deletion;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// The freshness rules without a site (P1b item 4; connectors design §5.4, §5.5; weakness review §3): the <c>lastmod</c>
/// sample's comparison and its <c>edit-site-profile</c> proposal on fixtures, <c>revalidateEvery</c>, the crawl report's
/// completeness and removal sections, <c>demote</c> factors and the incomplete-runs gauge.
/// </summary>
public sealed class FreshnessRulesTests
{
    private static readonly DateTimeOffset First = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Lastmod = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>500 pairs fetched <paramref name="gap"/> apart: <paramref name="silent"/> changed without a newer lastmod, <paramref name="announced"/> with one.</summary>
    private static List<LastmodSamplePair> Sample(int silent, int announced = 20, TimeSpan? gap = null, int size = LastmodSample.Size) =>
        [.. Enumerable.Range(0, size).Select(i =>
        {
            var second = First + (gap ?? TimeSpan.FromDays(7));
            var changed = i < silent + announced;
            var newer = i >= silent && i < silent + announced;
            return new LastmodSamplePair(
                $"https://www.umea.se/p{i}",
                new LastmodObservation($"h{i}", Lastmod, First),
                new LastmodObservation(changed ? $"h{i}-2" : $"h{i}", newer ? Lastmod.AddMonths(9) : Lastmod, second));
        })];

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(50, false)]
    public void The_sample_passes_with_at_most_2_percent_changed_without_a_newer_lastmod(int silent, bool passed)
    {
        var result = LastmodSample.Compare(Sample(silent));

        result.Passed.ShouldBe(passed);
        result.Compared.ShouldBe(500);
        result.Changed.ShouldBe(silent + 20);
        result.ChangedWithoutNewerLastmod.ShouldBe(silent);
        result.Examples.Count.ShouldBe(Math.Min(silent, 10));
        result.Refusal.ShouldBeNull();
    }

    [Fact]
    public void Changes_announced_by_a_newer_lastmod_never_count_and_a_missing_lastmod_does()
    {
        // 300 changed pages, all with a newer lastmod: reliable.
        LastmodSample.Compare(Sample(0, announced: 300)).Passed.ShouldBeTrue();

        var noLastmod = Sample(0, announced: 0);
        noLastmod[0] = noLastmod[0] with
        {
            First = noLastmod[0].First with { Lastmod = null },
            Second = new LastmodObservation("changed", null, First.AddDays(8)),
        };
        LastmodSample.Compare(noLastmod).ChangedWithoutNewerLastmod.ShouldBe(1);
    }

    [Fact]
    public void Pairs_fetched_less_than_7_days_apart_are_not_compared_and_a_small_sample_cannot_pass()
    {
        var early = LastmodSample.Compare(Sample(0, gap: TimeSpan.FromDays(6.9)));
        early.Compared.ShouldBe(0);
        early.Passed.ShouldBeFalse();
        early.Refusal.ShouldBe(LastmodSample.TooSmall);

        var small = LastmodSample.Compare(Sample(0, size: 499));
        small.Passed.ShouldBeFalse();
        small.Refusal.ShouldBe(LastmodSample.TooSmall);
    }

    [Fact]
    public void Pick_draws_distinct_urls_up_to_the_sample_size()
    {
        var urls = Enumerable.Range(0, 24_184).Select(i => $"https://www.umea.se/p{i}").ToList();

        var picked = LastmodSample.Pick(urls.Concat(urls));

        picked.Count.ShouldBe(500);
        picked.ShouldBeUnique();
        picked.ShouldAllBe(u => urls.Contains(u));
        LastmodSample.Pick(urls.Take(20)).Count.ShouldBe(20);
    }

    [Fact]
    public void A_passed_sample_proposes_lastmod_reliable_through_edit_site_profile_and_nothing_else_does()
    {
        var profiles = new InMemorySiteProfiles();
        var applied = profiles.Set("public", "www.umea.se", lastmodReliable: false, number: 3);
        var passed = LastmodSample.Compare(Sample(10));

        var proposal = LastmodSample.Propose(passed, "www.umea.se", applied).ShouldNotBeNull();
        proposal.Kind.ShouldBe(CommandKinds.EditSiteProfile);
        proposal.SiteProfileId.ShouldBe(applied.SiteProfileId);
        proposal.BaseVersion.ShouldBe(3);
        proposal.PresetId.ShouldBe("sitevision");
        proposal.Profile.ShouldBe(applied.Profile with { LastmodReliable = true });

        var job = LastmodSample.ProposalJob(proposal, Guid.NewGuid(), "public");
        job.Class.ShouldBe("command");
        job.DedupKey.ShouldBe(JobDedupKeys.SiteProfile(applied.SiteProfileId));
        ContractJson.Deserialize<EditSiteProfileCommand>(JsonDocument.Parse(job.Payload).RootElement).Profile.LastmodReliable.ShouldBeTrue();

        // Negative proofs: a failed sample, a host without a profile and a flag already set propose nothing.
        LastmodSample.Propose(LastmodSample.Compare(Sample(11)), "www.umea.se", applied).ShouldBeNull();
        LastmodSample.Propose(passed, "www.umea.se", null).ShouldBeNull();
        LastmodSample.Propose(passed, "www.umea.se", profiles.Set("public", "www.umea.se", lastmodReliable: true)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 4, false, false, false)]
    [InlineData(3, 4, false, false, true)]
    [InlineData(7, 4, false, false, true)]
    [InlineData(4, 4, false, false, false)]
    [InlineData(0, 1, false, false, true)]
    [InlineData(1, 4, true, false, true)]
    [InlineData(3, 4, false, true, false)]
    [InlineData(3, 4, true, true, false)]
    public void Every_revalidate_every_th_traversal_and_every_forced_one_revalidates_and_incremental_runs_never(
        int before, int every, bool forced, bool incremental, bool revalidates) =>
        CrawlFreshness.Revalidates(before, every, forced, incremental).ShouldBe(revalidates);

    [Fact]
    public void A_continue_run_without_seeds_is_never_complete_never_unreachable_and_not_counted_as_a_traversal()
    {
        var run = new CrawlRunRecord { RunId = Guid.NewGuid(), Classification = "public", SourceId = Guid.NewGuid(), MaxPages = 100, MaxDepth = 3 };

        var verdict = TraversalCompleteness.Evaluate(run, 0, false, CompletenessRule.Ratio, incremental: false, continued: true);
        var section = CrawlReportSections.Completeness(run, verdict, CompletenessRule.Ratio, false, 0, [new(false, false), new(true, false)], 3, continued: true);

        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldBe([TraversalCompleteness.Continued]);
        verdict.RunFailure.ShouldBeNull();
        section.ConsecutiveIncomplete.ShouldBe(1, "the continue run itself is left out, as an incremental run is");
        TraversalCompleteness.Evaluate(run, 0, false, CompletenessRule.Ratio, incremental: false).RunFailure
            .ShouldBe(TraversalCompleteness.SeedsUnreachable, "a sync without seeds still fails");
    }

    [Fact]
    public void A_continue_run_whose_carried_seeds_all_fail_again_is_unreachable()
    {
        var run = new CrawlRunRecord { RunId = Guid.NewGuid(), Classification = "public", SourceId = Guid.NewGuid(), MaxPages = 100, MaxDepth = 3, Seeds = 2 };

        TraversalCompleteness.Evaluate(run with { SeedsFailed = 1 }, 0, false, CompletenessRule.Ratio, incremental: false, continued: true).RunFailure
            .ShouldBeNull();
        TraversalCompleteness.Evaluate(run with { SeedsFailed = 2 }, 0, false, CompletenessRule.Ratio, incremental: false, continued: true).RunFailure
            .ShouldBe(TraversalCompleteness.SeedsUnreachable);
    }

    [Fact]
    public void The_report_has_the_completeness_and_removal_sections()
    {
        var run = new CrawlRunRecord
        {
            RunId = Guid.NewGuid(),
            Classification = "public",
            SourceId = Guid.NewGuid(),
            MaxPages = 20_000,
            MaxDepth = 10,
            Seeds = 14_100,
            SeedsFailed = 200,
            PausedHosts = new Dictionary<string, HostPause> { ["www.umea.se"] = new(1, CrawlConnector.RobotsUnreachable, null) },
        };
        var verdict = TraversalCompleteness.Evaluate(run, 0, false, CompletenessRule.Ratio, incremental: false);
        TraversalHistoryEntry[] earlier = [new(false, true), new(false, false), new(false, false), new(true, false)];

        var section = CrawlReportSections.Completeness(run, verdict, CompletenessRule.Ratio, false, 200, earlier, 3);

        section.Complete.ShouldBeFalse();
        section.Reasons.ShouldBe([TraversalCompleteness.HostPaused, TraversalCompleteness.SeedsFailed]);
        section.Rule.ShouldBe("ratio");
        (section.Seeds, section.SeedsResolved, section.SeedsFailed, section.FailedProtected).ShouldBe((14_100, 13_900, 200, 200));
        section.ResolvedShare.ShouldBe(0.9858);
        section.PausedHosts.ShouldBe(new Dictionary<string, string> { ["www.umea.se"] = CrawlConnector.RobotsUnreachable });
        section.ConsecutiveIncomplete.ShouldBe(3);
        section.Alert.ShouldBeTrue();
        CrawlReportSections.Completeness(run, verdict, CompletenessRule.Ratio, false, 0, [], 3).Alert.ShouldBeFalse();

        var removal = RemovalSection.From([
            new FetchOutcome.Gone("http.404"), new FetchOutcome.Gone("http.404"), new FetchOutcome.Gone("http.410"),
            new FetchOutcome.NotModified(Validators.None), new FetchOutcome.Failed("http.403"), new FetchOutcome.Skipped("robots.disallow"),
        ]);
        (removal.Checked, removal.Gone, removal.Kept).ShouldBe((6, 3, 1));
        removal.GoneByReason.ShouldBe(new Dictionary<string, int> { ["http.404"] = 2, ["http.410"] = 1 });
        removal.Other.ShouldBe(new Dictionary<string, int> { ["http.403"] = 1, ["robots.disallow"] = 1 });
        // Data only: the sections serialise for Runs.Report.
        JsonSerializer.Serialize(new { completeness = section, removals = removal }, JsonSerializerOptions.Web).ShouldContain("\"resolvedShare\":0.9858");
    }

    [Fact]
    public void Demote_patterns_change_no_scope_and_the_lowest_matching_factor_applies()
    {
        var scope = CrawlScope.Create(new CrawlSettings
        {
            StartUrls = ["https://www.umea.se/"],
            Demote = [new DemoteRule { Pattern = "www.umea.se/protokoll/**" }, new DemoteRule { Pattern = "www.umea.se/protokoll/20*/**", Factor = 0.2 }],
        });

        scope.DemoteFactor("https://www.umea.se/protokoll/2019/kf.pdf").ShouldBe(0.2);
        scope.DemoteFactor("https://www.umea.se/protokoll/aldre/kf.pdf").ShouldBe(0.5);
        scope.DemoteFactor("https://www.umea.se/bygga").ShouldBe(1.0);
        scope.Check("https://www.umea.se/protokoll/2019/kf.pdf").Admitted.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => CrawlScope.Create(new CrawlSettings
        {
            StartUrls = ["https://www.umea.se/"],
            Demote = [new DemoteRule { Pattern = "re:(" }],
        }));
    }

    [Fact]
    public void The_incomplete_runs_gauge_records_the_worst_source()
    {
        var recorded = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "rag.crawl.incomplete_runs")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => recorded.Add(value));
        listener.Start();

        ConnectorTestKit.Metrics().IncompleteRuns(TraversalCompleteness.ConsecutiveIncomplete([new(false, false), new(false, false), new(false, false)]));

        recorded.ShouldBe([3]);
    }
}
