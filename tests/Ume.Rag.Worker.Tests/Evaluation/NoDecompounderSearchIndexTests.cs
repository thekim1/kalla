extern alias fakegateway;

using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Evaluation;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Tests.Engine;
using FakeContent = fakegateway::Ume.Rag.FakeGateway.FakeContent;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>The evaluation's <c>hybrid-no-decompounder</c> keyword statement drops the scope's stop list as statement K does.</summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class NoDecompounderSearchIndexTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Stop_lexemes_are_removed_from_the_query()
    {
        var seed = await EngineKit.SeedUploadAsync(database, Classification.Public, Guid.CreateVersion7(), Guid.CreateVersion7());
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), "SELECT content.rag_ensure_profile_index($1, $2)", EngineKit.ProfileId, EngineKit.Dimensions);
        await using (var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin),
            (text, dims) => FakeContent.Vector(text, dims)))
        {
            foreach (var text in new[] { "Simhallen i Umeå kommun har öppet i dag.", "Kommunen har en xylofon i simhallen." })
            {
                await writer.AddDocumentAsync(new FixtureDocumentSpec
                {
                    CollectionId = seed.CollectionId, EmbeddingProfileId = EngineKit.ProfileId, SourceId = seed.SourceId,
                    Classification = seed.Classification, Url = $"https://example.se/{Guid.NewGuid():N}", Title = "Fixture",
                }, [new FixtureChunk(text)], Ct);
            }
        }

        await using var dataSource = NpgsqlDataSource.Create(database.Connection(RagDatabase.Worker));
        var index = new NoDecompounderSearchIndex(dataSource, new NoVectors(), TimeSpan.FromSeconds(10));
        var scope = new SearchScope(EngineKit.ProfileId, EngineKit.Dimensions, [new CollectionInScope(seed.CollectionId, RetrievalSettings.Default)],
            [Classification.Public], RetrievalPlatformSettings.Default);

        (await index.KeywordAsync(scope, "simhall xylofon", null, 10, Ct)).Count.ShouldBe(2);
        var stopped = await index.KeywordAsync(scope with { KeywordStopLexemes = ["simhall"] }, "simhall xylofon", null, 10, Ct);
        stopped.ShouldHaveSingleItem("only the chunk with the remaining word matches");
        (await index.KeywordAsync(scope with { KeywordStopLexemes = ["simhall", "xylofon"] }, "simhall xylofon", null, 10, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_evaluator_searches_whole_collections_and_a_scope_limited_to_topics_is_refused()
    {
        var target = new EvaluationTarget(Guid.CreateVersion7(), Classification.Public, Guid.CreateVersion7(), null,
            new EmbeddingProfileRecord(EngineKit.ProfileId, "fixture", "fixture-embed", EngineKit.Dimensions, 512, Residency.Eu),
            RetrievalPlatformSettings.Default, RetrievalSettings.Default, new Dictionary<Guid, IReadOnlyList<DemotePattern>>(), null, []);
        var evaluated = target.Scope(RetrievalSettings.Default);
        evaluated.TopicLinks.ShouldBeEmpty();
        RetrievalStatements.FragmentsOf(evaluated, null).ShouldBe(FilterFragments.None);

        await using var dataSource = NpgsqlDataSource.Create(database.Connection(RagDatabase.Worker));
        var index = new NoDecompounderSearchIndex(dataSource, new NoVectors(), TimeSpan.FromSeconds(10));
        var linked = new SearchScope(evaluated.EmbeddingProfileId, evaluated.Dimensions, [evaluated.Collections[0] with { Topics = ["bygglov"] }],
            evaluated.AllowedLevels, evaluated.Platform);
        await Should.ThrowAsync<NotSupportedException>(() => index.KeywordAsync(linked, "bygglov", null, 10, Ct));
    }

    private sealed class NoVectors : ISearchIndex
    {
        public Task<IReadOnlyList<Candidate>> KeywordAsync(SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Candidate>> VectorAsync(
            SearchScope scope, ReadOnlyMemory<float> vector, RetrievalFilters? filters, int limit, VectorSearch search, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
