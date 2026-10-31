using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>Knowledge design §16.1 <c>ResultDiversifier</c>: text-hash dedup, identity-group collapse, cap 1/3/10, stable order.</summary>
public sealed class ResultDiversifierTests
{
    private static IReadOnlyList<FusedCandidate> Fused(params Candidate[] candidates) =>
        RrfFusion.Fuse([new CandidateList(CandidateListKind.Vector, 1, [.. candidates.Select((c, i) => c with { Distance = 0.1 + (i * 0.01) })])]);

    [Fact]
    public void Keeps_the_first_chunk_of_each_document_and_text_hash()
    {
        // 1 and 2: same document, same text (old and new set of one document in one list); 3: same text, other document.
        var kept = ResultDiversifier.Dedup(Fused(
            Chunk(1, document: 7, text: "Bygglov krävs"),
            Chunk(2, document: 7, text: "Bygglov krävs"),
            Chunk(3, document: 8, text: "Bygglov krävs"),
            Chunk(4, document: 7, text: "Annan text")));

        kept.Select(f => f.Candidate.ChunkId).ShouldBe([Id(1), Id(3), Id(4)]);
    }

    [Fact]
    public void One_url_in_two_sources_collapses_to_the_document_of_its_first_chunk()
    {
        // Documents 7 and 8 are the same URL crawled for two collections (one identity group).
        var kept = ResultDiversifier.Dedup(Fused(
            Chunk(1, document: 8, identity: "https://www.umea.se/bygglov"),
            Chunk(2, document: 7, identity: "https://www.umea.se/bygglov"),
            Chunk(3, document: 8, identity: "https://www.umea.se/bygglov"),
            Chunk(4, document: 9)));

        kept.Select(f => f.Candidate.ChunkId).ShouldBe([Id(1), Id(3), Id(4)]);
    }

    [Theory]
    [InlineData(1, new[] { 1, 4, 6 })]
    [InlineData(3, new[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(10, new[] { 1, 2, 3, 4, 5, 6, 7 })]
    public void Caps_chunks_per_document_and_keeps_fused_order(int cap, int[] expected)
    {
        var ordered = Fused(
            Chunk(1, document: 7), Chunk(2, document: 7), Chunk(3, document: 7),
            Chunk(4, document: 8), Chunk(5, document: 8),
            Chunk(6, document: 9),
            Chunk(7, document: 7));

        ResultDiversifier.Cap(ordered, cap, topK: 10).Select(f => f.Candidate.ChunkId).ShouldBe([.. expected.Select(Id)]);
    }

    [Fact]
    public void Three_chunks_per_document_leave_room_for_four_documents_in_ten()
    {
        var ordered = Fused([.. Enumerable.Range(1, 20).Select(i => Chunk(i, document: 100 + ((i - 1) / 5)))]);

        var top = ResultDiversifier.Cap(ordered, 3, topK: 10);

        top.Count.ShouldBe(10);
        top.Select(f => f.Candidate.DocumentId).Distinct().Count().ShouldBe(4);
    }
}
