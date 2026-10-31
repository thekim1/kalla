using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>Settings resolution (knowledge design §12.1), the chunk-count cache (§6.4) and the statement variants (§6.2).</summary>
public sealed class RetrievalSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Collection_values_override_platform_defaults_which_override_the_built_in_ones()
    {
        var platform = RetrievalSettingsResolver.Platform("""
            { "keywordWeight": 0.5, "coverageMaxDistance": 0.45, "exactScanMaxChunks": 5000, "minHnswSelectivity": 0.01, "someP2Key": 1 }
            """);
        platform.ExactScanMaxChunks.ShouldBe(5000);
        platform.MinHnswSelectivity.ShouldBe(0.01);
        platform.Defaults.KeywordWeight.ShouldBe(0.5);
        platform.Defaults.VectorWeight.ShouldBe(1.0);

        var collection = RetrievalSettingsResolver.Collection(platform, """
            { "maxChunksPerDocument": 2, "coverageMinKeywordScore": 0.1, "recency": { "halfLifeDays": 730, "weight": 0.15, "kinds": ["pdf"] } }
            """);
        collection.MaxChunksPerDocument.ShouldBe(2);
        collection.KeywordWeight.ShouldBe(0.5);
        collection.CoverageMaxDistance.ShouldBe(0.45);
        collection.CoverageMinKeywordScore.ShouldBe(0.1);
        collection.Recency.ShouldNotBeNull().Kinds.ShouldBe(["pdf"]);

        RetrievalSettingsResolver.Collection(platform, """{ "coverageMinKeywordScore": null }""").CoverageMinKeywordScore.ShouldBeNull();
        RetrievalSettingsResolver.Collection(RetrievalSettingsResolver.Platform(null), "{}").ShouldBe(RetrievalSettings.Default);
    }

    [Theory]
    [InlineData("""{ "maxChunksPerDocument": 11 }""")]
    [InlineData("""{ "candidates": 10 }""")]
    [InlineData("""{ "vectorWeight": "high" }""")]
    [InlineData("""[1]""")]
    public void Out_of_range_or_mistyped_values_are_refused(string json) =>
        Should.Throw<Exception>(() => RetrievalSettingsResolver.Collection(RetrievalPlatformSettings.Default, json));

    [Fact]
    public async Task Chunk_counts_are_cached_and_refreshed_after_the_interval()
    {
        var index = new FakeIndex { Counts = [new(Profile, CollectionA, 5)] };
        var time = new FakeTimeProvider();
        using var cache = new ScopeSizeCache(index, time);

        (await cache.SizeAsync(OneCollection(), Ct)).ShouldBe(new ScopeSize(5, 5));
        index.Counts = [new(Profile, CollectionA, 50_000)];
        (await cache.SizeAsync(OneCollection(), Ct)).ScopeChunks.ShouldBe(5);
        time.Advance(TimeSpan.FromMinutes(10));
        (await cache.SizeAsync(OneCollection(), Ct)).ScopeChunks.ShouldBe(50_000);
        (await cache.SizeAsync(Scope((CollectionB, RetrievalSettings.Default)), Ct)).ShouldBe(new ScopeSize(0, 50_000)); // unknown = empty
    }

    [Fact]
    public void Every_statement_variant_is_distinct_and_numbers_its_filters_in_order()
    {
        var texts = new HashSet<string>(StringComparer.Ordinal);
        for (var mask = FilterFragments.None; mask <= FilterFragments.All; mask++)
        {
            texts.Add(RetrievalStatements.Keyword(Profile, 100, mask)).ShouldBeTrue();
            texts.Add(RetrievalStatements.VectorExact(Profile, 1024, 100, mask)).ShouldBeTrue();
            texts.Add(RetrievalStatements.VectorApproximate(Profile, 1024, 100, mask)).ShouldBeTrue();
        }

        texts.Count.ShouldBe(24);
        var all = RetrievalStatements.VectorExact(Profile, 1024, 100, FilterFragments.All);
        all.ShouldContain("""c."Tags" && $4""");
        all.ShouldContain("""c."DocumentType" = ANY ($5)""");
        all.ShouldContain("""c."SourceModifiedAt" >= $6""");
        RetrievalStatements.Keyword(Profile, 100, FilterFragments.ModifiedAfter).ShouldContain("""c."SourceModifiedAt" >= $4""");
        RetrievalStatements.Keyword(Profile, 100, FilterFragments.None).ShouldNotContain("IS NULL OR");
        all.ShouldContain($"""c."EmbeddingProfileId" = '{Profile}'::uuid""");
        RetrievalStatements.FragmentsOf(new RetrievalFilters(Tags: [], DocumentTypes: ["pdf"])).ShouldBe(FilterFragments.DocumentTypes);
        RetrievalStatements.Keyword(Profile, 100, FilterFragments.None).ShouldBeSameAs(RetrievalStatements.Keyword(Profile, 100, FilterFragments.None));
    }
}
