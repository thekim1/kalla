using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Retrieval;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// One question searched once: the result and the rank of its first expected document in the top k (processing design
/// §13.6; null when none is there, and always null for an unanswerable question).
/// </summary>
public sealed record QuestionOutcome(EvaluationQuestionRow Question, SearchResult Result, int? ExpectedRank)
{
    public bool Found => ExpectedRank is not null;

    public bool Low => Result.Coverage.Status == CoverageStatus.Low;

    /// <summary>
    /// The <c>EvaluationResults</c> row: <c>Hit</c> only for an answerable question, the coverage status, best distance
    /// and best keyword score for every question (weakness review §2: calibration needs the unanswerable ones too).
    /// </summary>
    public EvaluationResult ToResult(Guid runId, string classification) => new()
    {
        RunId = runId,
        QuestionId = Question.Id,
        Classification = classification,
        ExpectedRank = ExpectedRank,
        Hit = Question.Answerable ? Found : null,
        Coverage = Result.Coverage.Status switch
        {
            CoverageStatus.Ok => CoverageStates.Ok,
            CoverageStatus.Low => CoverageStates.Low,
            _ => null,
        },
        BestDistance = (float?)Result.Coverage.BestDistance,
        BestKeywordScore = (float?)Result.Coverage.BestKeywordScore,
    };
}

/// <summary>A model's prices from the mirror (<c>config."GatewayModels"</c>); either may be unknown.</summary>
public sealed record ModelPrice(decimal? InputPerMillionUsd, decimal? OutputPerMillionUsd);

/// <summary>recall@k and MRR@k over the answerable questions of a run (processing design §13.6), rounded as the columns hold them.</summary>
public sealed record EvaluationTotals(int Questions, int Found, decimal? RecallAtK, decimal? Mrr);

/// <summary>
/// The numbers of a run (processing design §13.6, §14.1, §14.8): per question the expected rank, recall@k and MRR@k
/// over answerable questions (unanswerable ones do not count, §13.4), per layer with <c>human</c> first, wins and losses
/// against a compared run, the coverage-threshold proposal from the human layer (knowledge design §7: the value that
/// misclassifies the fewest answerable and unanswerable questions), and the nightly recall alert.
/// </summary>
public static class EvaluationScoring
{
    /// <summary>The alert's drop below the 4-week median (processing design §14.8): 5 points of recall@10.</summary>
    public const decimal AlertDrop = 0.05m;

    public static readonly TimeSpan AlertWindow = TimeSpan.FromDays(28);

    private const int Decimals = 4;

    /// <summary>The rank of the first hit whose document is expected; null when none is in the hits.</summary>
    public static int? ExpectedRank(SearchResult result, IReadOnlyList<Guid> expectedDocumentIds)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expectedDocumentIds);
        return result.Hits.FirstOrDefault(h => expectedDocumentIds.Contains(h.Candidate.DocumentId))?.Rank;
    }

    public static EvaluationTotals Totals(IEnumerable<QuestionOutcome> outcomes)
    {
        var answerable = outcomes.Where(o => o.Question.Answerable).ToList();
        var found = answerable.Count(o => o.Found);
        if (answerable.Count == 0)
        {
            return new EvaluationTotals(0, 0, null, null);
        }

        var reciprocal = answerable.Sum(o => o.ExpectedRank is { } rank ? 1m / rank : 0m);
        return new EvaluationTotals(answerable.Count, found, Math.Round((decimal)found / answerable.Count, Decimals),
            Math.Round(reciprocal / answerable.Count, Decimals));
    }

    /// <summary>The run per layer present, <c>human</c> first (reports lead with the gold set, weakness review §2).</summary>
    public static IReadOnlyList<EvaluationLayerResult> Layers(IReadOnlyList<QuestionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var layers = new List<EvaluationLayerResult>(2);
        foreach (var layer in new[] { EvaluationLayer.Human, EvaluationLayer.Generated })
        {
            var of = outcomes.Where(o => o.Question.Layer == layer).ToList();
            if (of.Count == 0)
            {
                continue;
            }

            var totals = Totals(of);
            layers.Add(new EvaluationLayerResult
            {
                Layer = layer,
                Questions = totals.Questions,
                Found = totals.Found,
                Unanswerable = of.Count(o => !o.Question.Answerable),
                RecallAtK = (double?)totals.RecallAtK,
                Mrr = (double?)totals.Mrr,
            });
        }

        return layers;
    }

    /// <summary>
    /// Questions answerable in both runs: found now and not then (wins), and the reverse (losses). Questions added or
    /// retired since the compared run do not count.
    /// </summary>
    public static (int Wins, int Losses) Compare(IReadOnlyList<QuestionOutcome> outcomes, ComparedRun compared)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(compared);
        var wins = 0;
        var losses = 0;
        foreach (var outcome in outcomes.Where(o => o.Question.Answerable))
        {
            if (!compared.Ranks.TryGetValue(outcome.Question.Id, out var before))
            {
                continue;
            }

            wins += outcome.Found && before is null ? 1 : 0;
            losses += !outcome.Found && before is not null ? 1 : 0;
        }

        return (wins, losses);
    }

    /// <summary>
    /// The proposal over the run's <c>human</c> questions (weakness review §2); null without any. The distance side needs
    /// answerable and unanswerable questions; the keyword side also needs keyword scores, so a <c>vector</c> run has none.
    /// </summary>
    public static CoverageProposal? Propose(IReadOnlyList<QuestionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var human = outcomes.Where(o => o.Question.Layer == EvaluationLayer.Human).ToList();
        if (human.Count == 0)
        {
            return null;
        }

        var answerable = human.Where(o => o.Question.Answerable).ToList();
        var unanswerable = human.Where(o => !o.Question.Answerable).ToList();
        return new CoverageProposal
        {
            Answerable = answerable.Count,
            Unanswerable = unanswerable.Count,
            FalseNotFound = answerable.Count(o => o.Low),
            UnanswerableNotFound = unanswerable.Count(o => o.Low),
            CoverageMaxDistance = Separate(
                [.. answerable.Select(o => o.Result.Coverage.BestDistance)], [.. unanswerable.Select(o => o.Result.Coverage.BestDistance)], higherCovers: false),
            CoverageMinKeywordScore = Separate(
                [.. answerable.Select(o => o.Result.Coverage.BestKeywordScore)], [.. unanswerable.Select(o => o.Result.Coverage.BestKeywordScore)], higherCovers: true),
        };
    }

    /// <summary>
    /// The threshold that misclassifies the fewest questions: answerable ones should be covered, unanswerable ones not.
    /// Covered means at most the threshold (<paramref name="higherCovers"/> false: a distance) or at least it (true: a
    /// keyword score); a missing value is never covered. Each observed value is tried as the cut; ties go to the widest
    /// gap to the next value on the uncovered side, then to the strictest cut. The proposal is the middle of that gap,
    /// rounded to four decimals, with the questions it misclassifies. Null when either group is empty or no value exists.
    /// </summary>
    public static ThresholdProposal? Separate(IReadOnlyList<double?> answerable, IReadOnlyList<double?> unanswerable, bool higherCovers)
    {
        ArgumentNullException.ThrowIfNull(answerable);
        ArgumentNullException.ThrowIfNull(unanswerable);
        if (answerable.Count == 0 || unanswerable.Count == 0)
        {
            return null;
        }

        // In "x ≤ cut covers" form: a keyword score is negated.
        double? Oriented(double? value) => value is { } v ? (higherCovers ? -v : v) : null;
        var cuts = answerable.Concat(unanswerable).Select(Oriented).OfType<double>().Distinct().Order().ToList();
        if (cuts.Count == 0)
        {
            return null;
        }

        (int Errors, double Gap, double Value)? best = null;
        for (var i = 0; i < cuts.Count; i++)
        {
            var errors = Misclassified(answerable, unanswerable, Oriented, cuts[i]);
            var gap = i + 1 < cuts.Count ? cuts[i + 1] - cuts[i] : 0;
            var value = i + 1 < cuts.Count ? (cuts[i] + cuts[i + 1]) / 2 : cuts[i];
            if (best is null || errors < best.Value.Errors || (errors == best.Value.Errors && gap > best.Value.Gap))
            {
                best = (errors, gap, value);
            }
        }

        var proposed = Math.Round(higherCovers ? -best!.Value.Value : best!.Value.Value, Decimals, MidpointRounding.AwayFromZero);
        return new ThresholdProposal
        {
            Value = proposed,
            Misclassified = Misclassified(answerable, unanswerable, Oriented, higherCovers ? -proposed : proposed),
        };
    }

    /// <summary>
    /// The rerank model a run's stats name: the first that answered, else the platform's <c>rerankModel</c> (which then
    /// never answered); null for a run without rerank.
    /// </summary>
    public static string? RerankModel(IReadOnlyList<QuestionOutcome> outcomes, bool reranked, string? platformModel)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        return reranked ? outcomes.Select(o => o.Result.RerankUsage?.Model).FirstOrDefault(m => m is not null) ?? platformModel : null;
    }

    /// <summary>
    /// How the run's searches went (#399): total and rerank latency (median and 95th percentile, nearest rank, over every
    /// question), the <c>rerank_unavailable</c> fallbacks, and with <paramref name="reranked"/> the rerank tokens and their cost
    /// per question at <paramref name="price"/> (absent unless both prices are known). Degraded questions count in the latency
    /// and in the per-question cost's denominator: they are what a user would get.
    /// </summary>
    public static EvaluationSearchStats Search(IReadOnlyList<QuestionOutcome> outcomes, bool reranked, string? rerankModel, ModelPrice? price)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var timings = outcomes.Select(o => o.Result.Timings).ToList();
        var usages = outcomes.Select(o => o.Result.RerankUsage).OfType<RerankUsage>().ToList();
        var prompt = usages.Sum(u => (long)u.PromptTokens);
        var completion = usages.Sum(u => (long)u.CompletionTokens);
        double? cost = reranked && outcomes.Count > 0 && price is { InputPerMillionUsd: { } input, OutputPerMillionUsd: { } output }
            ? (double)Math.Round((prompt * input + completion * output) / 1_000_000m / outcomes.Count, 6)
            : null;
        return new EvaluationSearchStats
        {
            Searches = outcomes.Count,
            TotalP50Ms = Percentile(timings.Select(t => t.TotalMs), 0.50),
            TotalP95Ms = Percentile(timings.Select(t => t.TotalMs), 0.95),
            RerankP50Ms = reranked ? Percentile(timings.Select(t => t.RerankMs), 0.50) : null,
            RerankP95Ms = reranked ? Percentile(timings.Select(t => t.RerankMs), 0.95) : null,
            RerankUnavailable = outcomes.Count(o => o.Result.Degraded == RetrievalDegradation.RerankUnavailable),
            RerankModel = reranked ? rerankModel : null,
            RerankPromptTokens = (int)Math.Min(prompt, int.MaxValue),
            RerankCompletionTokens = (int)Math.Min(completion, int.MaxValue),
            RerankCostPerQueryUsd = cost,
        };
    }

    /// <summary>The nearest-rank percentile in milliseconds, rounded to 0.1 ms; 0 without values.</summary>
    public static double Percentile(IEnumerable<double> values, double fraction)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1);
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1);
        return Math.Round(sorted[index], 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>recall@k fell <see cref="AlertDrop"/> or more below the median of the earlier nightly runs (processing design §14.8).</summary>
    public static bool RecallAlert(decimal? recall, IReadOnlyList<decimal> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return recall is { } current && history.Count > 0 && Median(history) - current >= AlertDrop;
    }

    public static decimal Median(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfZero(values.Count, nameof(values));
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static int Misclassified(IReadOnlyList<double?> answerable, IReadOnlyList<double?> unanswerable, Func<double?, double?> oriented, double cut) =>
        answerable.Count(v => oriented(v) is not { } x || x > cut) + unanswerable.Count(v => oriented(v) is { } x && x <= cut);
}
