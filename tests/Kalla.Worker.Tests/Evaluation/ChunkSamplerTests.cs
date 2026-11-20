using System.Security.Cryptography;
using Kalla.Worker.Evaluation.Generation;

namespace Kalla.Worker.Tests.Evaluation;

/// <summary>
/// The sample of a generation round (processing design §13.1): the seed is <c>SHA-256(setId ‖ round)</c>, the same seed
/// and inputs give the same chunks in the same order, and the order keeps the per-document cap and the type minimum.
/// </summary>
public sealed class ChunkSamplerTests
{
    private static List<SampleChunk> Chunks(int documents, int perDocument, Func<int, string>? type = null) =>
        [.. Enumerable.Range(0, documents).SelectMany(d =>
        {
            var document = Guid.CreateVersion7();
            return Enumerable.Range(0, perDocument).Select(i => new SampleChunk(Guid.CreateVersion7(), document, type?.Invoke(d) ?? "page", i));
        })];

    [Fact]
    public void The_seed_is_sha256_of_the_set_id_and_the_round()
    {
        var setId = Guid.Parse("01929b6e-0000-7000-8000-000000000001");
        byte[] input = [.. setId.ToByteArray(bigEndian: true), 0, 0, 0, 2];

        ChunkSampler.Seed(setId, 2).ShouldBe(SHA256.HashData(input));
        ChunkSampler.Seed(setId, 3).ShouldNotBe(ChunkSampler.Seed(setId, 2));
    }

    [Fact]
    public void The_same_seed_and_inputs_give_the_same_sample_and_another_round_another()
    {
        var chunks = Chunks(20, 8);
        var setId = Guid.CreateVersion7();
        var shuffled = chunks.OrderBy(c => c.ChunkId.ToString("N")[^4..], StringComparer.Ordinal).ToList();

        var first = ChunkSampler.Rank(chunks, ChunkSampler.Seed(setId, 1), 60, 20, 96);
        var again = ChunkSampler.Rank(shuffled, ChunkSampler.Seed(setId, 1), 60, 20, 96);
        var next = ChunkSampler.Rank(chunks, ChunkSampler.Seed(setId, 2), 60, 20, 96);

        first.Select(c => c.ChunkId).ShouldBe(again.Select(c => c.ChunkId), "the input order must not matter");
        next.Take(96).Select(c => c.ChunkId).ShouldNotBe(first.Take(96).Select(c => c.ChunkId));
    }

    [Fact]
    public void The_order_is_round_robin_over_documents_within_the_per_document_cap()
    {
        var chunks = Chunks(4, 10);
        var target = ChunkSampler.Target(100, 4, chunks.Count);
        target.ShouldBe(12);
        ChunkSampler.PerDocumentCap(target, 4).ShouldBe(4);

        var order = ChunkSampler.Rank(chunks, ChunkSampler.Seed(Guid.CreateVersion7(), 1), target, 4, ChunkSampler.Candidates(target));

        order.Count.ShouldBe(16);
        order.GroupBy(c => c.DocumentId).ShouldAllBe(g => g.Count() == 4);
        order.Take(4).Select(c => c.DocumentId).Distinct().Count().ShouldBe(4, "the first pass takes one chunk of each document");
        order.Select(c => c.ChunkId).Distinct().Count().ShouldBe(order.Count);
    }

    [Fact]
    public void A_document_type_with_five_percent_of_the_chunks_gets_at_least_two()
    {
        // 95 pages and 5 PDFs over 100 documents: proportional would give the PDFs 0.5 of 10 candidates.
        var chunks = Chunks(100, 1, d => d < 5 ? "pdf" : "page");

        var quotas = ChunkSampler.Quotas(chunks, 10);
        var order = ChunkSampler.Rank(chunks, ChunkSampler.Seed(Guid.CreateVersion7(), 1), 10, 100, 10);

        quotas["pdf"].ShouldBe(2);
        quotas["page"].ShouldBe(8);
        order.Take(10).Count(c => c.DocumentType == "pdf").ShouldBe(2);
    }

    [Fact]
    public void The_target_is_min_of_the_maximum_three_per_document_and_the_eligible_chunks()
    {
        ChunkSampler.Target(100, 4, 50).ShouldBe(12);
        ChunkSampler.Target(100, 400, 1_000).ShouldBe(100);
        ChunkSampler.Target(100, 40, 7).ShouldBe(7);
        ChunkSampler.Candidates(10).ShouldBe(16);
    }
}
