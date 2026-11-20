using Microsoft.Extensions.Time.Testing;
using Kalla.Domain.Retrieval;
using static Kalla.Retrieval.Tests.Make;

namespace Kalla.Retrieval.Tests;

/// <summary>Age decay after fusion (knowledge design §6.5, §16.1; weakness review §3; P1b item 3).</summary>
public sealed class RecencyAdjusterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly RecencySettings Website = new(730, 0.15, ["pdf"]);

    private static Candidate Aged(int id, string type, double? ageDays, Guid? collection = null, double? distance = null, double? keyword = null) =>
        Chunk(id, distance, keyword, collection: collection) with { DocumentType = type, SourceModifiedAt = ageDays is { } d ? Now.AddDays(-d) : null };

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(730, 0.925)]      // one half-life: half of the weight
    [InlineData(1460, 0.8875)]    // two half-lives: three quarters of the weight
    [InlineData(100_000, 0.85)]   // never below 1 − weight
    public void The_decay_follows_the_half_life_and_never_falls_below_one_minus_weight(double ageDays, double factor) =>
        RecencyAdjuster.Decay(ageDays, Website).ShouldBe(factor, 1e-9);

    [Fact]
    public void Only_listed_types_with_a_modified_date_in_a_recency_collection_decay()
    {
        var adjuster = RecencyAdjuster.For(Scope((CollectionA, RetrievalSettings.Default with { Recency = Website }), (CollectionB, RetrievalSettings.Default)), Now)
            .ShouldNotBeNull();

        adjuster.Factor(Aged(1, "pdf", 730)).ShouldBe(0.925, 1e-9);
        adjuster.Factor(Aged(2, "PDF", 730)).ShouldBe(0.925, 1e-9); // types match without case
        adjuster.Factor(Aged(3, "html", 730)).ShouldBe(1.0);         // not in kinds
        adjuster.Factor(Aged(4, "pdf", null)).ShouldBe(1.0);         // no SourceModifiedAt: no decay
        adjuster.Factor(Aged(5, "pdf", 730, CollectionB)).ShouldBe(1.0); // its own collection has recency off
        adjuster.Factor(Aged(6, "pdf", -10)).ShouldBe(1.0);          // a date in the future counts as new
        RecencyAdjuster.For(OneCollection(), Now).ShouldBeNull();    // off by default
    }

    [Fact]
    public async Task The_engine_ranks_a_new_pdf_above_an_old_one_that_fused_slightly_higher()
    {
        var index = new FakeIndex
        {
            Keyword = [Aged(1, "pdf", 3650, keyword: 0.9), Aged(2, "pdf", 10, keyword: 0.8), Aged(3, "html", 3650, keyword: 0.7)],
        };
        using var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        using var engine = new RetrievalEngine(index, sizes, texts: new FakeTexts(), time: new FakeTimeProvider(Now));
        var scope = OneCollection(RetrievalSettings.Default with { Recency = Website });

        var decayed = await engine.SearchAsync(new SearchRequest("taxa", scope, Mode: RetrievalMode.Keyword), Ct);
        var plain = await engine.SearchAsync(new SearchRequest("taxa", OneCollection(), Mode: RetrievalMode.Keyword), Ct);

        plain.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(1), Id(2), Id(3)]);
        // 1/61 · 0.8504 < 1/62 · 0.9985: the ten-year-old PDF drops below the new one; the old HTML page keeps its RRF score.
        decayed.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(2), Id(3), Id(1)]);
        decayed.Coverage.ShouldBe(plain.Coverage); // the multipliers never change coverage
    }
}
