using Ume.Rag.Worker.Evaluation;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>The threshold proposal, the recall alert and the nightly occurrence (processing design §13.6, §14.8; knowledge design §7).</summary>
public sealed class EvaluationScoringTests
{
    [Fact]
    public void The_distance_proposal_is_the_middle_of_the_gap_that_separates_the_groups()
    {
        var proposal = EvaluationScoring.Separate([0.20, 0.30, 0.35], [0.60, 0.70, null], higherCovers: false).ShouldNotBeNull();
        proposal.Value.ShouldBe(0.475);
        proposal.Misclassified.ShouldBe(0);
    }

    [Fact]
    public void Overlapping_groups_get_the_cut_with_the_fewest_misclassified()
    {
        // Answerable 0.2, 0.4, 0.8 and unanswerable 0.5, 0.9: the best cut misclassifies one (0.8 or 0.5).
        var proposal = EvaluationScoring.Separate([0.2, 0.4, 0.8], [0.5, 0.9], higherCovers: false).ShouldNotBeNull();
        proposal.Misclassified.ShouldBe(1);
        proposal.Value.ShouldBe(0.45, "equal errors and gaps keep the strictest cut");
    }

    [Fact]
    public void The_keyword_proposal_is_a_floor_and_a_missing_score_is_never_covered()
    {
        var proposal = EvaluationScoring.Separate([0.4, 0.5, null], [0.1, null], higherCovers: true).ShouldNotBeNull();
        proposal.Value.ShouldBe(0.25);
        proposal.Misclassified.ShouldBe(1, "the answerable question without a keyword match");
    }

    [Fact]
    public void No_proposal_without_both_groups_or_without_values()
    {
        EvaluationScoring.Separate([0.2], [], higherCovers: false).ShouldBeNull();
        EvaluationScoring.Separate([null], [null], higherCovers: true).ShouldBeNull();
    }

    [Theory]
    [InlineData(0.80, true)]
    [InlineData(0.81, false)]
    public void The_alert_fires_5_points_below_the_median(double recall, bool alert) =>
        EvaluationScoring.RecallAlert((decimal)recall, [0.70m, 0.85m, 0.90m, 0.85m]).ShouldBe(alert);

    [Fact]
    public void No_alert_without_history() => EvaluationScoring.RecallAlert(0m, []).ShouldBeFalse();

    [Fact]
    public void The_nightly_occurrence_is_the_latest_0330_in_Stockholm()
    {
        // 2026-10-31 is CET (+01:00) after the DST change of 2026-10-25.
        var now = new DateTimeOffset(2026, 10, 31, 2, 0, 0, TimeSpan.Zero);
        EvalScheduler.LatestOccurrence(now).ShouldBe(new DateTimeOffset(2026, 10, 31, 2, 30, 0, TimeSpan.Zero).AddDays(-1));
        EvalScheduler.LatestOccurrence(now.AddHours(1)).ShouldBe(new DateTimeOffset(2026, 10, 31, 2, 30, 0, TimeSpan.Zero));
    }
}
