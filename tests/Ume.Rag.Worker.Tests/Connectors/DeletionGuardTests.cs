using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Deletion;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// Traversal completeness by P1a's strict rule, deletion candidates, the guard's arithmetic and the proposal lifecycle
/// (connectors design §5.4, §13; weakness review §3 "Phasing"), including C7 (a page-limit run deletes nothing).
/// </summary>
public sealed class DeletionGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly Guid Earlier = Guid.NewGuid();

    private static CrawlRunRecord Record(int seeds = 200, int seedsFailed = 0, bool limitHit = false, bool paused = false) => new()
    {
        RunId = Run,
        Classification = "public",
        SourceId = Guid.NewGuid(),
        MaxPages = 10_000,
        MaxDepth = 10,
        Seeds = seeds,
        SeedsFailed = seedsFailed,
        LimitHit = limitHit,
        PausedHosts = paused
            ? new Dictionary<string, HostPause> { ["www.umea.se"] = new(1, "crawl.breaker", Now.AddMinutes(30)) }
            : new Dictionary<string, HostPause>(),
    };

    private static List<SourceDocument> Documents(int count, int reached) =>
        [.. Enumerable.Range(0, count).Select(i => new SourceDocument
        {
            Id = Guid.NewGuid(),
            CanonicalIdentity = $"https://www.umea.se/p{i}",
            LastRunId = i < reached ? Run : Earlier,
            LastSeenAt = i < reached ? Now : Now.AddDays(-7),
        })];

    [Fact]
    public void A_traversal_is_complete_only_when_every_seed_resolved_and_nothing_is_open_paused_or_limited()
    {
        var complete = TraversalCompleteness.Evaluate(Record(), 0, false);
        complete.Complete.ShouldBeTrue();
        complete.Reasons.ShouldBeEmpty();
        complete.RunFailure.ShouldBeNull();
        complete.RunDegradation.ShouldBeNull();

        TraversalCompleteness.Evaluate(Record(), 3, false).Reasons.ShouldBe([TraversalCompleteness.ClaimsPending]);
        TraversalCompleteness.Evaluate(Record(paused: true), 0, false).ShouldSatisfyAllConditions(
            v => v.Complete.ShouldBeFalse(), v => v.RunDegradation.ShouldBe(TraversalCompleteness.HostPaused));
        TraversalCompleteness.Evaluate(Record(), 0, cancelled: true).Reasons.ShouldBe([TraversalCompleteness.Cancelled]);
    }

    [Fact]
    public void P1a_is_strict_one_failing_seed_of_200_makes_the_traversal_incomplete()
    {
        // P1b's ratio rule (≥ 99 % resolved) would call this complete; P1a keeps the stricter rule (weakness review §3 "Phasing").
        var verdict = TraversalCompleteness.Evaluate(Record(seedsFailed: 1), 0, false);

        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldBe([TraversalCompleteness.SeedsFailed]);
        verdict.RunFailure.ShouldBeNull();
    }

    [Fact]
    public void No_resolved_seed_fails_the_run_with_seeds_unreachable()
    {
        TraversalCompleteness.Evaluate(Record(seeds: 5, seedsFailed: 5), 0, false).RunFailure.ShouldBe(TraversalCompleteness.SeedsUnreachable);
        TraversalCompleteness.Evaluate(Record(seeds: 0), 0, false).RunFailure.ShouldBe(TraversalCompleteness.SeedsUnreachable);
    }

    [Fact]
    public void C7_a_page_limit_run_deletes_nothing_it_did_not_reach()
    {
        // Eneo's partial crawl: the run hit maxPages after reaching 40 of 100 documents.
        var documents = Documents(100, reached: 40);
        var verdict = TraversalCompleteness.Evaluate(Record(limitHit: true), 0, false);

        var candidates = DeletionCandidates.FromTraversal(Run, documents, verdict, new Dictionary<Guid, string>(), new HashSet<Guid>(), Now);
        var decision = DeletionGuard.Evaluate(candidates, DeletionCandidates.Live(documents), emptyListing: false, GuardSettings.Default);

        verdict.Complete.ShouldBeFalse();
        verdict.RunDegradation.ShouldBe(TraversalCompleteness.LimitPages);
        candidates.ShouldBeEmpty();
        decision.Apply.ShouldBeEmpty();
        decision.Wait.ShouldBeEmpty();
    }

    [Fact]
    public void C7_explicit_gone_answers_still_count_in_a_page_limit_run()
    {
        var documents = Documents(100, reached: 40);
        var gone = new Dictionary<Guid, string> { [documents[0].Id] = DeletionReasons.Http410 };
        var verdict = TraversalCompleteness.Evaluate(Record(limitHit: true), 0, false);

        var candidates = DeletionCandidates.FromTraversal(Run, documents, verdict, gone, new HashSet<Guid>(), Now);

        candidates.ShouldBe([new DeletionCandidate(documents[0].Id, DeletionReasons.Http410)]);
        DeletionGuard.Evaluate(candidates, 100, false, GuardSettings.Default).Apply.ShouldBe(candidates);
    }

    [Fact]
    public void A_complete_traversal_proposes_unreached_documents_but_never_held_deleted_or_failed_seed_ones()
    {
        var documents = Documents(10, reached: 6);
        documents[6] = documents[6] with { HeldAt = Now.AddDays(-1), HeldBy = "admin" };
        documents[7] = documents[7] with { DeletedAt = Now.AddDays(-2) };
        var failedSeeds = new HashSet<Guid> { documents[8].Id };

        var candidates = DeletionCandidates.FromTraversal(Run, documents, TraversalCompleteness.Evaluate(Record(), 0, false), new Dictionary<Guid, string>(), failedSeeds, Now);

        candidates.ShouldBe([new DeletionCandidate(documents[9].Id, DeletionReasons.NotSeen)]);
        DeletionCandidates.Live(documents).ShouldBe(8);
    }

    [Fact]
    public void Persistent_failure_needs_four_runs_and_fourteen_days()
    {
        var documents = Documents(3, reached: 3);
        documents[0] = documents[0] with { ConsecutiveFailures = 4, LastSeenAt = Now.AddDays(-14) };
        documents[1] = documents[1] with { ConsecutiveFailures = 4, LastSeenAt = Now.AddDays(-13) };
        documents[2] = documents[2] with { ConsecutiveFailures = 3, LastSeenAt = Now.AddDays(-30) };
        var incomplete = TraversalCompleteness.Evaluate(Record(seedsFailed: 1), 0, false);

        DeletionCandidates.FromTraversal(Run, documents, incomplete, new Dictionary<Guid, string>(), new HashSet<Guid>(), Now)
            .ShouldBe([new DeletionCandidate(documents[0].Id, DeletionReasons.PersistentFailure)]);
    }

    [Theory]
    [InlineData(100, 20, false, GuardRule.None)]
    [InlineData(100, 21, false, GuardRule.Ratio)]
    [InlineData(10, 4, false, GuardRule.None)]
    [InlineData(10, 5, false, GuardRule.Ratio)]
    [InlineData(50_000, 500, false, GuardRule.None)]
    [InlineData(50_000, 501, false, GuardRule.Absolute)]
    [InlineData(40, 0, true, GuardRule.EmptySource)]
    [InlineData(0, 0, true, GuardRule.None)]
    public void The_guard_trips_on_ratio_with_a_minimum_on_an_absolute_count_and_on_an_empty_source(int live, int n, bool empty, GuardRule rule)
    {
        var candidates = Enumerable.Range(0, n).Select(_ => new DeletionCandidate(Guid.NewGuid(), DeletionReasons.NotSeen)).ToList();

        var decision = DeletionGuard.Evaluate(candidates, live, empty, GuardSettings.Default);

        decision.Rule.ShouldBe(rule);
        decision.NonExempt.ShouldBe(n);
        (decision.Tripped ? decision.Wait : decision.Apply).Count.ShouldBe(n);
    }

    [Fact]
    public void Tripped_the_exempt_candidates_are_still_applied_and_the_rest_wait()
    {
        var candidates = Enumerable.Range(0, 25).Select(_ => new DeletionCandidate(Guid.NewGuid(), DeletionReasons.NotSeen))
            .Append(new DeletionCandidate(Guid.NewGuid(), DeletionReasons.RemovedFromList))
            .Append(new DeletionCandidate(Guid.NewGuid(), DeletionReasons.UploadDeleted))
            .ToList();

        // 25 % of 100 removed: pause (§18 "25 % removed → pause").
        var decision = DeletionGuard.Evaluate(candidates, 100, false, GuardSettings.Default);

        decision.Rule.ShouldBe(GuardRule.Ratio);
        decision.NonExempt.ShouldBe(25);
        decision.Apply.Select(c => c.Reason).ShouldBe([DeletionReasons.RemovedFromList, DeletionReasons.UploadDeleted]);
        decision.Wait.Count.ShouldBe(25);
        // Exempt candidates alone never trip the guard.
        DeletionGuard.Evaluate([.. Enumerable.Range(0, 90).Select(_ => new DeletionCandidate(Guid.NewGuid(), DeletionReasons.ScopeNarrowed))], 100, false, GuardSettings.Default)
            .Tripped.ShouldBeFalse();
    }

    [Fact]
    public void Confirm_drops_documents_seen_again_and_deletes_the_rest()
    {
        var documents = Documents(3, reached: 0);
        var sourceId = Guid.NewGuid();
        var candidates = documents.Select(d => new DeletionCandidate(d.Id, DeletionReasons.NotSeen)).ToList();
        var proposed = DeletionProposalLifecycle.Reconcile(Run, sourceId, "public", new GuardDecision(GuardRule.Ratio, 3, 3, [], candidates), Now);
        proposed.Delete.ShouldBeEmpty();
        proposed.Proposals.ShouldAllBe(p => p.Decision == ProposalDecisions.Pending && !p.Exempt);

        var current = documents.ToDictionary(d => d.Id);
        current[documents[0].Id] = documents[0] with { LastSeenAt = Now.AddDays(1) };
        current[documents[1].Id] = documents[1] with { DeletedAt = Now };
        var confirmed = DeletionProposalLifecycle.Confirm(proposed.Proposals, current, Now, "admin@umea.se", Now.AddDays(2));

        confirmed.Delete.ShouldBe([documents[2].Id]);
        confirmed.Proposals.Select(p => p.Decision).ShouldBe([ProposalDecisions.Dropped, ProposalDecisions.Dropped, ProposalDecisions.Confirmed]);
        confirmed.Proposals.ShouldAllBe(p => p.DecidedBy == "admin@umea.se");
    }

    [Fact]
    public void Reject_holds_the_documents_and_degrades_the_run_and_a_newer_run_supersedes()
    {
        var documents = Documents(2, reached: 0);
        var waiting = DeletionProposalLifecycle.Reconcile(Run, Guid.NewGuid(), "public",
            new GuardDecision(GuardRule.Ratio, 2, 2, [], [.. documents.Select(d => new DeletionCandidate(d.Id, DeletionReasons.NotSeen))]), Now).Proposals;

        var rejected = DeletionProposalLifecycle.Reject(waiting, "admin@umea.se", Now);
        rejected.Delete.ShouldBeEmpty();
        rejected.Holds.ShouldBe(documents.Select(d => d.Id));
        rejected.RunDegradation.ShouldBe(DeletionProposalLifecycle.Rejected);

        var superseded = DeletionProposalLifecycle.Supersede(waiting, Now);
        superseded.Proposals.ShouldAllBe(p => p.Decision == ProposalDecisions.Superseded);
        superseded.RunDegradation.ShouldBe(DeletionProposalLifecycle.SupersededReason);
    }

    [Fact]
    public void A_held_document_seen_again_loses_its_hold_and_a_deleted_one_is_revived_within_14_days()
    {
        var held = Documents(1, 0)[0] with { HeldAt = Now, HeldBy = "admin", ConsecutiveFailures = 2 };
        var seen = DocumentLifecycle.Seen(held, Run, Now, out var revived);
        seen.HeldAt.ShouldBeNull();
        seen.HeldBy.ShouldBeNull();
        seen.ConsecutiveFailures.ShouldBe((short)0);
        seen.LastRunId.ShouldBe(Run);
        revived.ShouldBeFalse();

        var deleted = DocumentLifecycle.SoftDelete(Documents(1, 0)[0], Now);
        deleted.PurgeAfter.ShouldBe(Now.AddDays(14));
        var back = DocumentLifecycle.Seen(deleted, Run, Now.AddDays(13), out revived);
        revived.ShouldBeTrue();
        back.DeletedAt.ShouldBeNull();
        back.PurgeAfter.ShouldBeNull();

        DocumentLifecycle.Seen(deleted, Run, Now.AddDays(15), out revived).DeletedAt.ShouldBe(Now);
        revived.ShouldBeFalse();
        DocumentLifecycle.ReleaseHold(held).HeldAt.ShouldBeNull();
    }

    [Fact]
    public void A_url_list_proposes_removed_urls_as_exempt_and_a_partial_listing_nothing()
    {
        var documents = Documents(3, 3);
        var listed = new HashSet<string> { documents[0].CanonicalIdentity, documents[1].CanonicalIdentity };

        var candidates = DeletionCandidates.FromListing(documents, listed, true, DeletionReasons.RemovedFromList);
        candidates.ShouldBe([new DeletionCandidate(documents[2].Id, DeletionReasons.RemovedFromList)]);
        candidates[0].Exempt.ShouldBeTrue();
        DeletionCandidates.FromListing(documents, listed, false, DeletionReasons.RemovedFromList).ShouldBeEmpty();
    }

    [Fact]
    public void Three_incomplete_traversals_in_a_row_raise_the_alert()
    {
        TraversalCompleteness.ShouldAlert([false, false, false]).ShouldBeTrue();
        TraversalCompleteness.ShouldAlert([false, false, true, false]).ShouldBeFalse();
        TraversalCompleteness.ShouldAlert([false, false]).ShouldBeFalse();
    }
}
