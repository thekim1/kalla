using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Retrieval;
using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>
/// The keyword stop list (knowledge design §6.2, §20; Decisions "Keyword stop list before the pilot crawl"): lexemes in
/// more than 30 % of the scope's chunks are dropped from statement K's query; the statistics are cached and refreshed in
/// the background; a failure never fails a search.
/// </summary>
public sealed class KeywordStopListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ReadOnlyMemory<float>> Embed(string query, CancellationToken ct) => Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0, 0 });

    [Fact]
    public async Task Lexemes_in_more_than_thirty_percent_of_the_scope_are_stopped()
    {
        var index = new FakeIndex
        {
            Counts = [new(Profile, CollectionA, 10_000)],
            Lexemes = [new(Profile, CollectionA, "umeå", 9_000), new(Profile, CollectionA, "kommun", 3_001),
                new(Profile, CollectionA, "kontakt", 3_000), new(Profile, CollectionA, "bygglov", 1_250)],
        };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());

        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["kommun", "umeå"]); // 30 % exactly stays; sorted
    }

    [Fact]
    public async Task A_scope_under_a_thousand_chunks_has_no_stop_list()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 999)], Lexemes = [new(Profile, CollectionA, "bygglov", 900)] };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());

        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBeEmpty();
        index.LexemeReads.ShouldBe(0); // nothing to read for a small scope
    }

    [Fact]
    public async Task Several_collections_add_up_their_chunks()
    {
        var index = new FakeIndex
        {
            Counts = [new(Profile, CollectionA, 3_000), new(Profile, CollectionB, 7_000)],
            // "umeå": 2,900 + 700 = 3,600 of 10,000 → stopped, although under 30 % in B; "simhall" 2,000 → kept, although
            // 67 % of A; "kommun" only counted in A (absent from B's statistics) 2,500 → kept.
            Lexemes = [new(Profile, CollectionA, "umeå", 2_900), new(Profile, CollectionB, "umeå", 700),
                new(Profile, CollectionA, "simhall", 2_000), new(Profile, CollectionA, "kommun", 2_500)],
        };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());

        (await cache.StopLexemesAsync(Scope((CollectionA, RetrievalSettings.Default), (CollectionB, RetrievalSettings.Default)), Ct))
            .ShouldBe(["umeå"]);
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["kommun", "simhall", "umeå"]); // A alone
    }

    [Fact]
    public async Task A_link_with_topics_takes_its_shares_of_the_whole_collection()
    {
        // The statistics are per collection: "umeå" in 2,000 of 10,000 chunks is 20 %, not 2,000 of the topic's 1,500.
        var index = new FakeIndex
        {
            Counts = [new(Profile, CollectionA, 1_500) { Tags = ["avfall"] }, new(Profile, CollectionA, 8_500)],
            Lexemes = [new(Profile, CollectionA, "umeå", 2_000), new(Profile, CollectionA, "kommun", 4_000)],
        };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());
        var topical = new SearchScope(Profile, 3, [new CollectionInScope(CollectionA, RetrievalSettings.Default) { Topics = ["avfall"] }], ["public"],
            RetrievalPlatformSettings.Default);

        (await cache.SizeAsync(topical, Ct)).ScopeChunks.ShouldBe(1_500);
        (await cache.StopLexemesAsync(topical, Ct)).ShouldBe(["kommun"]);
    }

    [Fact]
    public async Task Another_profile_s_statistics_do_not_count()
    {
        var other = Guid.Parse("01929b6e-0000-7000-8000-0000000000ff");
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 5_000)], Lexemes = [new(other, CollectionA, "umeå", 5_000)] };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());

        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Stale_statistics_serve_while_the_refresh_runs_in_the_background()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 10_000)], Lexemes = [new(Profile, CollectionA, "umeå", 9_000)] };
        var time = new FakeTimeProvider();
        using var cache = new ScopeSizeCache(index, time);
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]);

        index.Lexemes = [new(Profile, CollectionA, "kommun", 9_000)];
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]); // fresh: no read
        index.LexemeReads.ShouldBe(1);

        index.HoldLexemes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.Advance(TimeSpan.FromMinutes(10));
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]); // stale, served; the read waits
        index.HoldLexemes.SetResult();

        await WaitForAsync(async () => (await cache.StopLexemesAsync(OneCollection(), Ct)).SequenceEqual(["kommun"]));
        index.LexemeReads.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_read_gives_no_stop_list_or_keeps_the_previous_one()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 10_000)], LexemeFailure = new TimeoutException("ts_stat") };
        var time = new FakeTimeProvider();
        using var cache = new ScopeSizeCache(index, time);
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBeEmpty();

        index.LexemeFailure = null;
        index.Lexemes = [new(Profile, CollectionA, "umeå", 9_000)];
        // #333: none yet, but the failed read is not repeated by every search: only after FailedReadRetry.
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBeEmpty();
        index.LexemeReads.ShouldBe(1);
        time.Advance(ScopeSizeCache.FailedReadRetry);
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]);

        index.LexemeFailure = new TimeoutException("ts_stat");
        time.Advance(TimeSpan.FromMinutes(10));
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]);
        await WaitForAsync(() => Task.FromResult(index.LexemeReads == 3));
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]); // kept, and not read again until the interval
        index.LexemeReads.ShouldBe(3);
    }

    [Fact]
    public async Task Warming_reads_counts_and_statistics_so_the_first_search_does_not()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 10_000)], Lexemes = [new(Profile, CollectionA, "umeå", 9_000)] };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());

        cache.HasKeywordStatistics.ShouldBeFalse();
        (await cache.WarmAsync(Ct)).ShouldBeTrue();
        cache.HasKeywordStatistics.ShouldBeTrue();
        index.LexemeReads.ShouldBe(1);
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]);
        index.LexemeReads.ShouldBe(1);

        var failing = new FakeIndex { Counts = [new(Profile, CollectionA, 10_000)], LexemeFailure = new TimeoutException("ts_stat") };
        using var cold = new ScopeSizeCache(failing, new FakeTimeProvider());
        (await cold.WarmAsync(Ct)).ShouldBeFalse(); // reported, not thrown
        (await cold.StopLexemesAsync(OneCollection(), Ct)).ShouldBeEmpty();
        failing.LexemeReads.ShouldBe(1); // the first search does not wait for the failing read again
    }

    [Fact]
    public async Task Invalidate_drops_the_statistics()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 10_000)], Lexemes = [new(Profile, CollectionA, "umeå", 9_000)] };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBe(["umeå"]);

        index.Lexemes = [];
        cache.Invalidate();
        (await cache.StopLexemesAsync(OneCollection(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_engine_gives_K_the_scope_s_stop_list_and_V_is_unchanged()
    {
        var index = new FakeIndex
        {
            Counts = [new(Profile, CollectionA, 10_000)],
            Lexemes = [new(Profile, CollectionA, "umeå", 9_000), new(Profile, CollectionA, "kommun", 8_000)],
            Keyword = [Chunk(1, keyword: 0.5)],
            Vector = [Chunk(1, distance: 0.2)],
        };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());
        using var engine = new RetrievalEngine(index, cache);

        var result = await engine.SearchAsync(new SearchRequest("bygglov i umeå kommun", OneCollection(), Embed: Embed), Ct);

        index.KeywordStopLists.ShouldHaveSingleItem().ShouldBe(["kommun", "umeå"]);
        index.KeywordQueries.ShouldBe(["bygglov i umeå kommun"]); // the text is unchanged: Postgres removes the stemmed lexemes
        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(1)]);
    }

    [Fact]
    public async Task A_small_scope_s_K_gets_an_empty_stop_list()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 20)], Lexemes = [new(Profile, CollectionA, "umeå", 20)] };
        using var cache = new ScopeSizeCache(index, new FakeTimeProvider());
        using var engine = new RetrievalEngine(index, cache);

        await engine.SearchAsync(new SearchRequest("umeå", OneCollection(), Mode: RetrievalMode.Keyword), Ct);

        index.KeywordStopLists.ShouldHaveSingleItem().ShouldBeEmpty();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("The background refresh did not finish.");
    }
}
