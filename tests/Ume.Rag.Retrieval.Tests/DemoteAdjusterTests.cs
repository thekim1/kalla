using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Retrieval;
using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>Source <c>demote</c> after fusion (knowledge design §6.5, §16.1; weakness review §3; P1b item 3 part b).</summary>
public sealed class DemoteAdjusterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Source = Id(3000); // Make.Chunk's source

    private static SearchScope WithDemote(SearchScope scope, string json) =>
        scope with { Demote = new Dictionary<Guid, IReadOnlyList<DemotePattern>> { [Source] = DemoteAdjuster.Compile(json) } };

    [Fact]
    public void Rules_compile_from_the_settings_list_and_bad_entries_are_skipped()
    {
        var rules = DemoteAdjuster.Compile("""
            [{"pattern": "www.umea.se/arkiv/**", "factor": 0.5}, {"pattern": "re:^www\\.umea\\.se/gammalt/", "factor": 0.3},
             {"pattern": "re:(", "factor": 0.5}, {"pattern": "x/**", "factor": 1.5}, {"pattern": "y/**"}]
            """);
        rules.Select(r => (r.Pattern.Text, r.Factor)).ShouldBe([("www.umea.se/arkiv/**", 0.5), ("re:^www\\.umea\\.se/gammalt/", 0.3), ("y/**", 0.5)]);
        DemoteAdjuster.Compile(null).ShouldBeEmpty();
        DemoteAdjuster.Compile("""{"pattern": "a"}""").ShouldBeEmpty();
        DemoteAdjuster.Compile("not json").ShouldBeEmpty();
    }

    [Fact]
    public void The_lowest_matching_factor_applies_and_uploads_are_never_demoted()
    {
        var scope = WithDemote(OneCollection(), """[{"pattern": "WWW.umea.se/arkiv/**", "factor": 0.5}, {"pattern": "www.umea.se/arkiv/2010/**", "factor": 0.2}]""");
        var adjuster = DemoteAdjuster.For(scope).ShouldNotBeNull();

        adjuster.Factor(Chunk(1) with { Url = "https://www.umea.se/arkiv/nyhet" }).ShouldBe(0.5); // host case-insensitive
        adjuster.Factor(Chunk(2) with { Url = "https://www.umea.se/arkiv/2010/beslut" }).ShouldBe(0.2); // the lowest of two
        adjuster.Factor(Chunk(3) with { Url = "https://www.umea.se/bygglov" }).ShouldBe(1.0);
        adjuster.Factor(Chunk(4) with { Url = null }).ShouldBe(1.0); // an upload has no URL
        adjuster.Factor(Chunk(5) with { Url = "https://www.umea.se/arkiv/x", SourceId = Guid.NewGuid() }).ShouldBe(1.0); // another source
        DemoteAdjuster.For(OneCollection()).ShouldBeNull();
    }

    [Fact]
    public void Factors_are_worked_out_once_per_source_and_url_in_a_search()
    {
        // #333: the memo is keyed by (source, URL), so a document's chunks share a factor and nothing leaks across.
        var scope = WithDemote(OneCollection(), """[{"pattern": "www.umea.se/arkiv/**", "factor": 0.5}]""");
        var adjuster = DemoteAdjuster.For(scope).ShouldNotBeNull();
        var other = Guid.NewGuid();

        adjuster.Factor(Chunk(1) with { Url = "https://www.umea.se/arkiv/a" }).ShouldBe(0.5);
        adjuster.Factor(Chunk(2) with { Url = "https://www.umea.se/arkiv/a" }).ShouldBe(0.5); // the same document's next chunk
        adjuster.Factor(Chunk(3) with { Url = "https://www.umea.se/bygglov" }).ShouldBe(1.0); // same source, other URL
        adjuster.Factor(Chunk(4) with { Url = "https://www.umea.se/arkiv/a", SourceId = other }).ShouldBe(1.0); // same URL, other source
        adjuster.Factor(Chunk(5) with { Url = "https://www.umea.se/arkiv/b" }).ShouldBe(0.5);
    }

    [Fact]
    public async Task The_engine_multiplies_demote_with_decay_and_leaves_coverage_alone()
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var index = new FakeIndex
        {
            Keyword =
            [
                Chunk(1, keyword: 0.9) with { Url = "https://www.umea.se/arkiv/taxa" },
                Chunk(2, keyword: 0.8) with { Url = "https://www.umea.se/taxa" },
                Chunk(3, keyword: 0.7) with { Url = null }, // an upload
            ],
        };
        using var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        using var engine = new RetrievalEngine(index, sizes, texts: new FakeTexts(), time: new FakeTimeProvider(now));
        var scope = WithDemote(OneCollection(), """[{"pattern": "www.umea.se/arkiv/**", "factor": 0.5}]""");

        var demoted = await engine.SearchAsync(new SearchRequest("taxa", scope, Mode: RetrievalMode.Keyword), Ct);
        var plain = await engine.SearchAsync(new SearchRequest("taxa", OneCollection(), Mode: RetrievalMode.Keyword), Ct);

        plain.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(1), Id(2), Id(3)]);
        demoted.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(2), Id(3), Id(1)]); // the archive page halves to last
        demoted.Coverage.ShouldBe(plain.Coverage);
    }
}
