using Kalla.Domain.Retrieval;

namespace Kalla.Retrieval;

/// <summary>
/// The coverage signal (knowledge design §7), from the candidate lists after filters and before dedup and diversity.
/// <list type="bullet">
/// <item>Vector side: a searched collection is covered when its best (smallest) cosine distance among the vector
/// candidates is at most its <c>coverageMaxDistance</c>; a collection without a candidate is uncovered. When the search
/// was reranked and <c>coverageMinRerankScore</c> is set, the best rerank score against that floor replaces this side
/// (§6.7, P2).</item>
/// <item>Keyword side: covered when a collection's best keyword score is at least its <c>coverageMinKeywordScore</c>;
/// while that floor is null (not calibrated) the side is uncovered, so the vector side alone decides.</item>
/// <item>Status: <c>low</c> only when both sides are uncovered (any covered collection counts); <c>low</c> with no
/// searchable collection; <c>unknown</c> in <c>keyword</c> mode and when the embedding degraded.</item>
/// </list>
/// The result reports the best distance with its threshold and collection (the covering collection with the smallest
/// distance, else the collection with the smallest distance), and the best keyword score.
/// </summary>
public static class CoverageEvaluator
{
    public static Coverage Evaluate(
        SearchScope scope,
        RetrievalMode mode,
        bool degraded,
        IReadOnlyList<Candidate> vectorCandidates,
        IReadOnlyList<Candidate> keywordCandidates,
        double? bestRerankScore = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(vectorCandidates);
        ArgumentNullException.ThrowIfNull(keywordCandidates);

        var bestKeyword = keywordCandidates.Where(c => c.KeywordScore is not null).Select(c => c.KeywordScore).Max();
        if (scope.Collections.Count == 0)
        {
            return new Coverage(CoverageStatus.Low, null, null, null, bestKeyword);
        }

        // Vector side: best distance per searched collection.
        var bestByCollection = vectorCandidates
            .Where(c => c.Distance is not null)
            .GroupBy(c => c.CollectionId)
            .ToDictionary(g => g.Key, g => g.Min(c => c.Distance!.Value));
        var sides = scope.Collections
            .Where(c => bestByCollection.ContainsKey(c.CollectionId))
            .Select(c => new VectorSide(c.CollectionId, bestByCollection[c.CollectionId], c.Settings.CoverageMaxDistance))
            .OrderBy(s => s.Best)
            .ThenBy(s => s.CollectionId, GuidOrder.Instance)
            .ToList();
        var covering = sides.FirstOrDefault(s => s.Best <= s.Threshold);
        var reported = covering ?? sides.FirstOrDefault();

        Coverage Result(CoverageStatus status) => new(
            status, reported?.Best, reported?.Threshold, reported?.CollectionId, bestKeyword);

        if (mode == RetrievalMode.Keyword || degraded)
        {
            return Result(CoverageStatus.Unknown);
        }

        var vectorCovered = bestRerankScore is { } rerank && scope.Platform.CoverageMinRerankScore is { } rerankFloor
            ? rerank >= rerankFloor
            : covering is not null;

        // Keyword side: best keyword score per collection against its floor; null floor = uncovered.
        var keywordCovered = mode == RetrievalMode.Hybrid && keywordCandidates
            .Where(c => c.KeywordScore is not null)
            .GroupBy(c => c.CollectionId)
            .Any(g => scope.Collections.FirstOrDefault(c => c.CollectionId == g.Key)?.Settings.CoverageMinKeywordScore is { } floor
                      && g.Max(c => c.KeywordScore!.Value) >= floor);

        return Result(vectorCovered || keywordCovered ? CoverageStatus.Ok : CoverageStatus.Low);
    }

    private sealed record VectorSide(Guid CollectionId, double Best, double Threshold);
}
