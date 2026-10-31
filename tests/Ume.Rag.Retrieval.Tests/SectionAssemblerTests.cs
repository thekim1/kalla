using Ume.Rag.Retrieval.Postgres;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>
/// Knowledge design §16.1 section assembly (§9.2, §9.3): the run around a hit, alternating growth within
/// <c>expandMaxTokens</c>, overlap removal, subsection headings, merging, the response budget and <c>truncated</c>.
/// </summary>
public sealed class SectionAssemblerTests
{
    private static readonly Guid Set = Guid.Parse("00000000-0000-0000-0000-00000000aaaa");
    private static readonly Guid Doc = Guid.Parse("00000000-0000-0000-0000-00000000dddd");
    private const string Root = "Bygglov › Altan";

    private static Guid Id(int ordinal) => new($"00000000-0000-0000-0000-{ordinal:D12}");

    /// <summary>The neighbours H returns for <paramref name="hit"/>: one row per ordinal in <paramref name="ordinals"/>.</summary>
    private static IEnumerable<HydratedChunk> Window(int hit, IEnumerable<int> ordinals, int tokens = 100, Func<int, string>? path = null,
        Guid? set = null, short overlap = 0) =>
        ordinals.Select(o => new HydratedChunk(Id(hit), Id(o), set ?? Set, o, path?.Invoke(o) ?? Root, $"t{o}", overlap, tokens, null, null, null, null));

    private static SectionHit Hit(int ordinal, int max = 1500) => new(Id(ordinal), Doc, max);

    [Fact]
    public void A_hit_expands_to_its_whole_run_when_it_fits()
    {
        var assembly = SectionAssembler.Assemble([Hit(10)], [.. Window(10, Enumerable.Range(8, 5))], 8, 8000);

        var section = assembly.Sections.ShouldHaveSingleItem();
        section.Id.ShouldBe(0);
        section.ChunkIds.ShouldBe([Id(8), Id(9), Id(10), Id(11), Id(12)]);
        section.Text.ShouldBe($"{Root}\nt8\nt9\nt10\nt11\nt12");
        section.TokenEstimate.ShouldBe(500);
        section.Truncated.ShouldBeFalse();
        assembly.SectionOf[Id(10)].ShouldBe(0);
    }

    [Fact]
    public void Growth_alternates_after_then_before_and_stops_at_the_bound()
    {
        // 100 tokens each, bound 350: hit 10, then 11, then 9 (300); 12 would make 400.
        var assembly = SectionAssembler.Assemble([Hit(10, max: 350)], [.. Window(10, Enumerable.Range(6, 9))], 8, 8000);

        var section = assembly.Sections.ShouldHaveSingleItem();
        section.ChunkIds.ShouldBe([Id(9), Id(10), Id(11)]);
        section.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void A_hit_larger_than_the_bound_is_kept_alone()
    {
        var assembly = SectionAssembler.Assemble([Hit(10, max: 50)], [.. Window(10, [9, 10, 11])], 8, 8000);

        assembly.Sections.ShouldHaveSingleItem().ChunkIds.ShouldBe([Id(10)]);
    }

    [Fact]
    public void A_gap_in_the_ordinals_ends_the_run()
    {
        // 12 is missing (another heading path, filtered by H): 13 and 14 are not part of the section.
        var assembly = SectionAssembler.Assemble([Hit(10)], [.. Window(10, [9, 10, 11, 13, 14])], 8, 8000);

        assembly.Sections.ShouldHaveSingleItem().ChunkIds.ShouldBe([Id(9), Id(10), Id(11)]);
    }

    [Fact]
    public void A_run_reaching_the_window_edge_is_truncated()
    {
        var assembly = SectionAssembler.Assemble([Hit(10)], [.. Window(10, Enumerable.Range(8, 5))], 2, 8000);

        assembly.Sections.ShouldHaveSingleItem().Truncated.ShouldBeTrue();
    }

    [Fact]
    public void The_overlap_is_cut_from_every_chunk_after_the_first()
    {
        var chunks = new[]
        {
            new HydratedChunk(Id(10), Id(10), Set, 10, Root, "Första meningen. Andra meningen.", 0, 10, 2, null, null, null),
            new HydratedChunk(Id(10), Id(11), Set, 11, Root, "Andra meningen. Tredje meningen.", 16, 10, 3, 4, null, null),
        };

        var section = SectionAssembler.Assemble([Hit(10)], chunks, 8, 8000).Sections.ShouldHaveSingleItem();

        section.Text.ShouldBe($"{Root}\nFörsta meningen. Andra meningen.\nTredje meningen.");
        section.PageStart.ShouldBe(2);
        section.PageEnd.ShouldBe(4);
    }

    [Fact]
    public void A_subsection_inside_the_run_gets_its_heading_line()
    {
        var chunks = Window(10, [10, 11, 12], path: o => o == 11 ? Root + " › Mått" : Root);

        var section = SectionAssembler.Assemble([Hit(10)], [.. chunks], 8, 8000).Sections.ShouldHaveSingleItem();

        section.Text.ShouldBe($"{Root}\nt10\n### Mått\nt11\nt12");
        section.HeadingPath.ShouldBe(Root);
    }

    [Fact]
    public void Hits_in_one_run_share_one_section()
    {
        var chunks = Window(10, Enumerable.Range(8, 6)).Concat(Window(12, Enumerable.Range(8, 6)));

        var assembly = SectionAssembler.Assemble([Hit(10), Hit(12)], [.. chunks], 8, 8000);

        assembly.Sections.ShouldHaveSingleItem();
        assembly.SectionOf[Id(10)].ShouldBe(0);
        assembly.SectionOf[Id(12)].ShouldBe(0);
    }

    [Fact]
    public void Overlapping_windows_of_far_hits_merge_into_one_section()
    {
        // Bound 300: hit 10 takes 9–11, hit 13 takes 12–14 … they do not overlap; hit 12 takes 11–13 and overlaps both.
        var all = Enumerable.Range(4, 16).ToList();
        var chunks = Window(10, all).Concat(Window(14, all));

        var assembly = SectionAssembler.Assemble([Hit(10, max: 300), Hit(14, max: 300)], [.. chunks], 8, 8000);
        assembly.Sections.Count.ShouldBe(2);

        var merged = SectionAssembler.Assemble([Hit(10, max: 300), Hit(12, max: 300)], [.. Window(10, all).Concat(Window(12, all))], 8, 8000);
        var section = merged.Sections.ShouldHaveSingleItem();
        section.ChunkIds.ShouldBe([Id(9), Id(10), Id(11), Id(12), Id(13)]);
        merged.SectionOf[Id(12)].ShouldBe(0);
    }

    [Fact]
    public void Other_chunk_sets_are_never_merged()
    {
        var other = Guid.NewGuid();
        var chunks = Window(10, [9, 10, 11]).Concat(Window(20, [9, 10, 11], set: other).Select(c => c with { HitId = Id(20), ChunkId = Guid.NewGuid() }));
        var hits = new[] { Hit(10), new SectionHit(Id(20), Guid.NewGuid(), 1500) };

        // Hit 20's own row: ordinal 10 in the other set.
        var withHit = chunks.Select(c => c.HitId == Id(20) && c.Ordinal == 10 ? c with { ChunkId = Id(20) } : c).ToList();

        SectionAssembler.Assemble(hits, withHit, 8, 8000).Sections.Count.ShouldBe(2);
    }

    [Fact]
    public void Once_the_response_budget_is_spent_lower_ranks_get_no_section()
    {
        var other = Guid.NewGuid();
        var second = Window(30, [29, 30, 31], set: other).Select(c => c with { ChunkId = c.Ordinal == 30 ? Id(30) : Guid.NewGuid() });
        var third = Window(40, [40], set: Guid.NewGuid());
        var chunks = Window(10, [9, 10, 11]).Concat(second).Concat(third).ToList();

        var assembly = SectionAssembler.Assemble([Hit(10), Hit(30), Hit(40)], chunks, 8, 400);

        assembly.Sections.ShouldHaveSingleItem();
        assembly.SectionOf.Keys.ShouldBe([Id(10)]);
    }

    [Fact]
    public void A_stale_hit_without_rows_is_skipped()
    {
        var assembly = SectionAssembler.Assemble([Hit(99), Hit(10)], [.. Window(10, [10])], 8, 8000);

        assembly.Sections.ShouldHaveSingleItem().Id.ShouldBe(0);
        assembly.SectionOf.ContainsKey(Id(99)).ShouldBeFalse();
    }
}
