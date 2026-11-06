using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Evaluation;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>
/// The <c>eval</c> class on the test database with the FakeGateway (processing design §13.4–§13.6, §14, §17; P1a item 10,
/// P1b item 7): the three-way <c>on-demand</c> run with per-question coverage and the threshold proposal, the variants
/// telling the decompounder apart on a compound-only question, the evaluator returning what a direct
/// <see cref="RetrievalEngine.SearchAsync"/> with the same request returns, a re-run reproducing every number,
/// candidate settings, <c>publish-report</c> against the last run before the publish, <c>nightly</c> with the recall
/// alert on fake history, the nightly schedule, the kinds left to the set handler, and <c>hybrid</c> beside
/// <c>hybrid-rerank</c> with latency, cost per question and rerank fallbacks (#399).
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class EvaluationRunTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly EvaluationVariant[] ThreeWay = [EvaluationVariant.Vector, EvaluationVariant.Hybrid, EvaluationVariant.HybridNoDecompounder];

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-eval-blobs-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_three_way_run_writes_three_variant_runs_with_coverage_and_a_threshold_proposal_human_first()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);

        var row = await EvalKit.RunJobAsync(database, fixture, new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = ThreeWay },
            JobDedupKeys.Eval(fixture.CollectionId));
        var result = EvalKit.Result<EvalOnDemandResult>(row);

        result.Runs.Select(r => r.Variant).ShouldBe(ThreeWay);
        var runs = new Dictionary<EvaluationVariant, IReadOnlyDictionary<Guid, ResultRow>>();
        foreach (var run in result.Runs)
        {
            run.Candidate.ShouldBeFalse();
            run.QuestionCount.ShouldBe(4, "answerable questions only: three human, one generated");
            run.Found.ShouldBe(3, "the unreachable question is never found");
            run.Layers.Select(l => l.Layer).ShouldBe([EvaluationLayer.Human, EvaluationLayer.Generated], "reports lead with the human layer");
            run.Layers[0].ShouldBe(new EvaluationLayerResult
            {
                Layer = EvaluationLayer.Human, Questions = 3, Found = 2, Unanswerable = 2, RecallAtK = 0.6667, Mrr = run.Layers[0].Mrr,
            });

            var stored = await EvalKit.RunAsync(database, run.EvaluationRunId);
            stored.Kind.ShouldBe(EvaluationRunKinds.OnDemand);
            stored.Variant.ShouldBe(EvaluationRunner.VariantText(run.Variant));
            (stored.QuestionCount, stored.Found, stored.RecallAtK).ShouldBe((4, 3, 0.75m));

            var results = await EvalKit.ResultsAsync(database, run.EvaluationRunId);
            results.Count.ShouldBe(6);
            results.Values.ShouldAllBe(r => r.Coverage != null && r.BestDistance != null, "every question records its coverage");
            results[fixture.Questions.Unreachable].Hit.ShouldBe(false);
            results[fixture.Questions.Unanswerable[0]].Hit.ShouldBeNull("an unanswerable question has no hit");
            runs[run.Variant] = results;

            var proposal = run.Coverage.ShouldNotBeNull();
            (proposal.Answerable, proposal.Unanswerable).ShouldBe((3, 2));
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Candidate"::text || '|' || ("ThresholdProposal" ->> 'answerable') || '|' || ("ThresholdProposal" ->> 'unanswerable')
                FROM content."EvaluationRuns" WHERE "Id" = $1
                """, run.EvaluationRunId)).ShouldBe("false|3|2", "the proposal is kept on the run beyond job retention (#129)");
            var distance = proposal.CoverageMaxDistance.ShouldNotBeNull("answerable and unanswerable human questions give a distance proposal");
            distance.Misclassified.ShouldBeInRange(0, 5);
            if (run.Variant == EvaluationVariant.Vector)
            {
                proposal.CoverageMinKeywordScore.ShouldBeNull("a vector run has no keyword scores");
            }
            else
            {
                proposal.CoverageMinKeywordScore.ShouldNotBeNull();
            }
        }

        // The compound-only question reaches its document's keyword side only through the decompounder (both sides).
        var compound = fixture.Questions.CompoundOnly;
        runs[EvaluationVariant.Hybrid][compound].BestKeywordScore.ShouldNotBeNull("hybrid: 'tillstånd' matches the stored part of 'parkeringstillstånd'");
        runs[EvaluationVariant.HybridNoDecompounder][compound].BestKeywordScore.ShouldBeNull(
            "without the decompounder the scratch copy holds only 'parkeringstillstånd'");
        runs[EvaluationVariant.Vector][compound].BestKeywordScore.ShouldBeNull();
        runs[EvaluationVariant.HybridNoDecompounder][fixture.Questions.Keyword].BestKeywordScore.ShouldNotBeNull(
            "the scratch copy still matches a plain word");
    }

    [Fact]
    public async Task The_evaluator_returns_the_ids_and_order_of_a_direct_engine_search_with_the_same_request()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);
        var target = (await worker.Services.GetRequiredService<EvaluationStore>().LoadAsync(fixture.CollectionId, Ct)).ShouldNotBeNull();
        using var session = worker.Services.GetRequiredService<RetrievalEvaluator>().Open(target);

        // /v1/retrieve's path (issue #120): the api role's data source, the collection's resolved settings, every level.
        var platform = RetrievalSettingsResolver.Platform(
            await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Retrieval"::text FROM config."Settings" ORDER BY "Id" LIMIT 1"""));
        var settings = RetrievalSettingsResolver.Collection(platform,
            await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "RetrievalSettings"::text FROM config."Collections" WHERE "Id" = $1""", fixture.CollectionId));
        var scope = new SearchScope(EngineKit.ProfileId, EngineKit.Dimensions, [new CollectionInScope(fixture.CollectionId, settings)], Classification.All, platform);
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
                evaluated.Coverage.ShouldBe(direct.Coverage);
            }
        }
    }

    [Fact]
    public async Task Hybrid_and_hybrid_rerank_report_recall_latency_cost_per_question_and_fallbacks_side_by_side()
    {
        const string model = "ume/chat-eu"; // Eu in the FakeGateway's catalogue: within an internal collection's ceiling
        var fixture = await EvalKit.SeedAsync(database);
        var restore = await SetRetrievalAsync($$"""{"rerankModel":"{{model}}"}""");
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource", "InputPerMillionUsd", "OutputPerMillionUsd")
            VALUES ($1, 'model', ARRAY['Eu'], 'manual', false, 'manual', 2.5, 10)
            ON CONFLICT ("Name") DO UPDATE SET "InputPerMillionUsd" = 2.5, "OutputPerMillionUsd" = 10
            """, model);
        try
        {
            await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);
            var result = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture,
                new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = [EvaluationVariant.Hybrid, EvaluationVariant.HybridRerank] },
                JobDedupKeys.Eval(fixture.CollectionId)));

            result.Runs.Select(r => r.Variant).ShouldBe([EvaluationVariant.Hybrid, EvaluationVariant.HybridRerank]);
            var (hybrid, reranked) = (result.Runs[0], result.Runs[1]);
            foreach (var run in result.Runs)
            {
                run.RecallAtK.ShouldBe(0.75, "the unreachable question is never found, reranked or not");
                run.Mrr.ShouldNotBeNull();
                var search = run.Search.ShouldNotBeNull();
                search.Searches.ShouldBe(6, "every question, unanswerable ones included");
                search.TotalP95Ms.ShouldBeGreaterThanOrEqualTo(search.TotalP50Ms);
                search.RerankUnavailable.ShouldBe(0);
                (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Variant" || '|' || "Reranked"::text FROM content."EvaluationRuns" WHERE "Id" = $1""",
                    run.EvaluationRunId)).ShouldBe(run == reranked ? "hybrid|true" : "hybrid|false", "hybrid-rerank is stored as hybrid with Reranked set");
            }

            var plain = hybrid.Search!;
            (plain.RerankModel, plain.RerankP50Ms, plain.RerankCostPerQueryUsd, plain.RerankPromptTokens).ShouldBe(((string?)null, (double?)null, (double?)null, 0));

            var rerank = reranked.Search!;
            rerank.RerankModel.ShouldBe(model);
            rerank.RerankP50Ms.ShouldNotBeNull();
            rerank.RerankP95Ms.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(rerank.RerankP50Ms!.Value);
            rerank.RerankPromptTokens.ShouldBeGreaterThan(0, "the FakeGateway reports usage for every rerank call");
            rerank.RerankCompletionTokens.ShouldBeGreaterThan(0);
            rerank.RerankCostPerQueryUsd.ShouldBe(
                (double)Math.Round((rerank.RerankPromptTokens * 2.5m + rerank.RerankCompletionTokens * 10m) / 1_000_000m / 6, 6), "tokens at the mirror's prices, per question");
        }
        finally
        {
            await restore();
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."GatewayModels" WHERE "Name" = $1""", model);
        }
    }

    [Fact]
    public async Task Hybrid_rerank_without_a_rerank_model_counts_every_question_as_rerank_unavailable()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);

        var result = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture,
            new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = [EvaluationVariant.HybridRerank] }, JobDedupKeys.Eval(fixture.CollectionId)));

        var run = result.Runs.ShouldHaveSingleItem();
        run.RecallAtK.ShouldBe(0.75, "the fused order stands");
        var search = run.Search.ShouldNotBeNull();
        search.RerankUnavailable.ShouldBe(6, "Settings.Retrieval.rerankModel is unset: every rerank falls back, and the report says so");
        (search.RerankModel, search.RerankCostPerQueryUsd, search.RerankPromptTokens).ShouldBe(((string?)null, (double?)null, 0));
    }

    [Fact]
    public async Task The_same_set_settings_and_index_give_identical_results_twice()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);

        var payload = new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = ThreeWay };
        var first = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture, payload, JobDedupKeys.Eval(fixture.CollectionId)));
        var second = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture, payload, JobDedupKeys.Eval(fixture.CollectionId)));

        second.Runs.Select(r => r.EvaluationRunId).ShouldNotBe(first.Runs.Select(r => r.EvaluationRunId));
        static string Numbers(EvalOnDemandResult result) => Pipelines.Json.ContractJson.Serialize(result.Runs.Select(r => r with { EvaluationRunId = Guid.Empty, Search = null }).ToList());
        Numbers(second).ShouldBe(Numbers(first), "counts, layers and the proposal reproduce");
        for (var i = 0; i < first.Runs.Count; i++)
        {
            var before = await EvalKit.RunAsync(database, first.Runs[i].EvaluationRunId);
            var after = await EvalKit.RunAsync(database, second.Runs[i].EvaluationRunId);
            after.ShouldBe(before);
            var a = await EvalKit.ResultsAsync(database, first.Runs[i].EvaluationRunId);
            var b = await EvalKit.ResultsAsync(database, second.Runs[i].EvaluationRunId);
            b.OrderBy(r => r.Key).ShouldBe(a.OrderBy(r => r.Key), "every question's rank and coverage reproduce");
        }
    }

    [Fact]
    public async Task Candidate_settings_run_each_variant_once_more_marked_candidate_and_bad_ones_fail()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);

        var candidate = System.Text.Json.JsonDocument.Parse("""{"maxChunksPerDocument":1,"vectorWeight":2.0}""").RootElement;
        var result = EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture,
            new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = [EvaluationVariant.Hybrid, EvaluationVariant.Vector], CandidateSettings = candidate },
            JobDedupKeys.Eval(fixture.CollectionId)));
        result.Runs.Select(r => (r.Variant, r.Candidate)).ShouldBe(
            [(EvaluationVariant.Hybrid, false), (EvaluationVariant.Hybrid, true), (EvaluationVariant.Vector, false), (EvaluationVariant.Vector, true)]);
        foreach (var run in result.Runs)
        {
            (await Sql.ScalarAsync<bool>(database.Connection(null), """SELECT "Candidate" FROM content."EvaluationRuns" WHERE "Id" = $1""", run.EvaluationRunId))
                .ShouldBe(run.Candidate, "the candidate flag is stored on the run (#129)");
        }

        var bad = System.Text.Json.JsonDocument.Parse("""{"maxChunksPerDocument":50}""").RootElement;
        var failed = await EvalKit.RunJobAsync(database, fixture, new EvalOnDemandPayload { CollectionId = fixture.CollectionId, CandidateSettings = bad },
            JobDedupKeys.Eval(fixture.CollectionId), JobStates.Failed);
        failed.LastError.ShouldBe(EvaluationRunner.InvalidCandidateSettings);
    }

    [Fact]
    public async Task A_publish_report_compares_with_the_last_run_before_the_publish()
    {
        var fixture = await EvalKit.SeedAsync(database);
        var publishRun = await CommandSeeds.RunAsync(database, fixture.Seed, RunStates.Succeeded, DateTimeOffset.UtcNow);
        var publishedAt = await Sql.ScalarAsync<DateTime>(database.Connection(null), """SELECT "StartedAt" FROM content."Runs" WHERE "Id" = $1""", publishRun);
        var q = fixture.Questions;
        // Before the publish: the compound question found, the keyword question lost, the unreachable one "found" (now lost).
        var before = await EvalKit.PastRunAsync(database, fixture, EvaluationRunKinds.Nightly, publishedAt.AddHours(-2), 0.5m,
            new Dictionary<Guid, int?> { [q.CompoundOnly] = 1, [q.Keyword] = null, [q.Unreachable] = 2 });
        await EvalKit.PastRunAsync(database, fixture, EvaluationRunKinds.Nightly, publishedAt.AddMinutes(30), 1m,
            new Dictionary<Guid, int?> { [q.CompoundOnly] = 1, [q.Keyword] = 1, [q.Unreachable] = null });
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);

        var row = await EvalKit.RunJobAsync(database, fixture,
            new EvalPublishReportPayload { CollectionId = fixture.CollectionId, RunId = publishRun, PipelineVersionId = fixture.Seed.VersionId },
            JobDedupKeys.EvalReport(publishRun));
        var result = EvalKit.Result<EvalRunResult>(row);

        result.ComparedRunId.ShouldBe(before, "the run after the publish started is not the baseline");
        (result.QuestionCount, result.Found, result.Wins, result.Losses).ShouldBe((4, 3, 1, 1));
        result.RecallAlert.ShouldBeFalse();
        result.Layers[0].Layer.ShouldBe(EvaluationLayer.Human);
        var stored = await EvalKit.RunAsync(database, result.EvaluationRunId);
        stored.ShouldBe(new RunRow(EvaluationRunKinds.PublishReport, EvaluationVariants.Hybrid, 4, 3, 0.75m, stored.Mrr, before, 1, 1, fixture.Seed.VersionId, 1));
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.78, false)]
    public async Task A_nightly_run_raises_the_alert_only_5_points_below_the_4_week_median(double past, bool alert)
    {
        var fixture = await EvalKit.SeedAsync(database);
        await EvalKit.PublishAsync(database, fixture);
        try
        {
            var now = DateTimeOffset.UtcNow;
            Guid latest = default;
            for (var day = 7; day >= 1; day--)
            {
                latest = await EvalKit.PastRunAsync(database, fixture, EvaluationRunKinds.Nightly, now.AddDays(-day), (decimal)past);
            }

            // Outside the 4 weeks: would pull the median down if it counted.
            for (var day = 30; day <= 40; day++)
            {
                await EvalKit.PastRunAsync(database, fixture, EvaluationRunKinds.Nightly, now.AddDays(-day), 0m);
            }

            await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);
            var row = await EvalKit.RunJobAsync(database, fixture, new EvalNightlyPayload { CollectionId = fixture.CollectionId, ScheduledFor = now },
                JobDedupKeys.EvalNightly(fixture.CollectionId));
            var result = EvalKit.Result<EvalRunResult>(row);

            result.RecallAlert.ShouldBe(alert);
            result.ComparedRunId.ShouldBe(latest);
            (await EvalKit.RunAsync(database, result.EvaluationRunId)).ShouldSatisfyAllConditions(
                r => r.Kind.ShouldBe(EvaluationRunKinds.Nightly), r => r.RecallAtK.ShouldBe(0.75m));
        }
        finally
        {
            await EvalKit.PublishAsync(database, fixture, published: false);
        }
    }

    [Fact]
    public async Task The_nightly_schedule_enqueues_one_run_per_published_collection_and_night()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await EvalKit.PublishAsync(database, fixture);
        try
        {
            await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);
            var scheduler = worker.Services.GetRequiredService<EvalScheduler>();
            await scheduler.TickAsync(Ct);
            await scheduler.TickAsync(Ct);

            var key = JobDedupKeys.EvalNightly(fixture.CollectionId);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "DedupKey" = $1""", key)).ShouldBe(1);
            var payload = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Payload"::text FROM content."Jobs" WHERE "DedupKey" = $1""", key);
            var nightly = Pipelines.Json.ContractJson.Deserialize<EvalNightlyPayload>(System.Text.Json.JsonDocument.Parse(payload).RootElement);
            nightly.ScheduledFor.ShouldBe(EvalScheduler.LatestOccurrence(DateTimeOffset.UtcNow)!.Value);
        }
        finally
        {
            await EvalKit.PublishAsync(database, fixture, published: false);
        }
    }

    [Fact]
    public async Task Set_kinds_go_to_the_set_handler_which_refuses_without_a_model()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using (var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs))
        {
            // The engine registers the generator (Evaluation/Generation); with no Settings.EvaluationModel it refuses.
            var failed = await EvalKit.RunJobAsync(database, fixture, new EvalGenerateSetPayload { CollectionId = fixture.CollectionId },
                JobDedupKeys.EvalGenerate(fixture.CollectionId), JobStates.Failed);
            failed.LastError.ShouldBe("eval.model_unset");
        }

        var sets = new RecordingSetHandler();
        await using (var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs, s => s.AddSingleton<IEvalSetHandler>(sets)))
        {
            await EvalKit.RunJobAsync(database, fixture, new EvalTopUpSetPayload { CollectionId = fixture.CollectionId },
                JobDedupKeys.EvalGenerate(fixture.CollectionId));
            sets.Kinds.ShouldBe([EvalKinds.TopUpSet]);
        }
    }

    /// <summary>Sets <c>Settings.Retrieval</c> (creating the row when there is none); the returned callback puts it back.</summary>
    private async Task<Func<Task>> SetRetrievalAsync(string json)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var row = await db.Settings.FirstOrDefaultAsync(Ct);
        var (hadRow, before) = (row is not null, row?.Retrieval ?? "{}");
        if (row is null)
        {
            row = new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow };
            db.Settings.Add(row);
        }

        row.Retrieval = json;
        await db.SaveChangesAsync(Ct);
        return async () =>
        {
            await using var restore = TestPostgres.Context(database.Connection(null));
            var current = await restore.Settings.FirstAsync(Ct);
            if (hadRow)
            {
                current.Retrieval = before;
            }
            else
            {
                restore.Settings.Remove(current);
            }

            await restore.SaveChangesAsync(Ct);
        };
    }

    private sealed class RecordingSetHandler : IEvalSetHandler
    {
        public List<string> Kinds { get; } = [];

        public Task<JobResult> ExecuteAsync(JobContext context, string kind, CancellationToken cancellationToken)
        {
            Kinds.Add(kind);
            return Task.FromResult<JobResult>(new JobResult.Success());
        }
    }
}
