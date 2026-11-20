using Kalla.Domain.Retrieval;
using Kalla.Infrastructure.Gateway;
using Kalla.Pipelines.Jobs;
using Kalla.Retrieval;
using Kalla.Worker.Evaluation;

namespace Kalla.Worker.Tests.Evaluation;

/// <summary>
/// The threshold proposal, the recall alert, the nightly occurrence and the search stats (processing design §13.6, §14.8;
/// knowledge design §7; #399).
/// </summary>
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

    [Fact]
    public void Percentiles_are_nearest_rank_over_every_question()
    {
        double[] values = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        EvaluationScoring.Percentile(values, 0.50).ShouldBe(50);
        EvaluationScoring.Percentile(values, 0.95).ShouldBe(100);
        EvaluationScoring.Percentile([7.25], 0.95).ShouldBe(7.3);
        EvaluationScoring.Percentile([], 0.5).ShouldBe(0);
    }

    [Fact]
    public void A_reranked_run_reports_rerank_latency_tokens_cost_per_question_and_fallbacks()
    {
        QuestionOutcome[] outcomes =
        [
            Outcome(total: 900, rerank: 800, new GatewayCallUsage("rerank", "fors/chat-eu", 1_000, 0, 50, null)),
            Outcome(total: 1_100, rerank: 1_000, new GatewayCallUsage("rerank", "fors/chat-eu", 3_000, 0, 150, null)),
            Outcome(total: 30_100, rerank: 30_000, null, RetrievalDegradation.RerankUnavailable),
        ];

        var model = EvaluationScoring.RerankModel(outcomes, reranked: true, platformModel: "other");
        model.ShouldBe("fors/chat-eu", "the model that answered");
        var stats = EvaluationScoring.Search(outcomes, reranked: true, model, new ModelPrice(2m, 8m));

        stats.ShouldBe(new EvaluationSearchStats
        {
            Searches = 3,
            TotalP50Ms = 1_100,
            TotalP95Ms = 30_100,
            RerankP50Ms = 1_000,
            RerankP95Ms = 30_000,
            RerankUnavailable = 1,
            RerankModel = "fors/chat-eu",
            RerankPromptTokens = 4_000,
            RerankCompletionTokens = 200,
            // (4,000 x 2 + 200 x 8) / 1,000,000 / 3 questions, the fallback included.
            RerankCostPerQueryUsd = 0.0032,
        });
    }

    [Fact]
    public void A_run_without_rerank_or_without_prices_reports_no_cost()
    {
        QuestionOutcome[] outcomes = [Outcome(total: 40, rerank: 0, null)];

        var plain = EvaluationScoring.Search(outcomes, reranked: false, EvaluationScoring.RerankModel(outcomes, reranked: false, "fors/chat-eu"), null);
        plain.ShouldBe(new EvaluationSearchStats { Searches = 1, TotalP50Ms = 40, TotalP95Ms = 40 }, "no rerank model, latency, tokens or cost");

        var unpriced = EvaluationScoring.Search(outcomes, reranked: true, "fors/chat-eu", new ModelPrice(2m, null));
        unpriced.RerankCostPerQueryUsd.ShouldBe(0, "no tokens at all: the input price suffices");
        QuestionOutcome[] completions = [Outcome(total: 40, rerank: 30, new GatewayCallUsage("rerank", "fors/chat-eu", 1_000, 0, 50, null))];
        EvaluationScoring.Search(completions, reranked: true, "fors/chat-eu", new ModelPrice(2m, null)).RerankCostPerQueryUsd
            .ShouldBeNull("completion tokens need the output price");
        QuestionOutcome[] gateway = [Outcome(total: 40, rerank: 30, new GatewayCallUsage("rerank", "bge-reranker-v2-m3", 2_000, 0, 0, null))];
        EvaluationScoring.Search(gateway, reranked: true, "bge-reranker-v2-m3", new ModelPrice(0.1m, null)).RerankCostPerQueryUsd
            .ShouldBe(0.0002, "a gateway rerank model bills input tokens only (#414)");
        EvaluationScoring.RerankModel(outcomes, reranked: true, "fors/chat-eu").ShouldBe("fors/chat-eu", "none answered: the platform's model");
    }

    private static QuestionOutcome Outcome(double total, double rerank, GatewayCallUsage? usage, string? degraded = null) => new(
        new EvaluationQuestionRow(Guid.NewGuid(), EvaluationLayer.Generated, "q", [Guid.NewGuid()]),
        new SearchResult("q", [], new Coverage(CoverageStatus.Low, null, null, null, null), degraded, null, new SearchTimings(0, 0, 0, rerank, total), usage),
        null);
}
