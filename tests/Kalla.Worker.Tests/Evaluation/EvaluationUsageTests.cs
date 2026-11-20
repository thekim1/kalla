using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Jobs;
using Kalla.Retrieval;
using Kalla.TestKit;
using Kalla.Worker.Evaluation;
using Kalla.Worker.Evaluation.Generation;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Evaluation;

/// <summary>
/// A-31's done-when for evaluation (engine design §7.7, §10.6; processing design §13.1, §14.7), every job run by a worker
/// from its row against the FakeGateway: a set generation adds <c>eval-generate</c>, <c>eval-judge</c> and <c>eval-dedup</c>
/// rows with the gateway's tokens and cost, each call carrying the job's id; on <c>fake-no-cost</c> the cost is the estimate
/// from the mirror's prices, kept apart; an attempt put back by a refusal counts the calls it made once; a retrieval run adds
/// <c>query-embed</c> (cache misses only) and <c>rerank</c> rows, each call carrying its evaluation run's id.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class EvaluationUsageTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Model = "fors/chat-onprem";
    private const string NoCostModel = "fake-no-cost";

    /// <summary>Six Swedish questions with little wording in common; {0} is the topic, {1} the test's tag.</summary>
    private static readonly string[] Good =
    [
        "Vilken nämnd beslutar om {0} och hur lång väntan blir det för {1}?",
        "Kan man överklaga ett beslut om {0} om man inte håller med {1}?",
        "Var vänder jag mig med frågor om {0} på kvällar och helger hos {1}?",
        "Hur mycket betalar en familj per månad för {0} hos {1} under året?",
        "Måste jag boka tid innan jag besöker {0} med barnen hos {1}?",
        "Vem har ansvar för {0} när personalen är sjuk hos {1}?",
    ];

    private static readonly string[] Topics = ["förskola", "bygglov", "snöröjning", "bibliotek", "parkering", "avfall"];

    private static readonly string[] SetSteps = [EvalModelClient.GenerateStep, EvalModelClient.JudgeStep, EvalSetGenerator.DedupStep];

    private readonly EvalGatewayFactory _gateway = new();
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-eval-usage-");
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
    public async Task A_set_generation_adds_its_generate_judge_and_dedup_calls_with_the_gateways_cost_each_carrying_the_jobs_id()
    {
        var seed = await SeedAsync();
        var recorder = new GatewayCallRecorder();
        await using var worker = await StartWorkerAsync(recorder);

        var result = await GenerateAsync(seed);

        result.Kept.ShouldBeGreaterThan(0);
        var calls = recorder.Answered;
        calls.Select(c => c.Step).Distinct().Order().ShouldBe(SetSteps.Order());
        await ShouldCarryTheJobsIdAsync(calls);
        var usage = await EngineKit.DailyUsageAsync(database, seed.CollectionId);
        usage.Keys.Order().ShouldBe(SetSteps.Order());
        foreach (var step in SetSteps)
        {
            var own = calls.Where(c => c.Step == step).ToList();
            own.ShouldAllBe(c => c.CostUsd != null, "the FakeGateway prices every answer");
            var row = usage[step];
            (row.Calls, row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.CostUsd, row.EstimatedCalls, row.EstimatedCostUsd).ShouldBe(
                (own.Count, own.Sum(c => c.InputTokens), own.Sum(c => c.CachedInputTokens), own.Sum(c => c.OutputTokens), own.Sum(c => c.CostUsd!.Value), 0, 0m),
                step);
            row.Classifications.ShouldBe([seed.Classification]);
            row.Departments.ShouldBe([seed.DepartmentId]);
        }

        usage[EvalModelClient.GenerateStep].Calls.ShouldBe(result.Generated);
        usage[EvalModelClient.GenerateStep].OutputTokens.ShouldBeGreaterThan(0, "a chat answer has output tokens");
        TestGatewayKeys.Served(_gateway, Guid.Parse(calls[0].RunId!)).Select(r => (r.KeyId, r.ViaKeyId)).Distinct()
            .ShouldBe([(TestGatewayKeys.WorkerCredentialId, (Guid?)null)], "a collection without a key runs as the credential key itself");
    }

    /// <summary>A-34 (#580; engine design §7.7): every model call of a set generation runs as the collection's gateway key.</summary>
    [Fact]
    public async Task A_set_generations_generate_judge_and_dedup_calls_carry_the_collections_key()
    {
        var seed = await SeedAsync();
        await TestGatewayKeys.AssignAsync(database, seed.CollectionId, TestGatewayKeys.KnowledgeBase);
        var recorder = new GatewayCallRecorder();
        await using var worker = await StartWorkerAsync(recorder);

        var result = await GenerateAsync(seed);

        result.Kept.ShouldBeGreaterThan(0);
        var calls = recorder.Answered;
        await ShouldCarryTheJobsIdAsync(calls);
        var served = TestGatewayKeys.Served(_gateway, Guid.Parse(calls[0].RunId!));
        served.Count.ShouldBe(calls.Count, "the fake served every answered call of the job");
        served.Select(r => r.Step).Distinct().Order().ShouldBe(SetSteps.Order());
        served.Select(r => (r.KeyId, r.ViaKeyId)).Distinct().ShouldBe([(TestGatewayKeys.KnowledgeBase, TestGatewayKeys.WorkerCredentialId)],
            "every call runs as the collection's key, through the worker's credential key");
    }

    [Fact]
    public async Task On_a_model_without_a_gateway_cost_the_calls_are_estimated_from_the_mirrors_prices_and_kept_apart()
    {
        // fake-no-cost answers without x-fors-cost-usd; the mirror prices it (per million: 0.15 in, 0.075 cached, 0.60 out).
        await EvalSeeds.SetModelsAsync(database, NoCostModel);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource", "InputPerMillionUsd",
                                                "CachedInputPerMillionUsd", "OutputPerMillionUsd")
            VALUES ($1, 'model', ARRAY['OnPrem'], 'manual', false, 'manual', 0.15, 0.075, 0.60)
            ON CONFLICT ("Name") DO UPDATE SET "InputPerMillionUsd" = 0.15, "CachedInputPerMillionUsd" = 0.075, "OutputPerMillionUsd" = 0.60
            """, NoCostModel);
        try
        {
            var seed = await SeedAsync();
            var recorder = new GatewayCallRecorder();
            await using var worker = await StartWorkerAsync(recorder);

            await GenerateAsync(seed);

            var calls = recorder.Answered;
            var usage = await EngineKit.DailyUsageAsync(database, seed.CollectionId);
            foreach (var step in new[] { EvalModelClient.GenerateStep, EvalModelClient.JudgeStep })
            {
                var own = calls.Where(c => c.Step == step).ToList();
                own.ShouldNotBeEmpty(step);
                own.ShouldAllBe(c => c.CostUsd == null, "fake-no-cost sends no cost");
                var estimate = own.Sum(c => Math.Round(((c.InputTokens - c.CachedInputTokens) * 0.15m + c.CachedInputTokens * 0.075m + c.OutputTokens * 0.60m) / 1_000_000m,
                    8, MidpointRounding.AwayFromZero));
                estimate.ShouldBeGreaterThan(0m);
                var row = usage[step];
                (row.Calls, row.EstimatedCalls, row.CostUsd, row.EstimatedCostUsd, row.InputTokens, row.OutputTokens)
                    .ShouldBe((own.Count, own.Count, 0m, estimate, own.Sum(c => c.InputTokens), own.Sum(c => c.OutputTokens)), step);
            }

            var dedup = usage[EvalSetGenerator.DedupStep];
            (dedup.EstimatedCalls, dedup.EstimatedCostUsd).ShouldBe((0, 0m), "the embedding model's answers carry the gateway's cost");
            dedup.CostUsd.ShouldBe(calls.Where(c => c.Step == EvalSetGenerator.DedupStep).Sum(c => c.CostUsd!.Value));
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."GatewayModels" WHERE "Name" = $1""", NoCostModel);
        }
    }

    /// <summary>
    /// The first judge call is refused with 503 and <c>Retry-After</c>: the attempt is put back without counting it, after one
    /// answered generate call. That call is written with the release, once; the next attempt counts only its own calls.
    /// </summary>
    [Fact]
    public async Task An_attempt_put_back_by_a_refusal_counts_the_calls_it_made_once()
    {
        var seed = await SeedAsync();
        var recorder = new GatewayCallRecorder { RefuseFirst = EvalModelClient.JudgeStep };
        await using var worker = await StartWorkerAsync(recorder);

        var result = await GenerateAsync(seed);

        recorder.Calls.Count(c => c.Status == 503).ShouldBe(1);
        var calls = recorder.Answered;
        calls.Count(c => c.Step == EvalModelClient.GenerateStep).ShouldBe(result.Generated + 1, "the put-back attempt's one generate call, then the round's");
        var usage = await EngineKit.DailyUsageAsync(database, seed.CollectionId);
        foreach (var step in SetSteps.Where(s => calls.Any(c => c.Step == s)))
        {
            var own = calls.Where(c => c.Step == step).ToList();
            (usage[step].Calls, usage[step].InputTokens, usage[step].CostUsd).ShouldBe((own.Count, own.Sum(c => c.InputTokens), own.Sum(c => c.CostUsd!.Value)),
                $"{step}: every answered call once, the refused one not at all");
        }

        await ShouldCarryTheJobsIdAsync(calls);
    }

    [Fact]
    public async Task A_retrieval_run_adds_its_query_embeddings_on_cache_misses_only_and_its_rerank_calls_under_the_runs_id()
    {
        const string rerankModel = "fors/chat-eu"; // Eu in the FakeGateway's catalogue: within an internal collection's ceiling
        await using var gateway = new FakeGatewayFactory();
        var fixture = await EvalKit.SeedAsync(database);
        // The fixture's questions may be in the embedding cache from an earlier test; this one is new.
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
            VALUES ($1, $2, $3, 'human', $4, $5, 'active')
            """, Guid.CreateVersion7(), fixture.SetId, fixture.Seed.Classification, $"Hur sorteras avfallet hos {EvalSeeds.Tag()}?", new[] { fixture.Waste });
        var restore = await SetRerankModelAsync(rerankModel);
        try
        {
            var recorder = new GatewayCallRecorder();
            await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs, s =>
            {
                s.UseGatewayCredentialCeiling();
                s.AddHttpClient(EmbeddingClient.HttpClientName).AddHttpMessageHandler(recorder.Handler);
                s.AddHttpClient(ChatModelReranker.HttpClientName).AddHttpMessageHandler(recorder.Handler);
            });
            var payload = new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = [EvaluationVariant.Hybrid, EvaluationVariant.HybridRerank] };

            var first = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture, payload, JobDedupKeys.Eval(fixture.CollectionId)));

            var (hybrid, reranked) = (first.Runs[0].EvaluationRunId.ToString(), first.Runs[1].EvaluationRunId.ToString());
            var calls = recorder.Answered;
            var embeddings = calls.Where(c => c.Step == EvaluationQueryEmbedder.Step).ToList();
            var reranks = calls.Where(c => c.Step == ChatModelReranker.Step).ToList();
            embeddings.Count.ShouldBeInRange(1, 7, "at most one per question, by the first run (the reranked run reuses the vectors); the new one misses the cache");
            embeddings.ShouldAllBe(c => c.RunId == hybrid && c.CostUsd > 0);
            reranks.ShouldNotBeEmpty();
            reranks.Count.ShouldBeLessThanOrEqualTo(7, "at most one per question of the reranked run");
            reranks.ShouldAllBe(c => c.RunId == reranked && c.CostUsd > 0 && c.OutputTokens > 0);
            calls.Count.ShouldBe(embeddings.Count + reranks.Count);
            var usage = await EngineKit.DailyUsageAsync(database, fixture.CollectionId);
            usage.Keys.Order().ShouldBe(new[] { EvaluationQueryEmbedder.Step, ChatModelReranker.Step }.Order());
            ShouldMatch(usage[EvaluationQueryEmbedder.Step], embeddings);
            ShouldMatch(usage[ChatModelReranker.Step], reranks);

            // The same run again: every question is in the embedding cache, so only the rerank calls are made and counted.
            var second = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture, payload, JobDedupKeys.Eval(fixture.CollectionId)));

            var again = recorder.Answered.Skip(calls.Count).ToList();
            again.ShouldAllBe(c => c.Step == ChatModelReranker.Step && c.RunId == second.Runs[1].EvaluationRunId.ToString());
            usage = await EngineKit.DailyUsageAsync(database, fixture.CollectionId);
            ShouldMatch(usage[EvaluationQueryEmbedder.Step], embeddings);
            ShouldMatch(usage[ChatModelReranker.Step], [.. reranks, .. again]);
        }
        finally
        {
            await restore();
        }
    }

    private static void ShouldMatch(DailyUsage row, IEnumerable<RecordedCall> calls) =>
        (row.Calls, row.InputTokens, row.OutputTokens, row.CostUsd, row.EstimatedCalls).ShouldBe(
            (calls.Count(), calls.Sum(c => c.InputTokens), calls.Sum(c => c.OutputTokens), calls.Sum(c => c.CostUsd!.Value), 0));

    /// <summary>Every call carried one run id, the id of the set generation's <c>eval</c> job (engine design §7.7).</summary>
    private async Task ShouldCarryTheJobsIdAsync(IReadOnlyList<RecordedCall> calls)
    {
        var runId = calls.Select(c => c.RunId).Distinct().ShouldHaveSingleItem().ShouldNotBeNull();
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "Id" = $1 AND "Class" = 'eval'""",
            Guid.Parse(runId))).ShouldBe(1L);
    }

    /// <summary>Three live documents of two ~90-token chunks each, and six good questions scripted for generation.</summary>
    private async Task<SourceSeed> SeedAsync()
    {
        var tag = EvalSeeds.Tag();
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        await EngineKit.PauseAsync(database, seed);
        for (var d = 0; d < 3; d++)
        {
            await EvalSeeds.DocumentAsync(database, seed, $"Dokument {d} {tag}",
                [new(EvalSeeds.Body(Topics[2 * d], Guid.NewGuid().ToString())), new(EvalSeeds.Body(Topics[(2 * d) + 1], Guid.NewGuid().ToString()))]);
        }

        _gateway.Script(EvalModelClient.GenerateStep, [.. Good.Select((q, i) => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["fraga"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, q, Topics[i], tag), ["svar"] = $"Svar nummer {i} {tag}.", ["typ"] = "fakta",
        }))]);
        return seed;
    }

    /// <summary>
    /// A worker running the <c>eval</c> class on the scripted gateway, with every gateway call recorded; it reads its context from
    /// the fake, which acts as keys, before the first job.
    /// </summary>
    private async Task<WorkerHost> StartWorkerAsync(GatewayCallRecorder recorder)
    {
        var worker = await WorkerHost.StartAsync(database, s =>
        {
            foreach (var client in new[] { EmbeddingClient.HttpClientName, EvalModelClient.HttpClientName, EvalModelClient.CatalogueClientName })
            {
                s.UseGatewayCredentialCeiling();
                s.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(() => _gateway.Server.CreateHandler()).AddHttpMessageHandler(recorder.Handler);
            }

            TestGatewayKeys.ReadContextFrom(s, _gateway);
        }, new Dictionary<string, string?>
        {
            ["Gateway:BaseUrl"] = "http://fake-gateway",
            ["Gateway:Key"] = FakeGatewayFactory.WorkerCredentialKey,
            ["BlobStore:Root"] = _blobs.FullName,
            ["Worker:Classes"] = JobClasses.Eval,
            ["Worker:SchedulerSeconds"] = "600",
        }, engine: true);
        await TestGatewayKeys.ActAsKeysAsync(worker.Services);
        return worker;
    }

    private async Task<EvalSetResult> GenerateAsync(SourceSeed seed) =>
        EvalKit.Result<EvalSetResult>(await EvalKit.RunJobAsync(database, seed, new EvalGenerateSetPayload { CollectionId = seed.CollectionId },
            JobDedupKeys.EvalGenerate(seed.CollectionId)));

    /// <summary>Sets the platform's chat rerank model; returns how to put the retrieval settings back.</summary>
    private async Task<Func<Task>> SetRerankModelAsync(string model)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var row = await db.Settings.FirstAsync(Ct);
        var before = row.Retrieval;
        row.Retrieval = JsonSerializer.Serialize(new { rerankModel = model });
        await db.SaveChangesAsync(Ct);
        return async () =>
        {
            await using var restore = TestPostgres.Context(database.Connection(null));
            var current = await restore.Settings.FirstAsync(Ct);
            current.Retrieval = before;
            await restore.SaveChangesAsync(Ct);
        };
    }
}
