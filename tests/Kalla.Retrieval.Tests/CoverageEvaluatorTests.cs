using Kalla.Domain.Retrieval;
using static Kalla.Retrieval.Tests.Make;

namespace Kalla.Retrieval.Tests;

/// <summary>
/// Knowledge design §16.1 <c>CoverageEvaluator</c>: <c>low</c> only when both sides are weak, the keyword floor
/// (vector side alone while it is null), the multi-collection rule, per-collection thresholds, keyword mode and
/// degraded → <c>unknown</c>, empty scope → <c>low</c>, rerank score replacing the distance.
/// </summary>
public sealed class CoverageEvaluatorTests
{
    private static readonly RetrievalSettings Calibrated = RetrievalSettings.Default with { CoverageMinKeywordScore = 0.2 };

    [Theory]
    [InlineData(0.30, 0.50, CoverageStatus.Ok)]   // vector strong, keyword strong
    [InlineData(0.30, 0.05, CoverageStatus.Ok)]   // vector strong, keyword weak
    [InlineData(0.70, 0.50, CoverageStatus.Ok)]   // vector weak, keyword strong (a diarienummer still answers)
    [InlineData(0.70, 0.05, CoverageStatus.Low)]  // both weak
    public void Low_only_when_both_sides_are_weak(double distance, double keyword, CoverageStatus expected)
    {
        var coverage = CoverageEvaluator.Evaluate(OneCollection(Calibrated), RetrievalMode.Hybrid, degraded: false,
            [Chunk(1, distance: distance)], [Chunk(2, keyword: keyword)]);

        coverage.Status.ShouldBe(expected);
        coverage.BestDistance.ShouldBe(distance);
        coverage.Threshold.ShouldBe(0.50);
        coverage.CollectionId.ShouldBe(CollectionA);
        coverage.BestKeywordScore.ShouldBe(keyword);
    }

    [Fact]
    public void An_uncalibrated_keyword_floor_leaves_the_vector_side_alone()
    {
        var scope = OneCollection(); // CoverageMinKeywordScore null
        CoverageEvaluator.Evaluate(scope, RetrievalMode.Hybrid, false, [Chunk(1, distance: 0.7)], [Chunk(2, keyword: 5.0)])
            .Status.ShouldBe(CoverageStatus.Low);
        CoverageEvaluator.Evaluate(scope, RetrievalMode.Hybrid, false, [Chunk(1, distance: 0.5)], [])
            .Status.ShouldBe(CoverageStatus.Ok); // at the threshold counts as covered
    }

    [Fact]
    public void Any_covered_collection_counts_with_its_own_threshold()
    {
        var strict = RetrievalSettings.Default with { CoverageMaxDistance = 0.20 };
        var broad = RetrievalSettings.Default with { CoverageMaxDistance = 0.60 };
        var scope = Scope((CollectionA, strict), (CollectionB, broad));

        var coverage = CoverageEvaluator.Evaluate(scope, RetrievalMode.Vector, false,
            [Chunk(1, distance: 0.30, collection: CollectionA), Chunk(2, distance: 0.55, collection: CollectionB)], []);

        coverage.Status.ShouldBe(CoverageStatus.Ok);
        coverage.CollectionId.ShouldBe(CollectionB); // the covering collection is reported
        coverage.BestDistance.ShouldBe(0.55);
        coverage.Threshold.ShouldBe(0.60);

        var none = CoverageEvaluator.Evaluate(scope, RetrievalMode.Vector, false,
            [Chunk(1, distance: 0.30, collection: CollectionA), Chunk(2, distance: 0.65, collection: CollectionB)], []);
        none.Status.ShouldBe(CoverageStatus.Low);
        none.CollectionId.ShouldBe(CollectionA); // nothing covers: the smallest distance is reported
        none.Threshold.ShouldBe(0.20);
    }

    [Fact]
    public void A_collection_without_vector_candidates_is_uncovered()
    {
        var scope = Scope((CollectionA, RetrievalSettings.Default), (CollectionB, RetrievalSettings.Default with { CoverageMaxDistance = 0.9 }));

        var coverage = CoverageEvaluator.Evaluate(scope, RetrievalMode.Vector, false, [Chunk(1, distance: 0.8, collection: CollectionA)], []);

        coverage.Status.ShouldBe(CoverageStatus.Low);
    }

    [Fact]
    public void The_keyword_floor_is_per_collection()
    {
        var scope = Scope(
            (CollectionA, RetrievalSettings.Default with { CoverageMinKeywordScore = 0.5 }),
            (CollectionB, RetrievalSettings.Default));                                       // not calibrated

        CoverageEvaluator.Evaluate(scope, RetrievalMode.Hybrid, false, [], [Chunk(1, keyword: 0.9, collection: CollectionB)])
            .Status.ShouldBe(CoverageStatus.Low);
        CoverageEvaluator.Evaluate(scope, RetrievalMode.Hybrid, false, [], [Chunk(1, keyword: 0.5, collection: CollectionA)])
            .Status.ShouldBe(CoverageStatus.Ok);
    }

    [Fact]
    public void Keyword_mode_and_a_degraded_search_are_unknown()
    {
        CoverageEvaluator.Evaluate(OneCollection(Calibrated), RetrievalMode.Keyword, false, [], [Chunk(1, keyword: 0.01)])
            .Status.ShouldBe(CoverageStatus.Unknown);
        var degraded = CoverageEvaluator.Evaluate(OneCollection(Calibrated), RetrievalMode.Hybrid, true, [], [Chunk(1, keyword: 0.01)]);
        degraded.Status.ShouldBe(CoverageStatus.Unknown);
        degraded.BestKeywordScore.ShouldBe(0.01);
    }

    [Fact]
    public void An_empty_scope_is_low()
    {
        var coverage = CoverageEvaluator.Evaluate(Scope(), RetrievalMode.Hybrid, false, [], []);

        coverage.ShouldBe(new Coverage(CoverageStatus.Low, null, null, null, null));
    }

    [Fact]
    public void A_rerank_score_replaces_the_distance_when_reranking_is_on()
    {
        var platform = RetrievalPlatformSettings.Default with { CoverageMinRerankScore = 0.6 };
        var scope = Scope(platform, (CollectionA, RetrievalSettings.Default));

        CoverageEvaluator.Evaluate(scope, RetrievalMode.Vector, false, [Chunk(1, distance: 0.1)], [], bestRerankScore: 0.2)
            .Status.ShouldBe(CoverageStatus.Low);
        CoverageEvaluator.Evaluate(scope, RetrievalMode.Vector, false, [Chunk(1, distance: 0.9)], [], bestRerankScore: 0.7)
            .Status.ShouldBe(CoverageStatus.Ok);
        // Without a rerank floor the distance still decides.
        CoverageEvaluator.Evaluate(OneCollection(), RetrievalMode.Vector, false, [Chunk(1, distance: 0.1)], [], bestRerankScore: 0.0)
            .Status.ShouldBe(CoverageStatus.Ok);
    }
}
