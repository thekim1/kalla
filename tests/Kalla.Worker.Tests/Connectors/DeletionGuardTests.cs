using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Deletion;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// Traversal completeness by P1b's ratio rule and P1a's strict rule, deletion candidates, the guard's arithmetic, the
/// proposal lifecycle and the incomplete-traversal alert (connectors design §5.4, §13, §18; weakness review §3), including
/// C7 (a page-limit run deletes nothing) under the ratio rule.
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

    private static TraversalVerdict Evaluate(
        CrawlRunRecord run, int openRows, bool cancelled, CompletenessRule rule = CompletenessRule.Ratio, bool incremental = false) =>
        TraversalCompleteness.Evaluate(run, openRows, cancelled, rule, incremental);

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
        var complete = Evaluate(Record(), 0, false);
        complete.Complete.ShouldBeTrue();
        complete.Reasons.ShouldBeEmpty();
        complete.RunFailure.ShouldBeNull();
        complete.RunDegradation.ShouldBeNull();

        Evaluate(Record(), 3, false).Reasons.ShouldBe([TraversalCompleteness.ClaimsPending]);
        Evaluate(Record(paused: true), 0, false).ShouldSatisfyAllConditions(
            v => v.Complete.ShouldBeFalse(), v => v.RunDegradation.ShouldBe(TraversalCompleteness.HostPaused));
        Evaluate(Record(), 0, cancelled: true).Reasons.ShouldBe([TraversalCompleteness.Cancelled]);
    }

    [Fact]
    public void P1a_is_strict_one_failing_seed_of_200_makes_the_traversal_incomplete()
    {
        // Kept selectable (Crawl:CompletenessRule = Strict): P1a's rule, one failing seed blocks every not-seen deletion.
        var verdict = Evaluate(Record(seedsFailed: 1), 0, false, CompletenessRule.Strict);

        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldBe([TraversalCompleteness.SeedsFailed]);
        verdict.RunFailure.ShouldBeNull();
    }

    [Theory]
    [InlineData(200, 0, true)]
    [InlineData(200, 1, true)]
    [InlineData(200, 2, true)]
    [InlineData(200, 3, false)]
    [InlineData(100, 1, true)]
    [InlineData(100, 2, false)]
    [InlineData(14_100, 141, true)]
    [InlineData(14_100, 142, false)]
    public void P1b_ratio_a_traversal_is_complete_with_at_least_99_percent_of_its_seeds_resolved(int seeds, int failed, bool complete)
    {
        var verdict = Evaluate(Record(seeds: seeds, seedsFailed: failed), 0, false);

        verdict.Complete.ShouldBe(complete);
        verdict.Reasons.ShouldBe(complete ? [] : [TraversalCompleteness.SeedsFailed]);
        // Negative proof: the strict rule refuses every one of these with a failed seed.
        Evaluate(Record(seeds: seeds, seedsFailed: failed), 0, false, CompletenessRule.Strict).Complete.ShouldBe(failed == 0);
    }

    [Fact]
    public void P1b_ratio_still_needs_no_paused_host_no_open_claim_and_no_page_limit()
    {
        Evaluate(Record(seedsFailed: 1, paused: true), 0, false).Reasons.ShouldBe([TraversalCompleteness.HostPaused]);
        Evaluate(Record(seedsFailed: 1), 1, false).Reasons.ShouldBe([TraversalCompleteness.ClaimsPending]);
        Evaluate(Record(seedsFailed: 1, limitHit: true), 0, false).Reasons.ShouldBe([TraversalCompleteness.LimitPages]);
    }

    [Fact]
    public void An_incremental_run_never_marks_a_traversal_complete()
    {
        var verdict = Evaluate(Record(), 0, false, incremental: true);

        verdict.Complete.ShouldBeFalse();
        verdict.Reasons.ShouldBe([TraversalCompleteness.Incremental]);
        verdict.RunFailure.ShouldBeNull();
        verdict.RunDegradation.ShouldBeNull();
        // Negative proof: the same facts as a full traversal are complete.
        Evaluate(Record(), 0, false).Complete.ShouldBeTrue();
        // An incremental run with no changed seed admits none and does not fail; a full traversal without seeds does.
        Evaluate(Record(seeds: 0), 0, false, incremental: true).ShouldSatisfyAllConditions(
            v => v.RunFailure.ShouldBeNull(), v => v.Reasons.ShouldBe([TraversalCompleteness.Incremental]));
        Evaluate(Record(seeds: 0), 0, false).RunFailure.ShouldBe(TraversalCompleteness.SeedsUnreachable);
    }

    [Fact]
    public void One_failing_seed_of_200_completes_the_traversal_and_its_document_is_not_a_candidate_three_make_it_partial()
    {
        // Connectors design §18 "Deletion guard": 200 seed documents, 190 reached, one seed failed, nine not reached.
        var documents = Documents(200, reached: 190);
        var failedSeeds = new HashSet<Guid> { documents[190].Id };

        var one = Evaluate(Record(seeds: 200, seedsFailed: 1), 0, false);
        var candidates = DeletionCandidates.FromTraversal(Run, documents, one, new Dictionary<Guid, string>(), failedSeeds, Now);

        one.Complete.ShouldBeTrue();
        candidates.Count.ShouldBe(9);
        candidates.ShouldAllBe(c => c.Reason == DeletionReasons.NotSeen);
        candidates.ShouldNotContain(c => c.DocumentId == documents[190].Id);

        var three = Evaluate(Record(seeds: 200, seedsFailed: 3), 0, false);
        three.Complete.ShouldBeFalse();
        DeletionCandidates.FromTraversal(Run, documents, three, new Dictionary<Guid, string>(), failedSeeds, Now).ShouldBeEmpty();
    }

    [Fact]
    public void No_resolved_seed_fails_the_run_with_seeds_unreachable()
    {
        Evaluate(Record(seeds: 5, seedsFailed: 5), 0, false).RunFailure.ShouldBe(TraversalCompleteness.SeedsUnreachable);
        Evaluate(Record(seeds: 0), 0, false).RunFailure.ShouldBe(TraversalCompleteness.SeedsUnreachable);
    }

    [Fact]
    public void C7_a_page_limit_run_deletes_nothing_it_did_not_reach()
    {
        // Eneo's partial crawl: the run hit maxPages after reaching 40 of 100 documents; every seed resolved, so only the
        // limit keeps the ratio rule from calling the traversal complete.
        var documents = Documents(100, reached: 40);
        var verdict = Evaluate(Record(limitHit: true), 0, false);
        Evaluate(Record(limitHit: false), 0, false).Complete.ShouldBeTrue();

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
        var verdict = Evaluate(Record(limitHit: true), 0, false);

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

        var candidates = DeletionCandidates.FromTraversal(Run, documents, Evaluate(Record(), 0, false), new Dictionary<Guid, string>(), failedSeeds, Now);

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
        var incomplete = Evaluate(Record(seedsFailed: 1), 0, false, CompletenessRule.Strict);

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
    public void Three_incomplete_traversals_in_a_row_raise_the_alert_and_incremental_runs_do_not_count()
    {
        static TraversalHistoryEntry Full(bool complete) => new(complete, Incremental: false);
        var incremental = new TraversalHistoryEntry(false, Incremental: true);

        TraversalCompleteness.ShouldAlert(TraversalCompleteness.ConsecutiveIncomplete([Full(false), Full(false), Full(false)])).ShouldBeTrue();
        TraversalCompleteness.ShouldAlert(TraversalCompleteness.ConsecutiveIncomplete([Full(false), Full(false), Full(true), Full(false)])).ShouldBeFalse();
        TraversalCompleteness.ShouldAlert(TraversalCompleteness.ConsecutiveIncomplete([Full(false), Full(false)])).ShouldBeFalse();
        // Daily incremental runs between weekly traversals neither raise nor reset the alert.
        TraversalCompleteness.ConsecutiveIncomplete([incremental, incremental, Full(true), Full(false)]).ShouldBe(0);
        TraversalCompleteness.ConsecutiveIncomplete([incremental, Full(false), incremental, incremental, Full(false), Full(false), Full(true)]).ShouldBe(3);
        TraversalCompleteness.ShouldAlert(2, threshold: 2).ShouldBeTrue();
    }
}
