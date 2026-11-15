using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Retrieval;
using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>
/// The engine over a fake index (knowledge design §5.5, §6, §16.1 <c>IReranker</c>): modes, degrading, the reranker
/// seam before the cap, slots, the exact/HNSW rule and request validation.
/// </summary>
public sealed class RetrievalEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeIndex _index = new()
    {
        Vector = [Chunk(1, distance: 0.10, document: 7), Chunk(2, distance: 0.20, document: 7), Chunk(3, distance: 0.30, document: 7),
            Chunk(4, distance: 0.40, document: 7), Chunk(5, distance: 0.60, document: 8)],
        Keyword = [Chunk(5, keyword: 0.9, document: 8), Chunk(6, keyword: 0.4, document: 9)],
    };

    private readonly ScopeSizeCache _sizes;

    public RetrievalEngineTests() => _sizes = new ScopeSizeCache(_index, new FakeTimeProvider());

    public void Dispose() => _sizes.Dispose();

    private static Task<ReadOnlyMemory<float>> Embed(string query, CancellationToken ct) => Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0, 0 });

    private static Task<ReadOnlyMemory<float>> Unavailable(string query, CancellationToken ct) =>
        throw new QueryEmbeddingUnavailableException(RetrievalDegradation.EmbeddingResidency, "x-ume-residency was Eu");

    private RetrievalEngine Engine(IReranker? reranker = null, RetrievalEngineOptions? options = null) =>
        new(_index, _sizes, options, reranker, new FakeTexts());

    /// <summary>One collection on a platform with a model for both rerankers.</summary>
    private static SearchScope RerankScope(string? chatModel = "ume/small-eu", string? gatewayModel = "bge-reranker-v2-m3") =>
        Scope(RetrievalPlatformSettings.Default with { RerankModel = chatModel, GatewayRerankModel = gatewayModel }, (CollectionA, RetrievalSettings.Default));

    [Fact]
    public async Task Hybrid_fuses_both_lists_dedups_and_caps_three_per_document()
    {
        using var engine = Engine();
        var result = await engine.SearchAsync(new SearchRequest("Bygglovsansökan  för altan", OneCollection(), Embed: Embed), Ct);

        result.NormalizedQuery.ShouldBe("Bygglovsansökan för altan");
        _index.KeywordQueries.ShouldBe(["Bygglovsansökan för altan\nbygglov ansökan"]);
        result.VectorSearch.ShouldBe(VectorSearch.Exact); // nothing counted: the scope is small
        // 5 is in both lists; 2 and 6 tie on RRF and 2 has a distance; document 7 is capped at three (4 dropped).
        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(5), Id(1), Id(2), Id(6), Id(3)]);
        result.Hits.Select(h => h.Rank).ShouldBe([1, 2, 3, 4, 5]);
        result.Hits[0].Scores.ShouldBe(new ChunkScores(1.0 / 65 + 1.0 / 61, 0.60, 5, 0.9, 1, null));
        result.Coverage.Status.ShouldBe(CoverageStatus.Ok);
        result.Coverage.BestDistance.ShouldBe(0.10);
        result.Degraded.ShouldBeNull();
    }

    [Fact]
    public async Task Keyword_mode_never_embeds_and_vector_mode_never_runs_K()
    {
        using var engine = Engine();
        var keyword = await engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct);
        keyword.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(5), Id(6)]);
        keyword.Coverage.Status.ShouldBe(CoverageStatus.Unknown);
        keyword.VectorSearch.ShouldBeNull();

        _index.KeywordQueries.Clear();
        var vector = await engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Vector, Embed: Embed), Ct);
        _index.KeywordQueries.ShouldBeEmpty();
        vector.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(1), Id(2), Id(3), Id(5)]);
    }

    [Fact]
    public async Task A_failed_embedding_degrades_hybrid_to_keyword_only_with_unknown_coverage()
    {
        using var engine = Engine();
        var result = await engine.SearchAsync(new SearchRequest("altan", OneCollection(), Embed: Unavailable), Ct);

        result.Degraded.ShouldBe(RetrievalDegradation.EmbeddingResidency);
        result.Coverage.Status.ShouldBe(CoverageStatus.Unknown);
        result.VectorSearch.ShouldBeNull();
        _index.VectorSearches.ShouldBeEmpty();
        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(5), Id(6)]);
    }

    [Fact]
    public async Task Vector_mode_and_the_evaluator_do_not_degrade()
    {
        using var engine = Engine();
        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() =>
            engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Vector, Embed: Unavailable), Ct));
        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() =>
            engine.SearchAsync(new SearchRequest("altan", OneCollection(), Embed: Unavailable, AllowDegraded: false), Ct));
    }

    [Fact]
    public async Task K_runs_while_the_query_is_embedded()
    {
        using var engine = Engine();
        _index.HoldKeyword = new TaskCompletionSource();
        var embedded = new TaskCompletionSource();
        var search = engine.SearchAsync(new SearchRequest("altan", OneCollection(), Embed: async (q, ct) =>
        {
            // K has started before the embedding finishes.
            _index.KeywordQueries.ShouldNotBeEmpty();
            embedded.SetResult();
            return await Embed(q, ct);
        }), Ct);
        await embedded.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _index.HoldKeyword.SetResult();
        (await search).Hits.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_reranker_reorders_before_the_cap()
    {
        // Scores favour document 7's chunk 4 (dropped by the cap without reranking) and chunk 6.
        var reranker = new FakeReranker(new Dictionary<Guid, double> { [Id(4)] = 0.99, [Id(6)] = 0.95, [Id(1)] = 0.5 });
        using var engine = Engine(reranker);
        var result = await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed, Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu)), Ct);

        reranker.Calls.ShouldBe(1);
        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(4), Id(6), Id(1), Id(5), Id(2)]);
        result.Hits[0].Scores.RerankScore.ShouldBe(0.99);
        result.Degraded.ShouldBeNull();
    }

    [Fact]
    public async Task A_failing_reranker_keeps_the_fused_order_and_reports_it()
    {
        using var engine = Engine(new FakeReranker(null, fail: true));
        var result = await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed, Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu)), Ct);

        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(5), Id(1), Id(2), Id(6), Id(3)]);
        result.Degraded.ShouldBe(RetrievalDegradation.RerankUnavailable);
        result.Coverage.Status.ShouldBe(CoverageStatus.Ok);
    }

    [Fact]
    public async Task The_noop_reranker_and_a_request_without_rerank_leave_the_fused_order()
    {
        using var engine = Engine(NoopReranker.Instance);
        var noop = await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed, Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu)), Ct);
        noop.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(5), Id(1), Id(2), Id(6), Id(3)]);
        noop.Degraded.ShouldBeNull();

        var reranker = new FakeReranker(new Dictionary<Guid, double>());
        using var off = Engine(reranker);
        await off.SearchAsync(new SearchRequest("altan", OneCollection(), Embed: Embed), Ct);
        reranker.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(RerankerKind.ChatModel)]
    [InlineData(RerankerKind.Gateway)]
    public async Task Each_reranker_is_asked_with_its_own_model_and_none_when_unset(RerankerKind kind)
    {
        // The other reranker's model is not used in place of a missing one; the reranker then refuses (rerank_unavailable).
        var reranker = new FakeReranker(new Dictionary<Guid, double> { [Id(4)] = 0.99 });
        using var engine = Engine(reranker);
        var call = new RerankCall(Ume.Rag.Domain.Residency.Eu, Reranker: kind);

        await engine.SearchAsync(new SearchRequest("altan", kind == RerankerKind.Gateway ? RerankScope(gatewayModel: null) : RerankScope(chatModel: null),
            Embed: Embed, Rerank: call), Ct);
        (reranker.Last!.Reranker, reranker.Last.Model).ShouldBe((kind, (string?)null));
        await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed, Rerank: call), Ct);
        reranker.Last!.Model.ShouldBe(kind == RerankerKind.Gateway ? "bge-reranker-v2-m3" : "ume/small-eu");
    }

    [Fact]
    public async Task The_request_names_the_reranker_and_its_model_and_each_has_its_own_time_limit()
    {
        // ChatModelReranker 3 s by default, GatewayReranker 1.5 s (#438); here shortened, a reranker taking 300 ms.
        var options = new RetrievalEngineOptions { RerankTimeout = TimeSpan.FromSeconds(5), GatewayRerankTimeout = TimeSpan.FromMilliseconds(100) };
        new RetrievalEngineOptions().RerankTimeout.ShouldBe(TimeSpan.FromSeconds(3));
        new RetrievalEngineOptions().GatewayRerankTimeout.ShouldBe(TimeSpan.FromMilliseconds(1500));
        var slow = new FakeReranker(new Dictionary<Guid, double> { [Id(6)] = 0.9 }, delay: TimeSpan.FromMilliseconds(300));
        using var engine = Engine(slow, options);

        var chat = await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed, Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu)), Ct);
        chat.Degraded.ShouldBeNull();
        chat.Hits[0].Candidate.ChunkId.ShouldBe(Id(6));
        (slow.Last!.Reranker, slow.Last.Model).ShouldBe((RerankerKind.ChatModel, "ume/small-eu"));

        var gateway = await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed,
            Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu, Reranker: RerankerKind.Gateway)), Ct);
        (slow.Last!.Reranker, slow.Last.Model).ShouldBe((RerankerKind.Gateway, "bge-reranker-v2-m3"));
        gateway.Degraded.ShouldBe(RetrievalDegradation.RerankUnavailable); // over the gateway reranker's limit: the fused order, the answer goes on
        gateway.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(5), Id(1), Id(2), Id(6), Id(3)]);
    }

    [Fact]
    public async Task The_router_sends_each_request_to_the_reranker_it_names()
    {
        var chat = new FakeReranker(new Dictionary<Guid, double> { [Id(1)] = 0.9 });
        var gateway = new FakeReranker(new Dictionary<Guid, double> { [Id(6)] = 0.9 });
        using var engine = Engine(new RerankRouter(chat, gateway));

        (await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed, Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu)), Ct))
            .Hits[0].Candidate.ChunkId.ShouldBe(Id(1));
        (await engine.SearchAsync(new SearchRequest("altan", RerankScope(), Embed: Embed,
            Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu, Reranker: RerankerKind.Gateway)), Ct)).Hits[0].Candidate.ChunkId.ShouldBe(Id(6));
        (chat.Calls, gateway.Calls).ShouldBe((1, 1));
    }

    [Fact]
    public async Task Large_scopes_use_hnsw_unless_too_selective()
    {
        var platform = RetrievalPlatformSettings.Default;
        _index.Counts = [new(Profile, CollectionA, 20_000), new(Profile, CollectionB, 1_000), new(Guid.NewGuid(), CollectionB, 5_000_000)];
        using var engine = Engine();

        (await engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Vector, Embed: Embed), Ct))
            .VectorSearch.ShouldBe(VectorSearch.Approximate);
        (await engine.SearchAsync(new SearchRequest("altan", Scope((CollectionB, RetrievalSettings.Default)), Mode: RetrievalMode.Vector, Embed: Embed), Ct))
            .VectorSearch.ShouldBe(VectorSearch.Exact);

        ScopeSizeCache.Choose(new ScopeSize(10_000, 10_000_000), platform).ShouldBe(new VectorPlan(VectorSearch.Exact, false));
        ScopeSizeCache.Choose(new ScopeSize(20_000, 10_000_000), platform).ShouldBe(new VectorPlan(VectorSearch.Approximate, false)); // 0.2 %
        ScopeSizeCache.Choose(new ScopeSize(19_999, 10_000_000), platform).ShouldBe(new VectorPlan(VectorSearch.Exact, true));
        ScopeSizeCache.Choose(new ScopeSize(10_001, 0), platform).ShouldBe(new VectorPlan(VectorSearch.Approximate, false));
    }

    [Fact]
    public async Task A_narrow_topic_in_a_collection_over_the_limit_is_searched_exactly()
    {
        // B-10 (Decisions "M-2 open design calls", to-verify #180): 20,000 chunks in A, 300 of them on "avfall" (100 also
        // on "taxa"), 200 on "taxa" alone, 19,500 on other tags or untagged; the profile holds 20,000 chunks in all.
        _index.Counts =
        [
            new(Profile, CollectionA, 200) { Tags = ["avfall"] },
            new(Profile, CollectionA, 100) { Tags = ["avfall", "taxa"] },
            new(Profile, CollectionA, 200) { Tags = ["taxa"] },
            new(Profile, CollectionA, 9_500) { Tags = ["bygglov"] },
            new(Profile, CollectionA, 10_000),
        ];
        using var engine = Engine();

        SearchScope Linked(params string[] topics) =>
            new(Profile, 3, [new CollectionInScope(CollectionA, RetrievalSettings.Default) { Topics = topics }], ["public"], RetrievalPlatformSettings.Default);

        (await _sizes.SizeAsync(OneCollection(), Ct)).ShouldBe(new ScopeSize(20_000, 20_000));
        (await _sizes.SizeAsync(Linked("avfall"), Ct)).ShouldBe(new ScopeSize(300, 20_000));
        (await _sizes.SizeAsync(Linked("avfall", "taxa"), Ct)).ShouldBe(new ScopeSize(500, 20_000)); // the both-tagged 100 count once
        (await _sizes.SizeAsync(Linked("okänt"), Ct)).ShouldBe(new ScopeSize(0, 20_000));

        (await engine.SearchAsync(new SearchRequest("avfall", Linked("avfall"), Mode: RetrievalMode.Vector, Embed: Embed), Ct))
            .VectorSearch.ShouldBe(VectorSearch.Exact);
        (await engine.SearchAsync(new SearchRequest("avfall", OneCollection(), Mode: RetrievalMode.Vector, Embed: Embed), Ct))
            .VectorSearch.ShouldBe(VectorSearch.Approximate);
        (await engine.SearchAsync(new SearchRequest("bygglov", Linked("bygglov", "avfall"), Mode: RetrievalMode.Vector, Embed: Embed), Ct))
            .VectorSearch.ShouldBe(VectorSearch.Exact); // 9,800 chunks: under the limit as well
        _index.VectorSearches.ShouldBe([VectorSearch.Exact, VectorSearch.Approximate, VectorSearch.Exact]);
    }

    [Fact]
    public async Task No_free_slot_within_the_wait_is_search_unavailable()
    {
        // One search at once is two statement slots (§6.6, #764): K of two searches holds both.
        using var engine = Engine(options: new RetrievalEngineOptions { MaxConcurrentSearches = 1, SlotWait = TimeSpan.FromMilliseconds(50) });
        _index.HoldKeyword = new TaskCompletionSource();
        var first = engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct);
        var second = engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct);

        var refused = await Should.ThrowAsync<SearchUnavailableException>(() =>
            engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct));
        refused.Message.ShouldContain("slot");
        _index.KeywordQueries.Count.ShouldBe(2); // the refused search ran no statement

        _index.HoldKeyword.SetResult();
        await first;
        await second;
        await engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct); // slots released
    }

    [Fact]
    public async Task A_statement_waits_for_a_slot_so_no_more_than_two_per_search_slot_run_at_once()
    {
        using var engine = Engine(options: new RetrievalEngineOptions { MaxConcurrentSearches = 1, SlotWait = TimeSpan.FromSeconds(30) });
        _index.HoldKeyword = new TaskCompletionSource();
        var searches = Enumerable.Range(0, 3)
            .Select(_ => engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct)).ToList();

        _index.KeywordQueries.Count.ShouldBe(2); // the third waits for a slot; its statement has not started

        _index.HoldKeyword.SetResult();
        await Task.WhenAll(searches);
        _index.KeywordQueries.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_search_waiting_for_its_query_embedding_holds_no_slot()
    {
        // #764: the embedding call uses no connection. Three searches wait for it at once on an engine with two
        // statement slots (before: one search slot, so the second was refused after SlotWait), and K of another still runs.
        using var engine = Engine(options: new RetrievalEngineOptions { MaxConcurrentSearches = 1, SlotWait = TimeSpan.FromMilliseconds(50) });
        var embedding = new TaskCompletionSource();
        var waiting = 0;
        async Task<ReadOnlyMemory<float>> Held(string query, CancellationToken ct)
        {
            Interlocked.Increment(ref waiting);
            await embedding.Task.WaitAsync(ct);
            return await Embed(query, ct);
        }

        var searches = Enumerable.Range(0, 3).Select(_ => engine.SearchAsync(new SearchRequest("altan", OneCollection(), Embed: Held), Ct)).ToList();
        waiting.ShouldBe(3);
        await Task.Delay(TimeSpan.FromMilliseconds(150), Ct); // longer than SlotWait: nothing is refused meanwhile

        var keyword = await engine.SearchAsync(new SearchRequest("altan", OneCollection(), Mode: RetrievalMode.Keyword), Ct);
        keyword.Hits.ShouldNotBeEmpty();

        embedding.SetResult();
        foreach (var result in await Task.WhenAll(searches))
        {
            result.VectorSearch.ShouldBe(VectorSearch.Exact);
            result.Degraded.ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_search_waiting_for_its_rerank_call_holds_no_slot()
    {
        // #764: neither does the rerank call. Three searches wait in the reranker; a fourth runs K, V and its own text read.
        var reranker = new HeldReranker();
        using var engine = Engine(reranker, new RetrievalEngineOptions { MaxConcurrentSearches = 1, SlotWait = TimeSpan.FromMilliseconds(50) });
        SearchRequest Request() => new("altan", RerankScope(), Embed: Embed, Rerank: new RerankCall(Ume.Rag.Domain.Residency.Eu));

        var searches = Enumerable.Range(0, 3).Select(_ => engine.SearchAsync(Request(), Ct)).ToList();
        reranker.Waiting.ShouldBe(3);
        await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);

        var fourth = engine.SearchAsync(Request(), Ct);
        reranker.Waiting.ShouldBe(4); // through K, V and the text read while the others wait

        reranker.Release.SetResult();
        foreach (var result in await Task.WhenAll([.. searches, fourth]))
        {
            result.Degraded.ShouldBeNull();
            result.Hits[0].Scores.RerankScore.ShouldNotBeNull();
        }
    }

    /// <summary>A reranker whose calls wait for <see cref="Release"/>, then score every passage in its given order.</summary>
    private sealed class HeldReranker : IReranker
    {
        private int _waiting;

        public TaskCompletionSource Release { get; } = new();

        public int Waiting => Volatile.Read(ref _waiting);

        public async Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waiting);
            await Release.Task.WaitAsync(cancellationToken);
            return new RerankScores([.. request.Passages.Select((_, i) => (double?)(1.0 - (i * 0.01)))]);
        }
    }

    [Fact]
    public async Task An_empty_scope_searches_nothing_and_is_low()
    {
        using var engine = Engine();
        var result = await engine.SearchAsync(new SearchRequest("altan", Scope(), Embed: Embed), Ct);

        result.Hits.ShouldBeEmpty();
        result.Coverage.Status.ShouldBe(CoverageStatus.Low);
        _index.KeywordQueries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_requests_are_refused()
    {
        using var engine = Engine();
        await Should.ThrowAsync<ArgumentException>(() => engine.SearchAsync(new SearchRequest("  ", OneCollection(), Embed: Embed), Ct));
        await Should.ThrowAsync<ArgumentException>(() => engine.SearchAsync(new SearchRequest("altan", OneCollection(), TopK: 0, Embed: Embed), Ct));
        await Should.ThrowAsync<ArgumentException>(() => engine.SearchAsync(new SearchRequest("altan", OneCollection(), TopK: 51, Embed: Embed), Ct));
        await Should.ThrowAsync<ArgumentException>(() => engine.SearchAsync(new SearchRequest("altan", OneCollection()), Ct)); // hybrid without embedder
        await Should.ThrowAsync<ArgumentException>(() => engine.SearchAsync(new SearchRequest("altan", OneCollection(),
            Embed: (_, _) => Task.FromResult<ReadOnlyMemory<float>>(new float[4])), Ct));                                     // wrong dimensions
        await Should.ThrowAsync<ArgumentException>(() => engine.SearchAsync(new SearchRequest("altan",
            Scope((CollectionA, RetrievalSettings.Default), (CollectionA, RetrievalSettings.Default)), Embed: Embed), Ct));
    }

    [Fact]
    public async Task Collections_that_disagree_rank_with_the_platform_defaults()
    {
        var capOne = RetrievalSettings.Default with { MaxChunksPerDocument = 1 };
        using var engine = Engine();

        var agreed = await engine.SearchAsync(new SearchRequest("altan", Scope((CollectionA, capOne), (CollectionB, capOne)), Mode: RetrievalMode.Vector, Embed: Embed), Ct);
        agreed.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(1), Id(5)]);

        var differing = await engine.SearchAsync(new SearchRequest("altan", Scope((CollectionA, capOne), (CollectionB, RetrievalSettings.Default)), Mode: RetrievalMode.Vector, Embed: Embed), Ct);
        differing.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(1), Id(2), Id(3), Id(5)]);
    }
}
