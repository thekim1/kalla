extern alias fakegateway;

using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Jobs;
using Kalla.Retrieval;
using Kalla.TestKit;
using Kalla.Worker.Evaluation;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;
using FakeContent = fakegateway::Kalla.FakeGateway.FakeContent;

namespace Kalla.Worker.Tests.Evaluation;

/// <summary>
/// Knowledge design §16.3 "Contract test evaluator == API" (processing design §13.5) on the smoke set: the 30 questions
/// of <c>tests/evaluation/smoke</c> as the <c>human</c> layer of the smoke collection (seeded by <see cref="SmokeSet"/> under
/// its fixed ids, FakeGateway embeddings, <see cref="SmokeSet.RetrievalSettings"/>). For every question the worker's
/// evaluator (<see cref="EvaluationStore"/> and <see cref="RetrievalEvaluator"/>, variant <c>hybrid</c>, k = 10, no filters)
/// and <c>POST /v1/retrieve</c> on rag-api in process (an <c>internal</c> client's key, top 10, <c>expand: none</c>, hybrid)
/// return the same chunk ids in the same order. The embeddings are deterministic, so a difference is wiring (settings,
/// scope, normalisation, diversity). The design places it in <c>Kalla.Api.Tests</c> (stream B's); it lives here because
/// this project references both the worker and rag-api.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class EvaluatorApiContractTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-eval-contract-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_evaluator_and_the_retrieve_endpoint_return_the_same_chunks_in_the_same_order_for_every_smoke_question()
    {
        var seed = await EngineKit.SeedUploadAsync(database, Classification.Public, SmokeSet.CollectionId, SmokeSet.SourceId);
        await EngineKit.PauseAsync(database, seed);
        await EvaluationSmokeKit.PublishAsync(database, seed.CollectionId);
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), "SELECT content.rag_ensure_profile_index($1, $2)", EngineKit.ProfileId, EngineKit.Dimensions);
        SmokeSeed smoke;
        await using (var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin),
            (text, dims) => FakeContent.Vector(text, dims)))
        {
            smoke = await SmokeSet.SeedAsync(writer, EngineKit.ProfileId, cancellationToken: Ct);
        }

        await AddQuestionsAsync(seed, smoke);
        var slug = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Slug" FROM config."Collections" WHERE "Id" = $1""", seed.CollectionId);
        var (_, key) = await EvaluationSmokeKit.AddClientAsync(database, ClientKinds.Internal, Classification.Sensitive);

        await using var worker = await StartWorkerAsync();
        var target = (await worker.Services.GetRequiredService<EvaluationStore>().LoadAsync(seed.CollectionId, Ct)).ShouldNotBeNull();
        target.Questions.Count.ShouldBe(30);
        using var session = worker.Services.GetRequiredService<RetrievalEvaluator>().Open(target);
        await using var api = EvaluationSmokeKit.ApiHost(database, gateway, _blobs);
        using var http = await EvaluationSmokeKit.ClientAsync(api, key);

        var differences = new List<string>();
        foreach (var question in target.Questions)
        {
            var evaluated = (await session.SearchAsync(question, EvaluationVariant.Hybrid, target.Settings, Ct)).Hits.Select(h => h.Candidate.ChunkId).ToList();
            var retrieved = (await EvaluationSmokeKit.RetrieveAsync(http, slug, question.Question)).Select(r => r.ChunkId).ToList();

            evaluated.ShouldNotBeEmpty(question.Question);
            if (!evaluated.SequenceEqual(retrieved))
            {
                differences.Add($"{question.Question}\n  evaluator: {string.Join(' ', evaluated)}\n  api:       {string.Join(' ', retrieved)}");
            }
        }

        differences.Count.ShouldBe(0, $"questions of {target.Questions.Count} that differ; the first: {differences.FirstOrDefault()}");
    }

    /// <summary>
    /// Knowledge design §6.5 in the contract (#241, hand-off L3): a published crawl collection with <c>recency</c> on and a
    /// source <c>demote</c> pattern for its archive section. The archive page matches the question best and ranks first
    /// without demote; with it, the evaluator (every variant, and candidate settings) and <c>POST /v1/retrieve</c> both put
    /// it lower, in the same order. A ten-year-old page carries the age decay.
    /// </summary>
    [Fact]
    public async Task The_evaluator_and_the_retrieve_endpoint_apply_recency_and_source_demote_alike()
    {
        var tag = "zq" + Guid.NewGuid().ToString("N")[..12];
        var seed = await EngineKit.SeedCrawlAsync(database, new
        {
            startUrls = new[] { "https://www.example.org/" },
            demote = new[] { new { pattern = "www.example.org/arkiv/**", factor = 0.5 } },
        }, Classification.Public);
        await EngineKit.PauseAsync(database, seed);
        await EvaluationSmokeKit.PublishAsync(database, seed.CollectionId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Collections" SET "RetrievalSettings" = $2::jsonb WHERE "Id" = $1""",
            seed.CollectionId, """{"coverageMaxDistance":0.85,"recency":{"halfLifeDays":730,"weight":0.15}}""");
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), "SELECT content.rag_ensure_profile_index($1, $2)", EngineKit.ProfileId, EngineKit.Dimensions);

        var question = $"Vad kostar parkeringstillstånd {tag} för boende i centrum?";
        var now = DateTimeOffset.UtcNow;
        FixtureDocument archive, current, old;
        await using (var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin),
            (text, dims) => FakeContent.Vector(text, dims)))
        {
            archive = await AddPageAsync(writer, seed, $"https://www.example.org/arkiv/parkering-{tag}", question, now);
            current = await AddPageAsync(writer, seed, $"https://www.example.org/parkering-{tag}",
                question + " Taxan gäller från januari och betalas varje månad.", now);
            old = await AddPageAsync(writer, seed, $"https://www.example.org/trafik-{tag}",
                question + " Ansökan görs hos trafikavdelningen på stadshuset med blankett.", now.AddYears(-10));
        }

        await AddQuestionAsync(seed, question, [current.Id]);
        var slug = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Slug" FROM config."Collections" WHERE "Id" = $1""", seed.CollectionId);
        var (_, key) = await EvaluationSmokeKit.AddClientAsync(database, ClientKinds.Internal, Classification.Sensitive);

        await using var worker = await StartWorkerAsync();
        var target = (await worker.Services.GetRequiredService<EvaluationStore>().LoadAsync(seed.CollectionId, Ct)).ShouldNotBeNull();
        target.Settings.Recency.ShouldNotBeNull();
        target.Demote.Keys.ShouldBe([seed.SourceId]);
        var row = target.Questions.ShouldHaveSingleItem();
        var evaluator = worker.Services.GetRequiredService<RetrievalEvaluator>();
        await using var api = EvaluationSmokeKit.ApiHost(database, gateway, _blobs);
        using var http = await EvaluationSmokeKit.ClientAsync(api, key);

        var drifted = target with { Profile = target.Profile with { CanaryCheckedAt = DateTimeOffset.UtcNow, CanarySimilarity = 0.5 } };
        Should.Throw<Worker.Processing.StageTransientException>(() => evaluator.Open(drifted)).Code.ShouldBe("embed.canary_mismatch");

        // Without demote the archive page is first: the fixture reorders.
        using (var undemoted = evaluator.Open(target with { Demote = new Dictionary<Guid, IReadOnlyList<DemotePattern>>() }))
        {
            (await undemoted.SearchAsync(row, EvaluationVariant.Hybrid, target.Settings, Ct)).Hits[0].Candidate.DocumentId
                .ShouldBe(archive.Id, "the archive page ranks first without demote");
        }

        using var session = evaluator.Open(target);
        var evaluated = (await session.SearchAsync(row, EvaluationVariant.Hybrid, target.Settings, Ct)).Hits.Select(h => h.Candidate).ToList();
        var retrieved = await EvaluationSmokeKit.RetrieveAsync(http, slug, question);

        evaluated.Select(c => c.ChunkId).ShouldBe(retrieved.Select(r => r.ChunkId), "evaluator and /v1/retrieve: the same chunks in the same order");
        evaluated.Select(c => c.DocumentId).ShouldBe([current.Id, archive.Id, old.Id], ignoreOrder: true);
        retrieved[0].DocumentId.ShouldNotBe(archive.Id, "/v1/retrieve demotes the archive page");
        evaluated[0].DocumentId.ShouldNotBe(archive.Id, "the evaluator demotes the archive page");
        foreach (var (variant, settings) in new[]
                 {
                     (EvaluationVariant.Vector, target.Settings), (EvaluationVariant.HybridNoDecompounder, target.Settings),
                     (EvaluationVariant.Hybrid, target.Settings with { KeywordWeight = 0.9 }),
                 })
        {
            (await session.SearchAsync(row, variant, settings, Ct)).Hits[0].Candidate.DocumentId
                .ShouldNotBe(archive.Id, $"{variant}, keyword weight {settings.KeywordWeight}");
        }
    }

    /// <summary>A live page of the seed's source at <paramref name="url"/>, modified at <paramref name="modified"/>, with one chunk.</summary>
    private static Task<FixtureDocument> AddPageAsync(ContentFixtureWriter writer, SourceSeed seed, string url, string text, DateTimeOffset modified) =>
        writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = seed.CollectionId, EmbeddingProfileId = EngineKit.ProfileId, SourceId = seed.SourceId, Classification = seed.Classification,
            Url = url, Title = url, SourceModifiedAt = modified,
        }, [new FixtureChunk(text) { DocumentType = "page", SourceModifiedAt = modified }], Ct);

    /// <summary>One active <c>human</c> question in a new set of the seed's collection.</summary>
    private async Task AddQuestionAsync(SourceSeed seed, string question, Guid[] expected)
    {
        var setId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null),
            """INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ($1, $2, $3)""", setId, seed.CollectionId, seed.Classification);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
            VALUES ($1, $2, $3, $4, $5, $6, 'active')
            """, Guid.CreateVersion7(), setId, seed.Classification, EvaluationLayers.Human, question, expected);
    }

    /// <summary>The 30 smoke questions as the collection's <c>human</c> layer, in file order (UUIDv7 ids keep it), expecting their documents.</summary>
    private async Task AddQuestionsAsync(SourceSeed seed, SmokeSeed smoke)
    {
        var setId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null),
            """INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ($1, $2, $3)""", setId, seed.CollectionId, seed.Classification);
        foreach (var question in SmokeSet.Questions())
        {
            await Sql.ExecuteAsync(database.Connection(null), """
                INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
                VALUES ($1, $2, $3, $4, $5, $6, 'active')
                """, Guid.CreateVersion7(), setId, seed.Classification, EvaluationLayers.Human, question.Question,
                question.Expected.Select(file => smoke.Documents[file]).ToArray());
        }
    }

    /// <summary>A worker for the evaluator's services (the <c>eval</c> class); the scheduler does not tick during the test.</summary>
    private Task<WorkerHost> StartWorkerAsync() =>
        WorkerHost.StartAsync(database, s => s.AddHttpClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()).Services.UseGatewayCredentialCeiling(),
            new Dictionary<string, string?>
            {
                ["Gateway:BaseUrl"] = "http://fake-gateway",
                ["Gateway:Key"] = FakeGatewayFactory.WorkerCredentialKey,
                ["BlobStore:Root"] = _blobs.FullName,
                ["Worker:Classes"] = JobClasses.Eval,
                ["Worker:SchedulerSeconds"] = "600",
            }, engine: true);
}
