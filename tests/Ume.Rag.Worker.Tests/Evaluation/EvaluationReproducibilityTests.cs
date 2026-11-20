using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Evaluation;
using Ume.Rag.Worker.Evaluation.Generation;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>
/// Processing design §17 "Evaluation reproducibility", on the test database with the FakeGateway (deterministic embeddings
/// and scripted model answers), every job run by a worker from its row: the same seed gives the same sample, the same
/// questions and the same metrics; a re-run gives identical results; the evaluator returns the ids and order of a direct
/// <see cref="RetrievalEngine.SearchAsync"/> (the <c>/v1/retrieve</c> path); win and loss counts against the previous run.
/// The design places it in <c>Ume.Rag.IntegrationTests</c> (stream B's); it lives here with the other evaluation tests.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class EvaluationReproducibilityTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Model = "ume/chat-onprem";
    private const int Documents = 3;
    private const int ChunksPerDocument = 4;

    private static readonly EvaluationVariant[] ThreeWay = [EvaluationVariant.Vector, EvaluationVariant.Hybrid, EvaluationVariant.HybridNoDecompounder];

    /// <summary>Nine Swedish questions (one per sampled chunk) that pass the filters and are not near-duplicates; {0} is the topic, {1} the test's tag.</summary>
    private static readonly string[] Questions =
    [
        "Vilken nämnd beslutar om {0} och hur lång väntan blir det för {1}?",
        "Kan man överklaga ett beslut om {0} om man inte håller med {1}?",
        "Var vänder jag mig med frågor om {0} på kvällar och helger hos {1}?",
        "Hur mycket betalar en familj per månad för {0} hos {1} under året?",
        "Måste jag boka tid innan jag besöker {0} med barnen hos {1}?",
        "Vem har ansvar för {0} när personalen är sjuk hos {1}?",
        "Går det att söka {0} digitalt med mobilt bankid hos {1}?",
        "Vilka papper behöver jag lämna när jag söker {0} hos {1}?",
        "Hur ansöker man om {0} och vilka avgifter gäller för {1} i år?",
    ];

    private static readonly string[] Topics =
        ["förskola", "bygglov", "snöröjning", "bibliotek", "parkering", "avfall", "färdtjänst", "simhall", "hemtjänst", "vattenmätare", "skolskjuts", "fritidsgård"];

    private readonly EvalGatewayFactory _gateway = new();
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-eval-repro-");
    private PlatformSettings? _previous;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _previous = await EvalSeeds.SetModelsAsync(database, Model);
    }

    public async ValueTask DisposeAsync()
    {
        await EvalSeeds.RestoreModelsAsync(database, _previous is not null, _previous);
        _blobs.Delete(recursive: true);
        await _gateway.DisposeAsync();
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_sample_the_same_questions_and_the_same_metrics()
    {
        var corpus = await SeedAsync();
        await using var worker = await StartWorkerAsync();

        var setId = await GenerateAsync(corpus);
        var first = await EvalSeeds.QuestionsAsync(database, setId);

        // The sample is the seeded ranking's first N chunks: N = min(100, 3 × 3 documents, 12 eligible) = 9 of 12, so the
        // seed decides which three chunks are left out.
        var eligible = await worker.Services.GetRequiredService<EvalSetStore>().EligibleAsync(corpus.Seed.CollectionId, EvalSetGenerator.MinBodyTokens, Ct);
        eligible.Count.ShouldBe(Documents * ChunksPerDocument);
        var target = ChunkSampler.Target(EvalSettings.DefaultMaxQuestions, Documents, eligible.Count);
        target.ShouldBe(9);
        var ranking = ChunkSampler.Rank(eligible, ChunkSampler.Seed(setId, 1), target, Documents, ChunkSampler.Candidates(target));
        first.Select(q => q.SourceChunkId).Order().ShouldBe(ranking.Take(target).Select(c => c.ChunkId).Order());
        first.ShouldAllBe(q => q.Origin["seed"]!.GetValue<string>() == Convert.ToHexStringLower(ChunkSampler.Seed(setId, 1)));
        var firstRun = await OnDemandAsync(corpus);

        // Round 1 of the same set again: the same seed.
        await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."EvaluationQuestions" WHERE "SetId" = $1 AND "Layer" = 'generated'""", setId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."EvaluationSets" SET "Round" = 1 WHERE "Id" = $1""", setId);
        (await GenerateAsync(corpus)).ShouldBe(setId);
        var second = await EvalSeeds.QuestionsAsync(database, setId);

        second.Select(q => q.Id).ShouldNotBe(first.Select(q => q.Id));
        Projection(second).ShouldBe(Projection(first), "same chunks, questions, answers, expected documents and origin (seed, round, judge scores)");
        var secondRun = await OnDemandAsync(corpus);
        Numbers(secondRun).ShouldBe(Numbers(firstRun), "counts, recall, MRR, layers and the proposal");
        for (var i = 0; i < ThreeWay.Length; i++)
        {
            var before = await ResultsByQuestionAsync(firstRun.Runs[i].EvaluationRunId, first);
            var after = await ResultsByQuestionAsync(secondRun.Runs[i].EvaluationRunId, second);
            after.ShouldBe(before, $"{ThreeWay[i]}: every question's rank and coverage");
        }
    }

    [Fact]
    public async Task A_re_run_gives_identical_results_and_the_evaluator_returns_the_engine_order()
    {
        var corpus = await SeedAsync();
        await using var worker = await StartWorkerAsync();
        var setId = await GenerateAsync(corpus);
        var questions = await EvalSeeds.QuestionsAsync(database, setId);
        questions.Count.ShouldBe(9);

        var first = await OnDemandAsync(corpus);
        var second = await OnDemandAsync(corpus);
        Numbers(second).ShouldBe(Numbers(first));
        for (var i = 0; i < ThreeWay.Length; i++)
        {
            (await EvalKit.RunAsync(database, second.Runs[i].EvaluationRunId)).ShouldBe(await EvalKit.RunAsync(database, first.Runs[i].EvaluationRunId));
            var a = await EvalKit.ResultsAsync(database, first.Runs[i].EvaluationRunId);
            var b = await EvalKit.ResultsAsync(database, second.Runs[i].EvaluationRunId);
            b.OrderBy(r => r.Key).ShouldBe(a.OrderBy(r => r.Key), $"{ThreeWay[i]}: every question's rank and coverage");
        }

        // /v1/retrieve's path (as in EvaluationRunTests): the api role's data source, the collection's resolved settings.
        var target = (await worker.Services.GetRequiredService<EvaluationStore>().LoadAsync(corpus.Seed.CollectionId, Ct)).ShouldNotBeNull();
        target.Questions.Count.ShouldBe(9);
        using var session = worker.Services.GetRequiredService<RetrievalEvaluator>().Open(target);
        var platform = RetrievalSettingsResolver.Platform(
            await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Retrieval"::text FROM config."Settings" ORDER BY "Id" LIMIT 1"""));
        var settings = RetrievalSettingsResolver.Collection(platform, await Sql.ScalarAsync<string>(database.Connection(null),
            """SELECT "RetrievalSettings"::text FROM config."Collections" WHERE "Id" = $1""", corpus.Seed.CollectionId));
        var scope = new SearchScope(EngineKit.ProfileId, EngineKit.Dimensions, [new CollectionInScope(corpus.Seed.CollectionId, settings)], Classification.All,
            platform);
        await using var api = RetrievalDataSource.Create(database.Connection(RagDatabase.Api));
        var index = new PostgresSearchIndex(api);
        using var sizes = new ScopeSizeCache(index, TimeProvider.System);
        using var engine = new RetrievalEngine(index, sizes);
        QueryEmbedder embed = (query, _) => Task.FromResult<ReadOnlyMemory<float>>(EvalKit.Embed(query, EngineKit.Dimensions));
        foreach (var question in target.Questions)
        {
            foreach (var (variant, mode) in new[] { (EvaluationVariant.Hybrid, RetrievalMode.Hybrid), (EvaluationVariant.Vector, RetrievalMode.Vector) })
            {
                var direct = await engine.SearchAsync(new SearchRequest(question.Question, scope, 10, mode, Embed: embed, AllowDegraded: false), Ct);
                var evaluated = await session.SearchAsync(question, variant, target.Settings, Ct);

                direct.Hits.ShouldNotBeEmpty();
                evaluated.Hits.Select(h => h.Candidate.ChunkId).ShouldBe(direct.Hits.Select(h => h.Candidate.ChunkId), $"{variant}: {question.Question}");
                evaluated.Hits.Select(h => h.Score).ShouldBe(direct.Hits.Select(h => h.Score));
            }
        }
    }

    [Fact]
    public async Task Win_and_loss_counts_are_taken_against_the_previous_run()
    {
        var corpus = await SeedAsync();
        await using var worker = await StartWorkerAsync();
        var setId = await GenerateAsync(corpus);
        var willWin = await HumanQuestionAsync(corpus, setId, $"Var kan man låna böcker och tidskrifter hos {corpus.Tag}?", await UnindexedDocumentAsync(corpus));
        var willLose = await HumanQuestionAsync(corpus, setId, $"Hur går man till väga för att få bygglov hos {corpus.Tag}?", corpus.DocumentIds[1]);
        await EvalKit.PublishAsync(database, corpus.Seed.CollectionId);
        try
        {
            var a = await NightlyAsync(corpus);
            a.ComparedRunId.ShouldBeNull("the set's first comparable run");
            (await EvalKit.RunAsync(database, a.EvaluationRunId)).ShouldSatisfyAllConditions(r => r.Wins.ShouldBeNull(), r => r.Losses.ShouldBeNull());

            // Nothing changed: no wins, no losses, the same numbers.
            var b = await NightlyAsync(corpus);
            b.ComparedRunId.ShouldBe(a.EvaluationRunId);
            (b.Wins, b.Losses).ShouldBe((0, 0));
            Numbers(b).ShouldBe(Numbers(a with { ComparedRunId = a.EvaluationRunId }));
            var resultsB = await EvalKit.ResultsAsync(database, b.EvaluationRunId);
            resultsB.OrderBy(r => r.Key).ShouldBe((await EvalKit.ResultsAsync(database, a.EvaluationRunId)).OrderBy(r => r.Key));
            resultsB[willWin].Hit.ShouldBe(false, "its expected document is not in the index");
            resultsB[willLose].Hit.ShouldBe(true);

            // An editor fixes one question's expected document and breaks another's (edit-eval-question).
            await ExpectAsync(willWin, corpus.DocumentIds[0]);
            await ExpectAsync(willLose, await UnindexedDocumentAsync(corpus));
            var c = await NightlyAsync(corpus);

            c.ComparedRunId.ShouldBe(b.EvaluationRunId);
            (c.Wins, c.Losses).ShouldBe((1, 1));
            var resultsC = await EvalKit.ResultsAsync(database, c.EvaluationRunId);
            var wins = resultsC.Where(r => r.Value.Hit == true && resultsB[r.Key].Hit == false).Select(r => r.Key).ToList();
            var losses = resultsC.Where(r => r.Value.Hit == false && resultsB[r.Key].Hit == true).Select(r => r.Key).ToList();
            wins.ShouldBe([willWin]);
            losses.ShouldBe([willLose]);
            var stored = await EvalKit.RunAsync(database, c.EvaluationRunId);
            (stored.ComparedRunId, stored.Wins, stored.Losses).ShouldBe((b.EvaluationRunId, 1, 1));
            c.Found.ShouldBe(b.Found, "one won, one lost");
        }
        finally
        {
            await EvalKit.PublishAsync(database, corpus.Seed.CollectionId, published: false);
        }
    }

    /// <summary>Three documents of four ~90-token chunks with real (fake-hashed) vectors, and nine good questions scripted for generation.</summary>
    private async Task<Corpus> SeedAsync()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        await EngineKit.PauseAsync(database, seed);
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), "SELECT content.rag_ensure_profile_index($1, $2)", EngineKit.ProfileId, EngineKit.Dimensions);
        var tag = EvalSeeds.Tag();
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin), EvalKit.Embed);
        var documents = new List<Guid>();
        for (var d = 0; d < Documents; d++)
        {
            var chunks = new List<FixtureChunk>();
            for (var c = 0; c < ChunksPerDocument; c++)
            {
                var i = (d * ChunksPerDocument) + c;
                var text = EvalSeeds.Body(Topics[i], Guid.NewGuid().ToString());
                chunks.Add(new FixtureChunk(text) { HeadingPath = "Avsnitt " + Topics[i] });
            }

            documents.Add((await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = seed.CollectionId,
                EmbeddingProfileId = EngineKit.ProfileId,
                SourceId = seed.SourceId,
                Classification = seed.Classification,
                Url = $"https://site.test/repro/{d}/{tag}",
                Title = $"Dokument {d} {tag}",
            }, chunks, Ct)).Id);
        }

        // A generation asks about the sample's nine chunks in order and keeps every answer, so each one plays this array from
        // its start: the same sample gets the same questions.
        _gateway.Script(EvalModelClient.GenerateStep, [.. Questions.Select((q, i) => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["fraga"] = string.Format(CultureInfo.InvariantCulture, q, Topics[i], tag), ["svar"] = $"Svar nummer {i} {tag}.", ["typ"] = "fakta",
        }))]);
        return new Corpus(seed, documents, tag);
    }

    /// <summary>A worker running the <c>eval</c> class (generation and runs) on the scripted gateway; the scheduler does not tick during the test.</summary>
    private Task<WorkerHost> StartWorkerAsync() =>
        WorkerHost.StartAsync(database, s =>
        {
            s.UseGatewayCredentialCeiling();
            s.AddHttpClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _gateway.Server.CreateHandler());
            s.AddHttpClient(EvalModelClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _gateway.Server.CreateHandler());
            s.AddHttpClient(EvalModelClient.CatalogueClientName).ConfigurePrimaryHttpMessageHandler(() => _gateway.Server.CreateHandler());
        }, new Dictionary<string, string?>
        {
            ["Gateway:BaseUrl"] = "http://fake-gateway",
            ["Gateway:Key"] = FakeGatewayFactory.WorkerCredentialKey,
            ["BlobStore:Root"] = _blobs.FullName,
            ["Worker:Classes"] = JobClasses.Eval,
            ["Worker:SchedulerSeconds"] = "600",
        }, engine: true);

    /// <summary>A <c>generate-set</c> job; returns the set id after checking it wrote round 1 with nine kept questions.</summary>
    private async Task<Guid> GenerateAsync(Corpus corpus)
    {
        var row = await EvalKit.RunJobAsync(database, corpus.Seed, new EvalGenerateSetPayload { CollectionId = corpus.Seed.CollectionId },
            JobDedupKeys.EvalGenerate(corpus.Seed.CollectionId));
        var result = EvalKit.Result<EvalSetResult>(row);
        (result.Round, result.Kept).ShouldBe((1, 9), $"generated {result.Generated}");
        return result.SetId;
    }

    private async Task<EvalOnDemandResult> OnDemandAsync(Corpus corpus) =>
        EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, corpus.Seed,
            new EvalOnDemandPayload { CollectionId = corpus.Seed.CollectionId, Variants = ThreeWay }, JobDedupKeys.Eval(corpus.Seed.CollectionId)));

    private async Task<EvalRunResult> NightlyAsync(Corpus corpus) =>
        EvalKit.Result<EvalRunResult>(await EvalKit.RunJobAsync(database, corpus.Seed,
            new EvalNightlyPayload { CollectionId = corpus.Seed.CollectionId, ScheduledFor = DateTimeOffset.UtcNow }, JobDedupKeys.EvalNightly(corpus.Seed.CollectionId)));

    private async Task<Guid> HumanQuestionAsync(Corpus corpus, Guid setId, string question, Guid expected)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
            VALUES ($1, $2, $3, 'human', $4, $5, 'active')
            """, id, setId, corpus.Seed.Classification, question, new[] { expected });
        return id;
    }

    /// <summary>
    /// A document of the corpus's source without a live set: an expected document that is never found but not gone (a deleted
    /// or purged one leaves the question out of the run, processing design §13.3).
    /// </summary>
    private async Task<Guid> UnindexedDocumentAsync(Corpus corpus)
    {
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin), EvalKit.Embed);
        return (await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = corpus.Seed.CollectionId,
            EmbeddingProfileId = EngineKit.ProfileId,
            SourceId = corpus.Seed.SourceId,
            Classification = corpus.Seed.Classification,
            Url = $"https://site.test/repro/unindexed/{Guid.NewGuid():N}",
            SetState = "writing",
        }, [new FixtureChunk($"Ett dokument som ännu inte är sökbart hos {corpus.Tag}.")], Ct)).Id;
    }

    private Task ExpectAsync(Guid questionId, Guid document) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE content."EvaluationQuestions" SET "ExpectedDocumentIds" = $2 WHERE "Id" = $1""", questionId,
            new[] { document });

    /// <summary>The run's results by question text (question ids differ between two generations).</summary>
    private async Task<List<KeyValuePair<string, ResultRow>>> ResultsByQuestionAsync(Guid runId, IReadOnlyList<StoredQuestion> questions)
    {
        var text = questions.ToDictionary(q => q.Id, q => q.Question);
        var results = await EvalKit.ResultsAsync(database, runId);
        return [.. results.Where(r => text.ContainsKey(r.Key)).Select(r => KeyValuePair.Create(text[r.Key], r.Value)).OrderBy(r => r.Key, StringComparer.Ordinal)];
    }

    private static List<string> Projection(IReadOnlyList<StoredQuestion> questions) =>
        [.. questions.OrderBy(q => q.SourceChunkId).Select(q =>
            $"{q.SourceChunkId}|{q.Question}|{q.ExpectedAnswer}|{string.Join(',', q.ExpectedDocumentIds)}|{Convert.ToHexStringLower(q.SourceContentHash)}|{q.State}|{q.Origin.ToJsonString()}")];

    // Latency is measured, not reproduced (#399): the search stats are left out.
    private static string Numbers(EvalOnDemandResult result) =>
        ContractJson.Serialize(result.Runs.Select(r => r with { EvaluationRunId = Guid.Empty, Search = null }).ToList());

    private static string Numbers(EvalRunResult result) => ContractJson.Serialize(result with { EvaluationRunId = Guid.Empty, Search = null });

    private sealed record Corpus(SourceSeed Seed, IReadOnlyList<Guid> DocumentIds, string Tag);
}
