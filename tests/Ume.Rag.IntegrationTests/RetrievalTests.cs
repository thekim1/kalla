using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The search core on Postgres (knowledge design §16.2; to-verify #4, #7, #19): statements K, V and H+D as
/// <c>ume_rag_api</c> over chunks written by <see cref="ContentFixtureWriter"/>; every variant runs and is auto-prepared
/// once per connection; generic plans use the partial GIN, the per-profile HNSW and the <c>CollectionId</c> B-tree as
/// designed; Swedish compounds and inflections; defence in depth; the statement time limit.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class RetrievalTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const int Dims = 3;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ContentFixtureWriter _writer = null!;
    private NpgsqlDataSource _api = null!;
    private Guid _profile;

    public async ValueTask InitializeAsync()
    {
        _writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin));
        _api = RetrievalDataSource.Create(database.Connection(Api));
        _profile = await _writer.AddEmbeddingProfileAsync(Dims, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _api.DisposeAsync();
    }

    private static float[] V(float x, float y, float z) => [x, y, z];

    private SearchScope Scope(params Guid[] collections) => Scope(["public"], collections);

    private SearchScope Scope(string[] levels, params Guid[] collections) => new(
        _profile, Dims, [.. collections.Select(c => new CollectionInScope(c, RetrievalSettings.Default))], levels, RetrievalPlatformSettings.Default);

    private FixtureDocumentSpec Doc(Guid collection, string classification = "public", string? url = null) =>
        new() { CollectionId = collection, EmbeddingProfileId = _profile, Classification = classification, Url = url, Title = "Fixture" };

    private (RetrievalEngine Engine, PostgresSearchIndex Index, ScopeSizeCache Sizes) Engine(NpgsqlDataSource? dataSource = null)
    {
        var index = new PostgresSearchIndex(dataSource ?? _api);
        var sizes = new ScopeSizeCache(index, new FakeTimeProvider());
        return (new RetrievalEngine(index, sizes), index, sizes);
    }

    private static QueryEmbedder Fixed(float[] vector) => (_, _) => Task.FromResult<ReadOnlyMemory<float>>(vector);

    [Fact]
    public async Task Hybrid_search_fuses_k_and_v_and_both_vector_plans_agree_on_a_small_scope()
    {
        var collection = Guid.NewGuid();
        var near = await _writer.AddDocumentAsync(Doc(collection, url: "https://www.umea.se/bygglov"),
        [
            new FixtureChunk("Ansökan om bygglov lämnas via e-tjänsten.") { Embedding = V(1, 0, 0), HeadingPath = "Bygga › Bygglov" },
            new FixtureChunk("Handläggningstiden är tio veckor.") { Embedding = V(0.9f, 0.1f, 0), HeadingPath = "Bygga › Bygglov" },
        ], Ct);
        var keywordOnly = await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("Diarienummer BN-2026-0042 gäller bygglov.") { Embedding = V(0, 0, 1) }], Ct);
        var (engine, index, sizes) = Engine();
        using var _ = engine;
        using var __ = sizes;

        var result = await engine.SearchAsync(new SearchRequest("bygglov", Scope(collection), Embed: Fixed(V(1, 0, 0))), Ct);

        result.VectorSearch.ShouldBe(VectorSearch.Exact);
        result.Hits[0].Candidate.ChunkId.ShouldBe(near.ChunkIds[0]);
        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([near.ChunkIds[0], keywordOnly.ChunkIds[0], near.ChunkIds[1]], ignoreOrder: true);
        result.Hits[0].Scores.VectorDistance!.Value.ShouldBe(0, 1e-6);
        result.Hits[0].Scores.KeywordScore.ShouldNotBeNull().ShouldBeGreaterThan(0);
        result.Hits[0].Candidate.Url.ShouldBe("https://www.umea.se/bygglov");
        result.Coverage.Status.ShouldBe(CoverageStatus.Ok);

        var exact = await index.VectorAsync(Scope(collection), V(1, 0, 0), null, 100, VectorSearch.Exact, Ct);
        var approximate = await index.VectorAsync(Scope(collection), V(1, 0, 0), null, 100, VectorSearch.Approximate, Ct);
        approximate.OrderBy(c => c.Distance).Select(c => c.ChunkId).ShouldBe(exact.OrderBy(c => c.Distance).Select(c => c.ChunkId));
        approximate.Zip(exact.OrderBy(c => c.Distance)).ShouldAllBe(p => Math.Abs(p.First.Distance!.Value - p.Second.Distance!.Value) < 1e-3);
    }

    [Fact]
    public async Task Bygglov_altan_finds_a_chunk_with_only_the_compound_and_an_inflection()
    {
        var collection = Guid.NewGuid();
        var doc = await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("Skicka in din bygglovsansökan innan du bygger altanen.") { Embedding = V(0, 1, 0) }], Ct);
        await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("Sophämtning sker varannan vecka.") { Embedding = V(0, 0, 1) }], Ct);
        var (engine, _, sizes) = Engine();
        using var e = engine;
        using var s = sizes;

        var result = await engine.SearchAsync(new SearchRequest("bygglov altan", Scope(collection), Mode: RetrievalMode.Keyword), Ct);
        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([doc.ChunkIds[0]]);

        // OR semantics: one known word among unknown ones is enough; stop words alone match nothing.
        (await engine.SearchAsync(new SearchRequest("zebra altan giraff", Scope(collection), Mode: RetrievalMode.Keyword), Ct)).Hits.Count.ShouldBe(1);
        (await engine.SearchAsync(new SearchRequest("och det är som", Scope(collection), Mode: RetrievalMode.Keyword), Ct)).Hits.ShouldBeEmpty();
        await using var connection = await _api.OpenConnectionAsync(Ct);
        await using var or = new NpgsqlCommand("SELECT content.rag_or_query(to_tsvector('swedish', 'bygglov altan'))::text", connection);
        var tsquery = (string)(await or.ExecuteScalarAsync(Ct))!;
        tsquery.ShouldContain(" | ");
        tsquery.ShouldNotContain(" & ");
    }

    [Fact]
    public async Task The_stop_list_drops_lexemes_in_more_than_thirty_percent_of_the_scope_from_K()
    {
        // 1,200 chunks: "Umeå kommun informerar" in all, "simhall" in a third (400 > 360), "xylofon" in a tenth. The topic word
        // is used by no other test: these chunks stay in the shared database and move its statistics (the planner's choices
        // in IngestionSchemaTests).
        var collection = Guid.NewGuid();
        var xylofon = new HashSet<Guid>();
        for (var document = 0; document < 12; document++)
        {
            var spec = Enumerable.Range(0, 100).Select(i => document * 100 + i).ToList();
            var doc = await _writer.AddDocumentAsync(Doc(collection), [.. spec.Select(n => new FixtureChunk(
                $"Umeå kommun informerar om sida {n}{(n % 3 == 0 ? " simhall" : "")}{(n % 10 == 0 ? " xylofon" : "")}") { Embedding = V(1, 0, 0) })], Ct);
            xylofon.UnionWith(spec.Select((n, i) => (n, i)).Where(p => p.n % 10 == 0).Select(p => doc.ChunkIds[p.i]));
        }

        var (engine, index, sizes) = Engine();
        using var e = engine;
        using var s = sizes;
        var lexemes = (await index.FrequentLexemesAsync(100, 0.10, Ct)).Where(l => l.CollectionId == collection).ToDictionary(l => l.Lexeme, l => l.Chunks);
        lexemes["kommun"].ShouldBe(1200);
        lexemes["simhall"].ShouldBe(400);
        lexemes["xylofon"].ShouldBe(120);
        lexemes.Keys.ShouldNotContain("1"); // "sida {n}" numbers are in one chunk each, under 10 %
        var stop = await sizes.StopLexemesAsync(Scope(collection), Ct);
        stop.ShouldContain("kommun");
        stop.ShouldContain("simhall");
        stop.ShouldNotContain("xylofon");

        // Through the engine: only the topic word is searched, so every keyword hit has it.
        var result = await engine.SearchAsync(new SearchRequest("xylofon för simhall i Umeå kommun", Scope(collection), Mode: RetrievalMode.Keyword), Ct);
        result.Hits.Count.ShouldBe(10);
        result.Hits.ShouldAllBe(h => xylofon.Contains(h.Candidate.ChunkId));
        (await index.KeywordAsync(Scope(collection) with { KeywordStopLexemes = stop }, "xylofon för simhall i Umeå kommun", null, 200, Ct)).Count.ShouldBe(120);
        (await index.KeywordAsync(Scope(collection), "xylofon för simhall i Umeå kommun", null, 200, Ct)).Count.ShouldBe(200); // no stop list: everything

        // Stop lexemes only: no query, no candidates, like stop words (the vector side still answers in hybrid).
        (await engine.SearchAsync(new SearchRequest("Umeå kommun", Scope(collection), Mode: RetrievalMode.Keyword), Ct)).Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Filters_apply_in_both_statements_and_only_when_present()
    {
        var collection = Guid.NewGuid();
        var old = DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var recent = DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var pdf = await _writer.AddDocumentAsync(Doc(collection),
            [new FixtureChunk("Taxa för avfall 2026") { Embedding = V(1, 0, 0), DocumentType = "pdf", Tags = ["taxa"], SourceModifiedAt = recent }], Ct);
        var html = await _writer.AddDocumentAsync(Doc(collection),
            [new FixtureChunk("Taxa för avfall 2020") { Embedding = V(1, 0.1f, 0), DocumentType = "html", Tags = ["arkiv"], SourceModifiedAt = old }], Ct);
        var index = new PostgresSearchIndex(_api);
        var scope = Scope(collection);

        foreach (var filters in new[]
                 {
                     new RetrievalFilters(Tags: ["taxa"]),
                     new RetrievalFilters(DocumentTypes: ["pdf"]),
                     new RetrievalFilters(ModifiedAfter: DateTimeOffset.Parse("2026-01-01T01:00:00+01:00", System.Globalization.CultureInfo.InvariantCulture)),
                     new RetrievalFilters(["taxa", "x"], ["pdf"], recent.AddDays(-1)),
                 })
        {
            (await index.KeywordAsync(scope, "avfall", filters, 100, Ct)).Select(c => c.ChunkId).ShouldBe([pdf.ChunkIds[0]]);
            (await index.VectorAsync(scope, V(1, 0, 0), filters, 100, VectorSearch.Exact, Ct)).Select(c => c.ChunkId).ShouldBe([pdf.ChunkIds[0]]);
            (await index.VectorAsync(scope, V(1, 0, 0), filters, 100, VectorSearch.Approximate, Ct)).Select(c => c.ChunkId).ShouldBe([pdf.ChunkIds[0]]);
        }

        (await index.KeywordAsync(scope, "avfall", new RetrievalFilters(Tags: []), 100, Ct)).Count.ShouldBe(2); // empty = absent
        (await index.VectorAsync(scope, V(1, 0, 0), null, 100, VectorSearch.Exact, Ct)).Select(c => c.ChunkId).ShouldBe([pdf.ChunkIds[0], html.ChunkIds[0]], ignoreOrder: true);
    }

    [Fact]
    public async Task Levels_above_the_callers_invisible_sets_and_other_collections_never_come_back()
    {
        var collection = Guid.NewGuid();
        var other = Guid.NewGuid();
        var visible = await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("parkeringstillstånd synligt") { Embedding = V(1, 0, 0) }], Ct);
        var secret = await _writer.AddDocumentAsync(Doc(collection, "internal"), [new FixtureChunk("parkeringstillstånd internt") { Embedding = V(1, 0, 0) }], Ct);
        var held = await _writer.AddDocumentAsync(Doc(collection) with { SetState = "held" }, [new FixtureChunk("parkeringstillstånd hållet") { Embedding = V(1, 0, 0) }], Ct);
        var writing = await _writer.AddInvisibleSetAsync(visible, [new FixtureChunk("parkeringstillstånd skrivs") { Embedding = V(1, 0, 0) }], "writing", Ct);
        var superseded = await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("parkeringstillstånd gammalt") { Embedding = V(1, 0, 0) }], Ct);
        var current = await _writer.SwapAsync(superseded, [new FixtureChunk("parkeringstillstånd nytt") { Embedding = V(1, 0, 0) }], Ct);
        var elsewhere = await _writer.AddDocumentAsync(Doc(other), [new FixtureChunk("parkeringstillstånd annan samling") { Embedding = V(1, 0, 0) }], Ct);
        var index = new PostgresSearchIndex(_api);
        var hydrator = new PostgresHydrator(_api);
        var scope = Scope(collection);
        var expected = new[] { visible.ChunkIds[0], current.ChunkIds[0] };

        (await index.KeywordAsync(scope, "parkeringstillstånd", null, 100, Ct)).Select(c => c.ChunkId).ShouldBe(expected, ignoreOrder: true);
        (await index.VectorAsync(scope, V(1, 0, 0), null, 100, VectorSearch.Exact, Ct)).Select(c => c.ChunkId).ShouldBe(expected, ignoreOrder: true);
        (await index.VectorAsync(scope, V(1, 0, 0), null, 100, VectorSearch.Approximate, Ct)).Select(c => c.ChunkId).ShouldBe(expected, ignoreOrder: true);

        // With the internal level allowed, the internal chunk appears.
        (await index.KeywordAsync(Scope(["public", "internal"], collection), "parkeringstillstånd", null, 100, Ct)).Count.ShouldBe(3);

        // H re-checks levels, scope and DeletedAt for ids handed to it, whatever their origin.
        var all = new[] { visible.ChunkIds[0], secret.ChunkIds[0], held.ChunkIds[0], writing[0], superseded.ChunkIds[0], current.ChunkIds[0], elsewhere.ChunkIds[0] };
        var hydration = await hydrator.HydrateAsync(new HydrationRequest(all, [visible.Id, secret.Id, held.Id, current.Id, elsewhere.Id],
            ["public"], [collection], [visible.SourceId, current.SourceId, elsewhere.SourceId, held.SourceId]), Ct);
        hydration.Chunks.Select(c => c.ChunkId).ShouldBe(expected, ignoreOrder: true);
        hydration.Documents.Select(d => d.Id).ShouldBe([visible.Id, held.Id, current.Id, elsewhere.Id], ignoreOrder: true); // D: level only
        hydration.Documents.Single(d => d.Id == held.Id).Visible.ShouldBeFalse();     // no live set
        hydration.Documents.Single(d => d.Id == visible.Id).Visible.ShouldBeTrue();
        hydration.Documents.Single(d => d.Id == visible.Id).Language.ShouldBe("sv"); // granted to the api for citations
    }

    [Fact]
    public async Task A_candidate_whose_set_was_swapped_out_before_hydration_is_dropped()
    {
        var collection = Guid.NewGuid();
        var doc = await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("Förskoleplats söks i e-tjänsten.") { Embedding = V(1, 0, 0) }], Ct);
        var (engine, _, sizes) = Engine();
        using var e = engine;
        using var s = sizes;
        var result = await engine.SearchAsync(new SearchRequest("förskoleplats", Scope(collection), Embed: Fixed(V(1, 0, 0))), Ct);
        var hit = result.Hits.ShouldHaveSingleItem().Candidate;

        // The index writer swaps the document between V and H; the old set's chunks are deleted in the same transaction.
        var swapped = await _writer.SwapAsync(doc, [new FixtureChunk("Förskoleplats söks i e-tjänsten, nya regler.") { Embedding = V(1, 0, 0) }], Ct);
        var hydration = await new PostgresHydrator(_api).HydrateAsync(
            new HydrationRequest([hit.ChunkId], [hit.DocumentId], ["public"], [collection], [doc.SourceId]), Ct);
        hydration.HasHit(hit.ChunkId).ShouldBeFalse();

        // Safety net on its own: a chunk not deleted but no longer in the live set is dropped by the LiveChunkSetId check.
        await _writer.ExecuteAsync($"""UPDATE content."Chunks" SET "DeletedAt" = NULL WHERE "Id" = '{hit.ChunkId}'""", Ct);
        hydration = await new PostgresHydrator(_api).HydrateAsync(
            new HydrationRequest([hit.ChunkId, swapped.ChunkIds[0]], [hit.DocumentId], ["public"], [collection], [doc.SourceId]), Ct);
        hydration.HasHit(hit.ChunkId).ShouldBeFalse();
        hydration.HasHit(swapped.ChunkIds[0]).ShouldBeTrue();
    }

    [Fact]
    public async Task Hydration_returns_the_hits_section_neighbours_by_heading_path_in_one_batch()
    {
        var collection = Guid.NewGuid();
        var doc = await _writer.AddDocumentAsync(Doc(collection),
        [
            new FixtureChunk("Inledning") { HeadingPath = "Bygglov", Embedding = V(1, 0, 0) },
            new FixtureChunk("Krav del 1") { HeadingPath = "Bygglov › Krav", Embedding = V(1, 0, 0) },
            new FixtureChunk("Krav del 2") { HeadingPath = "Bygglov › Krav", Embedding = V(1, 0, 0), OverlapChars = 4, Page = 3, PageEnd = 4 },
            new FixtureChunk("Krav detalj") { HeadingPath = "Bygglov › Krav › Mått", Embedding = V(1, 0, 0) },
            new FixtureChunk("Avgifter") { HeadingPath = "Bygglov › Avgifter", Embedding = V(1, 0, 0) },
            new FixtureChunk("Utan rubrik") { Embedding = V(1, 0, 0) },
        ], Ct);
        var hydrator = new PostgresHydrator(_api);

        var section = await hydrator.HydrateAsync(new HydrationRequest([doc.ChunkIds[2]], [doc.Id], ["public"], [collection], [doc.SourceId], 8), Ct);
        section.Chunks.Select(c => c.Ordinal).ShouldBe([1, 2, 3]); // same path or below it; not the parent, not the sibling
        var hit = section.Chunks.Single(c => c.ChunkId == doc.ChunkIds[2]);
        (hit.OverlapChars, hit.Page, hit.PageEnd, hit.Text).ShouldBe(((short)4, (int?)3, (int?)4, "Krav del 2"));

        var only = await hydrator.HydrateAsync(new HydrationRequest([doc.ChunkIds[2]], [doc.Id], ["public"], [collection], [doc.SourceId], 0), Ct);
        only.Chunks.Select(c => c.ChunkId).ShouldBe([doc.ChunkIds[2]]);

        // A hit without a heading path comes back (the design's "=" alone would drop it: NULL = NULL is not true).
        var unheaded = await hydrator.HydrateAsync(new HydrationRequest([doc.ChunkIds[5]], [doc.Id], ["public"], [collection], [doc.SourceId], 8), Ct);
        unheaded.Chunks.Select(c => c.ChunkId).ShouldBe([doc.ChunkIds[5]]);
    }

    [Fact]
    public async Task Every_statement_variant_runs_and_is_auto_prepared_once_per_connection()
    {
        // to-verify #19: one physical connection, so its pg_prepared_statements shows every variant.
        var builder = new NpgsqlConnectionStringBuilder(database.Connection(Api)) { MaxPoolSize = 1 };
        await using var single = RetrievalDataSource.Create(builder.ConnectionString);
        var collection = Guid.NewGuid();
        await _writer.AddDocumentAsync(Doc(collection), [new FixtureChunk("avfallstaxa") { Embedding = V(1, 0, 0), Tags = ["taxa"], DocumentType = "pdf" }], Ct);
        var index = new PostgresSearchIndex(single);
        var scope = Scope(collection);

        for (var round = 0; round < 3; round++)
        {
            for (var mask = FilterFragments.None; mask <= FilterFragments.All; mask++)
            {
                var filters = new RetrievalFilters(
                    mask.HasFlag(FilterFragments.Tags) ? ["taxa"] : null,
                    mask.HasFlag(FilterFragments.DocumentTypes) ? ["pdf"] : null,
                    mask.HasFlag(FilterFragments.ModifiedAfter) ? DateTimeOffset.UnixEpoch : null);
                (await index.KeywordAsync(scope, "avfall taxa", filters, 100, Ct)).Count.ShouldBe(mask.HasFlag(FilterFragments.ModifiedAfter) ? 0 : 1);
                await index.VectorAsync(scope, V(1, 0, 0), filters, 100, VectorSearch.Exact, Ct);
                await index.VectorAsync(scope, V(1, 0, 0), filters, 100, VectorSearch.Approximate, Ct);
            }
        }

        await using var connection = await single.OpenConnectionAsync(Ct);
        await using var prepared = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE statement LIKE '%kw AS MATERIALIZED%'),
                   count(*) FILTER (WHERE statement LIKE '%vec AS MATERIALIZED%' AND statement LIKE '%::halfvec(%'),
                   count(*) FILTER (WHERE statement LIKE '%vec AS MATERIALIZED%' AND statement NOT LIKE '%::halfvec(%'),
                   bool_and(NOT from_sql)
            FROM pg_prepared_statements
            """, connection);
        await using var reader = await prepared.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3)).ShouldBe((8L, 8L, 8L, true));
        await reader.DisposeAsync();

        // No SET runs: the HNSW settings on this api connection are the role defaults (data-model §1.1).
        foreach (var (name, value) in new[] { ("hnsw.ef_search", "100"), ("hnsw.iterative_scan", "relaxed_order"), ("hnsw.max_scan_tuples", "100000") })
        {
            await using var show = new NpgsqlCommand($"SHOW {name}", connection);
            ((string)(await show.ExecuteScalarAsync(Ct))!).ShouldBe(value);
        }
    }

    // Flaky (#297): the planner's choice over the shared fixture's tables, which parallel test classes write while this
    // runs; it failed CI twice on 2026-11-01 after #253. It runs locally and in the weekly flake hunt, not in the PR gate.
    [Fact]
    [Trait(TestCategory.Name, TestCategory.Flaky)]
    public async Task Generic_plans_use_the_partial_gin_the_profiles_hnsw_and_the_collection_btree_as_designed()
    {
        // to-verify #4. Enough rows of the profile for the statistics to mean something; planner shortcuts disabled
        // so the plan shows what an index can serve.
        var collection = Guid.NewGuid();
        var random = new Random(7);
        var chunks = Enumerable.Range(0, 3000)
            .Select(i => new FixtureChunk($"sida {i} avfall{i} sophämtning") { Embedding = V((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) })
            .ToList();
        await _writer.AddDocumentAsync(Doc(collection), chunks, Ct);
        await Sql.ExecuteAsync(database.Connection(null), """ANALYZE content."Chunks" """);
        var hnsw = "IX_Chunks_Embedding_" + _profile.ToString("N");

        foreach (var mask in new[] { FilterFragments.None, FilterFragments.DocumentTypes | FilterFragments.ModifiedAfter, FilterFragments.All })
        {
            var approximate = await ExplainGenericAsync(RetrievalStatements.VectorApproximate(_profile, Dims, 100, mask), "enable_seqscan", "enable_sort");
            approximate.ShouldContain(hnsw, customMessage: approximate);

            var exact = await ExplainGenericAsync(RetrievalStatements.VectorExact(_profile, Dims, 100, mask), "enable_seqscan", "enable_sort");
            exact.ShouldNotContain("IX_Chunks_Embedding", customMessage: exact); // HNSW cannot serve it, even with sorting penalised
            var keyword = await ExplainGenericAsync(RetrievalStatements.Keyword(_profile, 100, mask), "enable_seqscan", "enable_indexscan");
            if (!mask.HasFlag(FilterFragments.Tags))
            {
                // With a tags filter the tags GIN index (IX_Chunks_Tags) may be the cheaper way in, as the plan shows.
                exact.ShouldContain("IX_Chunks_CollectionId", customMessage: exact);
                keyword.ShouldContain("IX_Chunks_SearchVector", customMessage: keyword);
            }
        }

        // to-verify #7: H's neighbour lookup is the (ChunkSetId, Ordinal) range; starts_with is only a filter on it.
        var hydrate = await ExplainGenericAsync(RetrievalStatements.Hits, "enable_seqscan");
        hydrate.ShouldContain("IX_Chunks_ChunkSetId_Ordinal", customMessage: hydrate);
        hydrate.ShouldContain("starts_with", customMessage: hydrate);
        hydrate.ShouldNotContain("Index Cond: (starts_with", customMessage: hydrate);
    }

    [Fact]
    public async Task A_statement_over_its_time_limit_is_search_unavailable_within_two_seconds()
    {
        // The fixture function: rag_or_query sleeps 5 s for one magic lexeme only, so parallel tests are unaffected.
        var superuser = database.Connection(null);
        var original = await Sql.ScalarAsync<string>(superuser, "SELECT pg_get_functiondef('content.rag_or_query(tsvector)'::regprocedure)");
        await Sql.ExecuteAsync(superuser, """
            CREATE OR REPLACE FUNCTION content.rag_or_query(tsvector) RETURNS tsquery
            LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF $1 @@ 'zzlangsamzz'::tsquery THEN PERFORM pg_sleep(5); END IF;
                RETURN (SELECT content.rag_tsquery_or_agg(plainto_tsquery('simple'::regconfig, l)) FROM unnest(tsvector_to_array($1)) l);
            END $$
            """);
        try
        {
            var (engine, _, sizes) = Engine();
            using var e = engine;
            using var s = sizes;
            var started = Stopwatch.StartNew();
            var refused = await Should.ThrowAsync<SearchUnavailableException>(() =>
                engine.SearchAsync(new SearchRequest("zzlangsamzz", Scope(Guid.NewGuid()), Mode: RetrievalMode.Keyword), Ct));
            started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
            refused.Message.ShouldContain("time limit");
        }
        finally
        {
            await Sql.ExecuteAsync(superuser, original);
        }
    }

    /// <summary>
    /// The generic plan of a statement with <c>$n</c> parameters (<c>PREPARE</c>, then <c>EXPLAIN EXECUTE</c> under
    /// <c>plan_cache_mode = force_generic_plan</c>, as a pooled statement runs once auto-prepared), with the given planner
    /// methods off. Test-only SQL on its own connection, inside a transaction.
    /// </summary>
    private async Task<string> ExplainGenericAsync(string statement, params string[] off)
    {
        var parameters = System.Text.RegularExpressions.Regex.Matches(statement, @"\$(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        await using var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        foreach (var setting in off.Append("plan_cache_mode"))
        {
            var value = setting == "plan_cache_mode" ? "force_generic_plan" : "off";
            await using var command = new NpgsqlCommand($"SET LOCAL {setting} = {value}", connection, transaction);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var prepare = new NpgsqlCommand("PREPARE retrieval_plan AS " + statement, connection, transaction))
        {
            await prepare.ExecuteNonQueryAsync(Ct);
        }

        var nulls = string.Join(", ", Enumerable.Repeat("NULL", parameters));
        await using var explain = new NpgsqlCommand($"EXPLAIN EXECUTE retrieval_plan({nulls})", connection, transaction);
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}
