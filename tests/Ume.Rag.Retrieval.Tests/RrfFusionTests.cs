using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>Knowledge design §16.1 <c>RrfFusion</c>: hand-computed values, weights, missing ranks, tie-breaks, modes, profiles.</summary>
public sealed class RrfFusionTests
{
    private static CandidateList Vector(params Candidate[] c) => new(CandidateListKind.Vector, 1.0, c);

    private static CandidateList Keyword(params Candidate[] c) => new(CandidateListKind.Keyword, 1.0, c);

    [Fact]
    public void Fuses_by_hand_computed_reciprocal_ranks_and_a_missing_rank_adds_nothing()
    {
        // Vector: 1 (0.1), 2 (0.2), 3 (0.3). Keyword: 2 (0.9), 4 (0.5).
        var fused = RrfFusion.Fuse([
            Vector(Chunk(1, distance: 0.1), Chunk(2, distance: 0.2), Chunk(3, distance: 0.3)),
            Keyword(Chunk(2, keyword: 0.9), Chunk(4, keyword: 0.5)),
        ]);

        fused.Select(f => f.Candidate.ChunkId).ShouldBe([Id(2), Id(1), Id(4), Id(3)]);
        fused[0].Fused.ShouldBe(1.0 / 62 + 1.0 / 61, 1e-12);   // rank 2 in vector, rank 1 in keyword
        fused[1].Fused.ShouldBe(1.0 / 61, 1e-12);
        fused[2].Fused.ShouldBe(1.0 / 62, 1e-12);              // keyword rank 2 only
        fused[3].Fused.ShouldBe(1.0 / 63, 1e-12);
        fused[0].VectorRank.ShouldBe(2);
        fused[0].KeywordRank.ShouldBe(1);
        fused[0].Distance.ShouldBe(0.2);
        fused[0].KeywordScore.ShouldBe(0.9);
        fused[1].KeywordRank.ShouldBeNull();
        fused[2].VectorRank.ShouldBeNull();
        fused[2].Distance.ShouldBeNull();
    }

    [Fact]
    public void Weights_scale_each_list()
    {
        var fused = RrfFusion.Fuse([
            new CandidateList(CandidateListKind.Vector, 0.5, [Chunk(1, distance: 0.1)]),
            new CandidateList(CandidateListKind.Keyword, 2.0, [Chunk(2, keyword: 0.1)]),
        ]);

        fused.Select(f => f.Candidate.ChunkId).ShouldBe([Id(2), Id(1)]);
        fused[0].Fused.ShouldBe(2.0 / 61, 1e-12);
        fused[1].Fused.ShouldBe(0.5 / 61, 1e-12);
    }

    [Fact]
    public void Equal_scores_order_by_distance_with_nulls_last_then_by_chunk_id()
    {
        // 5 is vector rank 1 (distance 0.4) and 6 keyword rank 1: equal fused scores; the one with a distance first.
        var fused = RrfFusion.Fuse([Vector(Chunk(5, distance: 0.4)), Keyword(Chunk(6, keyword: 0.9))]);
        fused.Select(f => f.Candidate.ChunkId).ShouldBe([Id(5), Id(6)]);

        // Equal distances in one list rank by id; then two vector lists (profiles) give equal scores and distance ties.
        var ties = RrfFusion.Fuse([Vector(Chunk(9, distance: 0.3)), Vector(Chunk(8, distance: 0.3))]);
        ties.Select(f => f.Candidate.ChunkId).ShouldBe([Id(8), Id(9)]);
    }

    [Fact]
    public void A_single_list_keeps_its_own_order_in_vector_and_keyword_mode()
    {
        // Relaxed-order HNSW may return rows slightly out of order: the list is re-sorted by distance, then id.
        var vector = RrfFusion.Fuse([Vector(Chunk(3, distance: 0.3), Chunk(1, distance: 0.1), Chunk(4, distance: 0.1), Chunk(2, distance: 0.2))]);
        vector.Select(f => f.Candidate.ChunkId).ShouldBe([Id(1), Id(4), Id(2), Id(3)]);

        var keyword = RrfFusion.Fuse([Keyword(Chunk(3, keyword: 0.1), Chunk(1, keyword: 0.7), Chunk(2, keyword: 0.7))]);
        keyword.Select(f => f.Candidate.ChunkId).ShouldBe([Id(1), Id(2), Id(3)]);
    }

    [Fact]
    public void Several_profiles_each_add_a_vector_term_and_share_the_keyword_list()
    {
        var fused = RrfFusion.Fuse([
            Vector(Chunk(1, distance: 0.1), Chunk(2, distance: 0.2)),
            Vector(Chunk(3, distance: 0.9), Chunk(1, distance: 0.95)), // other profile: incomparable distances, ranks only
            Keyword(Chunk(3, keyword: 0.5)),
        ]);

        fused.Select(f => f.Candidate.ChunkId).ShouldBe([Id(3), Id(1), Id(2)]);
        fused[0].Fused.ShouldBe(2.0 / 61, 1e-12);
        fused[1].Fused.ShouldBe(1.0 / 61 + 1.0 / 62, 1e-12);
        fused[1].Distance.ShouldBe(0.1);
        fused[1].VectorRank.ShouldBe(1);
    }

    [Fact]
    public void The_score_adjuster_seam_multiplies_after_fusion_and_reorders()
    {
        var fused = RrfFusion.Fuse([Vector(Chunk(1, distance: 0.1), Chunk(2, distance: 0.2))], new HalveChunk(Id(1)));

        fused.Select(f => f.Candidate.ChunkId).ShouldBe([Id(2), Id(1)]);
        fused[1].Fused.ShouldBe(1.0 / 61, 1e-12);
        fused[1].Score.ShouldBe(0.5 / 61, 1e-12);
    }

    [Fact]
    public void Uuid_order_matches_postgres_byte_order()
    {
        var a = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var b = Guid.Parse("00000000-ffff-0000-0000-000000000000");
        var c = Guid.Parse("ff000000-0000-0000-0000-000000000000");
        new[] { c, a, b }.Order(GuidOrder.Instance).ShouldBe([b, a, c]);
    }

    private sealed class HalveChunk(Guid id) : IScoreAdjuster
    {
        public double Factor(Candidate candidate) => candidate.ChunkId == id ? 0.5 : 1.0;
    }
}
