using System.Text;
using System.Text.Json;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>
/// The P1b job contracts (P1b README §3 item 2, hand-off L1): preview, evaluation and command kinds, the sync additions,
/// and that they are additive to J3: what P1a wrote still validates and binds.
/// </summary>
public sealed class P1bJobContractTests
{
    private const string G = "0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a";
    private static readonly Guid Id = Guid.Parse(G);

    private static IReadOnlyList<SchemaError> Check<T>(string json) =>
        ContractSchemas.Validator(typeof(T)).Validate(Repository.Json(json));

    [Fact]
    public void Every_p1b_record_writes_what_its_schema_accepts()
    {
        var facts = Repository.Json("""{"chunks":14,"tokens":5200}""");
        object[] samples =
        [
            new StagePreviewPayload { PipelineVersionId = Id, CollectionId = Id, SourceId = Id, DocumentIds = [Id], BasePreviewJobId = Id, UntilStage = "chunk" },
            new StagePreviewPayload { PipelineVersionId = Id, CollectionId = Id, Urls = ["https://www.umea.se/"], Embed = true, PiiSpans = true },
            new StagePreviewPayload { PipelineVersionId = Id, CollectionId = Id, SourceId = Id, UploadIds = [Id] },
            new PreflightPayload { CollectionId = Id, SourceType = "url-list", Settings = Repository.Json("""{"urls":["https://www.umea.se/"]}""") },
            new PreflightPayload { CollectionId = Id, SourceType = "crawl", Settings = Repository.Json("""{"startUrls":["https://www.umea.se/"],"maxPages":20000}""") },
            new PublishPlanPayload { PipelineId = Id, PipelineVersionId = Id },
            new EvalPublishReportPayload { CollectionId = Id, RunId = Id, PipelineVersionId = Id },
            new EvalNightlyPayload { CollectionId = Id, ScheduledFor = new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.FromHours(1)) },
            new EvalGenerateSetPayload { CollectionId = Id },
            new EvalTopUpSetPayload { CollectionId = Id },
            new EvalOnDemandPayload { CollectionId = Id, CandidateSettings = Repository.Json("""{"vectorWeight":1.0,"keywordWeight":1.5,"candidates":100,"maxChunksPerDocument":3,"recency":null}""") },
            new SyncPayload { SourceId = Id, Kind = SyncKind.Reprocess, Trigger = RunTrigger.Publish },
            new SyncPayload { SourceId = Id, Kind = SyncKind.Reprocess, Trigger = RunTrigger.Rollback },
            new SyncPayload { SourceId = Id, Kind = SyncKind.RetryFailed, Trigger = RunTrigger.Manual },
            new SyncPayload { SourceId = Id, Kind = SyncKind.Sync, Trigger = RunTrigger.Schedule, Incremental = true, ScheduledFor = DateTimeOffset.UnixEpoch },
            new ConfirmDeletionCommand { RunId = Id, DecidedBy = "6f1c2d3e-0000-4000-8000-000000000001" },
            new RejectDeletionCommand { RunId = Id, DecidedBy = "6f1c2d3e-0000-4000-8000-000000000001" },
            new ReleaseHoldCommand { SourceId = Id },
            new DeleteSourceCommand { SourceId = Id },
            new ClearPiiFlagCommand { DocumentId = Id, DecidedBy = "6f1c2d3e-0000-4000-8000-000000000001" },
            new ReprocessDocumentCommand { DocumentId = Id },
            new ReprocessDocumentCommand { DocumentId = Id, FromStage = "fetch" },
            new CheckHnswIndexCommand { EmbeddingProfileId = Id, Trigger = HnswCheckTrigger.Nightly },
            new CheckHnswIndexCommand { EmbeddingProfileId = Id, Trigger = HnswCheckTrigger.Sync },
            new StagePreviewResult
            {
                ExpiresAt = DateTimeOffset.UnixEpoch,
                Documents =
                [
                    new PreviewDocumentResult
                    {
                        Url = "https://www.umea.se/",
                        Stages =
                        [
                            new PreviewStageResult { Stage = "parse", State = PreviewStageState.Succeeded, DurationMs = 820, CacheHit = true, Output = $"previews/{Id}/0/parse.json", EstimatedCostUsd = 0m, CostUsd = 0m },
                            new PreviewStageResult { Stage = "embed", State = PreviewStageState.Skipped, Facts = facts },
                            new PreviewStageResult { Stage = "personal-data", State = PreviewStageState.Failed, Error = "ner.not_ready", Truncated = true },
                        ],
                        SiteProfile = new PreviewSiteProfile { Host = "www.umea.se", Origin = SiteProfileOrigin.Preset, PresetId = "sitevision", Before = $"previews/{Id}/0/before.json", After = $"previews/{Id}/0/after.json" },
                    },
                ],
            },
            new PreflightResult
            {
                State = PreflightState.Warn,
                EstimatedPages = 14100,
                Cms = "sitevision",
                Checks =
                [
                    new PreflightCheckResult { Check = "reachability", State = PreflightState.Pass, Code = "http.200", Target = "https://www.umea.se/" },
                    new PreflightCheckResult { Check = "pages", State = PreflightState.Warn, Code = "crawl.limit_pages", Facts = Repository.Json("""{"estimated":14100,"maxPages":10000}""") },
                ],
            },
            new PublishPlanResult { PipelineVersionId = Id, Documents = 1240, Sources = [new PublishPlanSource { SourceId = Id, Documents = 1240 }], ParserPages = 900, EmbeddingTokens = 2_100_000, EstimatedMinutes = 85, EstimatedCostUsd = 0.25m },
            new EvalRunResult { EvaluationRunId = Id, ComparedRunId = Id, QuestionCount = 140, Found = 101, Wins = 4, Losses = 2, RecallAlert = true },
            new EvalSetResult { SetId = Id, Round = 2, Generated = 160, Kept = 96, Retired = 7, Tokens = 198_000 },
            new EvalOnDemandResult { Runs = [new EvaluationRunRef { Variant = EvaluationVariant.Hybrid, EvaluationRunId = Id, QuestionCount = 140, Found = 101, Candidate = true }] },
            new DeletionCommandResult { Deleted = 12, Dropped = 1 },
            new DeletionCommandResult { Held = 30 },
            new ReleaseHoldResult { Released = 30 },
            new DeleteSourceResult { Documents = 13, Uploads = 13, CancelledJobs = 2 },
            new ClearPiiFlagResult { ReleasedChunkSetId = Id },
            new ClearPiiFlagResult(),
            new ReprocessDocumentResult { RunId = Id, ExecutionId = Id },
            new CheckHnswIndexResult { Scopes = 1, Queries = 200, Recall = 0.62, Rebuilt = true, RecallAfter = 0.99 },
            new CheckHnswIndexResult(),
        ];

        foreach (var sample in samples)
        {
            var json = JsonSerializer.SerializeToElement(sample, sample.GetType(), ContractJson.Options);
            ContractSchemas.Validator(sample.GetType()).Validate(json).ShouldBeEmpty(json.GetRawText());
            JsonSerializer.Serialize(ContractJson.Deserialize(json, sample.GetType()), sample.GetType(), ContractJson.Options).ShouldBe(json.GetRawText());
        }
    }

    [Fact]
    public void Kinds_are_the_documented_strings()
    {
        PreviewKinds.All.ShouldBe(["stage-preview", "preflight", "publish-plan"]);
        EvalKinds.All.ShouldBe(["generate-set", "top-up-set", "publish-report", "nightly", "on-demand"]);
        CommandKinds.All.ShouldBe(
        [
            "cancel-run", "edit-site-profile", "rollback-site-profile", "edit-eval-question",
            "confirm-deletion", "reject-deletion", "release-hold", "clear-pii-flag", "reprocess-document", "edit-parse-rule", "disable-parse-rule",
            "check-hnsw-index", "delete-source",
        ]);
        ContractJson.Serialize(new ReprocessDocumentCommand { DocumentId = Id }).ShouldBe($$"""{"kind":"reprocess-document","documentId":"{{G}}","fromStage":"parse"}""");
        ContractJson.Serialize(new StagePreviewPayload { PipelineVersionId = Id, CollectionId = Id, DocumentIds = [Id] })
            .ShouldBe($$"""{"kind":"stage-preview","pipelineVersionId":"{{G}}","collectionId":"{{G}}","documentIds":["{{G}}"],"untilStage":"personal-data","embed":false,"piiSpans":false}""");
    }

    [Theory]
    [InlineData(typeof(StagePreviewPayload), $$"""{"pipelineVersionId":"{{G}}","collectionId":"{{G}}","documentIds":["{{G}}"]}""")]
    [InlineData(typeof(PreflightPayload), $$$"""{"collectionId":"{{{G}}}","sourceType":"crawl","settings":{}}""")]
    [InlineData(typeof(PublishPlanPayload), $$"""{"pipelineId":"{{G}}","pipelineVersionId":"{{G}}"}""")]
    [InlineData(typeof(EvalPublishReportPayload), $$"""{"collectionId":"{{G}}","runId":"{{G}}","pipelineVersionId":"{{G}}"}""")]
    [InlineData(typeof(EvalNightlyPayload), $$"""{"collectionId":"{{G}}","scheduledFor":"2026-11-02T03:00:00+01:00"}""")]
    [InlineData(typeof(EvalGenerateSetPayload), $$"""{"collectionId":"{{G}}"}""")]
    [InlineData(typeof(EvalTopUpSetPayload), $$"""{"kind":"generate-set","collectionId":"{{G}}"}""")]
    [InlineData(typeof(ConfirmDeletionCommand), $$"""{"kind":"reject-deletion","runId":"{{G}}","decidedBy":"x"}""")]
    [InlineData(typeof(RejectDeletionCommand), $$"""{"runId":"{{G}}","decidedBy":"x"}""")]
    [InlineData(typeof(ReleaseHoldCommand), $$"""{"sourceId":"{{G}}"}""")]
    [InlineData(typeof(DeleteSourceCommand), $$"""{"kind":"release-hold","sourceId":"{{G}}"}""")]
    [InlineData(typeof(ClearPiiFlagCommand), $$"""{"documentId":"{{G}}","decidedBy":"x"}""")]
    [InlineData(typeof(ReprocessDocumentCommand), $$"""{"documentId":"{{G}}"}""")]
    [InlineData(typeof(CheckHnswIndexCommand), $$"""{"embeddingProfileId":"{{G}}","trigger":"nightly"}""")]
    public void Kind_is_required_and_exact(Type type, string json) =>
        ContractSchemas.Validator(type).Validate(Repository.Json(json)).ShouldNotBeEmpty();

    [Theory]
    [InlineData(typeof(StagePreviewPayload), $$"""{"kind":"stage-preview","collectionId":"{{G}}","documentIds":["{{G}}"]}""")]
    [InlineData(typeof(PreflightPayload), $$"""{"kind":"preflight","collectionId":"{{G}}","sourceType":"crawl"}""")]
    [InlineData(typeof(PublishPlanPayload), $$"""{"kind":"publish-plan","pipelineVersionId":"{{G}}"}""")]
    [InlineData(typeof(EvalPublishReportPayload), $$"""{"kind":"publish-report","collectionId":"{{G}}","pipelineVersionId":"{{G}}"}""")]
    [InlineData(typeof(EvalNightlyPayload), $$"""{"kind":"nightly","collectionId":"{{G}}"}""")]
    [InlineData(typeof(EvalTopUpSetPayload), """{"kind":"top-up-set"}""")]
    [InlineData(typeof(ConfirmDeletionCommand), $$"""{"kind":"confirm-deletion","runId":"{{G}}"}""")]
    [InlineData(typeof(RejectDeletionCommand), $$"""{"kind":"reject-deletion","runId":"{{G}}","decidedBy":""}""")]
    [InlineData(typeof(ReleaseHoldCommand), """{"kind":"release-hold"}""")]
    [InlineData(typeof(DeleteSourceCommand), """{"kind":"delete-source"}""")]
    [InlineData(typeof(ClearPiiFlagCommand), $$"""{"kind":"clear-pii-flag","documentId":"{{G}}"}""")]
    [InlineData(typeof(ReprocessDocumentCommand), """{"kind":"reprocess-document","fromStage":"parse"}""")]
    [InlineData(typeof(ReprocessDocumentCommand), $$"""{"kind":"reprocess-document","documentId":"{{G}}","fromStage":"ocr"}""")]
    [InlineData(typeof(CheckHnswIndexCommand), $$"""{"kind":"check-hnsw-index","embeddingProfileId":"{{G}}"}""")]
    [InlineData(typeof(CheckHnswIndexCommand), $$"""{"kind":"check-hnsw-index","embeddingProfileId":"{{G}}","trigger":"weekly"}""")]
    public void A_payload_missing_a_required_member_or_out_of_range_fails(Type type, string json) =>
        ContractSchemas.Validator(type).Validate(Repository.Json(json)).ShouldNotBeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData($$""","documentIds":["{{G}}"],"urls":["https://www.umea.se/"]""")]
    [InlineData(""","documentIds":[]""")]
    [InlineData($$""","documentIds":["{{G}}","0193a4c2-7d1e-7b3f-9c2a-000000000001","0193a4c2-7d1e-7b3f-9c2a-000000000002","0193a4c2-7d1e-7b3f-9c2a-000000000003","0193a4c2-7d1e-7b3f-9c2a-000000000004","0193a4c2-7d1e-7b3f-9c2a-000000000005"]""")]
    [InlineData(""","urls":["ftp://www.umea.se/"]""")]
    [InlineData($$""","uploadIds":["{{G}}"]""")]
    [InlineData($$""","documentIds":["{{G}}"],"untilStage":"ocr" """)]
    public void A_stage_preview_names_one_to_five_documents_one_way(string target) =>
        Check<StagePreviewPayload>($$"""{"kind":"stage-preview","pipelineVersionId":"{{G}}","collectionId":"{{G}}"{{target}}}""").ShouldNotBeEmpty();

    [Fact]
    public void A_url_list_preflight_samples_at_most_20_urls()
    {
        static string Payload(string sourceType, int count, string member) =>
            $$$"""{"kind":"preflight","collectionId":"{{{G}}}","sourceType":"{{{sourceType}}}","settings":{"{{{member}}}":[{{{string.Join(",", Enumerable.Range(0, count).Select(i => $"\"https://www.umea.se/{i}\""))}}}]}}""";

        Check<PreflightPayload>(Payload("url-list", 20, "urls")).ShouldBeEmpty();
        Check<PreflightPayload>(Payload("url-list", 21, "urls")).ShouldNotBeEmpty();
        Check<PreflightPayload>(Payload("crawl", 21, "startUrls")).ShouldBeEmpty();
        Check<PreflightPayload>($$$"""{"kind":"preflight","collectionId":"{{{G}}}","sourceType":"upload","settings":{}}""").ShouldNotBeEmpty();
        Check<PreflightPayload>($$"""{"kind":"preflight","collectionId":"{{G}}","sourceType":"crawl","settings":[]}""").ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("""{"kind":"sync","trigger":"publish"}""")]
    [InlineData("""{"kind":"retry-failed","trigger":"rollback"}""")]
    [InlineData("""{"kind":"reprocess","trigger":"schedule","incremental":true}""")]
    public void Publish_and_rollback_reprocess_and_only_a_sync_runs_incrementally(string members) =>
        Check<SyncPayload>($$"""{"sourceId":"{{G}}",{{members[1..]}}""").ShouldNotBeEmpty();

    [Fact]
    public void On_demand_candidate_settings_are_an_object()
    {
        Check<EvalOnDemandPayload>($$$"""{"kind":"on-demand","collectionId":"{{{G}}}","candidateSettings":{"vectorWeight":2.0}}""").ShouldBeEmpty();
        Check<EvalOnDemandPayload>($$"""{"kind":"on-demand","collectionId":"{{G}}","candidateSettings":"vectorWeight=2"}""").ShouldNotBeEmpty();
        var payload = ContractJson.Deserialize<EvalOnDemandPayload>(Repository.Json($$$"""{"kind":"on-demand","collectionId":"{{{G}}}","candidateSettings":{"vectorWeight":2.0}}"""));
        payload.CandidateSettings.ShouldNotBeNull().GetProperty("vectorWeight").GetDouble().ShouldBe(2.0);
    }

    [Fact]
    public void Results_carry_codes_and_preview_paths_only()
    {
        Check<PreflightResult>("""{"state":"fail","checks":[{"check":"egress","state":"fail","code":"Egress blocked"}]}""").ShouldNotBeEmpty();
        Check<StagePreviewResult>("""{"expiresAt":"2026-11-01T00:00:00Z","documents":[{"stages":[{"stage":"parse","state":"succeeded","output":"raw/internal/abc"}]}]}""").ShouldNotBeEmpty();
        Check<StagePreviewResult>("""{"expiresAt":"2026-11-01T00:00:00Z","documents":[{"stages":[{"stage":"parse","state":"done"}]}]}""").ShouldNotBeEmpty();
    }

    [Fact]
    public void Realistic_preview_and_preflight_payloads_stay_within_4_kb()
    {
        var preview = new StagePreviewPayload
        {
            PipelineVersionId = Id,
            CollectionId = Id,
            SourceId = Id,
            Urls = [.. Enumerable.Range(0, 5).Select(i => $"https://www.umea.se/{new string('a', 600)}/{i}")],
            BasePreviewJobId = Id,
        };
        Encoding.UTF8.GetByteCount(ContractJson.Serialize(preview)).ShouldBeLessThanOrEqualTo(4096);
        var preflight = new PreflightPayload
        {
            CollectionId = Id,
            SourceType = "url-list",
            Settings = ContractJson.ToElement(new { urls = Enumerable.Range(0, 20).Select(i => $"https://www.umea.se/kommun-och-politik/{new string('a', 100)}/{i}") }),
        };
        Encoding.UTF8.GetByteCount(ContractJson.Serialize(preflight)).ShouldBeLessThanOrEqualTo(4096);
    }

    [Fact]
    public void Dedup_keys_follow_admin_4_7()
    {
        new[]
        {
            JobDedupKeys.PiiPreview(Id), JobDedupKeys.Plan(Id), JobDedupKeys.EvalGenerate(Id), JobDedupKeys.Deletion(Id),
            JobDedupKeys.ReleaseHold(Id), JobDedupKeys.PiiClear(Id), JobDedupKeys.Reprocess(Id),
        }.ShouldBe([$"pii-preview:{G}", $"plan:{G}", $"eval-generate:{G}", $"deletion:{G}", $"release-hold:{G}", $"pii-clear:{G}", $"reprocess:{G}"]);
        JobDedupKeys.EvalReport(Id).ShouldBe($"eval-report:{G}");
        JobDedupKeys.DeleteSource(Id).ShouldBe($"delete-source:{G}");
        JobDedupKeys.EvalNightly(Id).ShouldNotBe(JobDedupKeys.Eval(Id));
    }

    /// <summary>
    /// Additive to J3: payloads and results exactly as the P1a records wrote them (and as the admin enqueues them since
    /// P1a) validate against the current schemas and bind to the current records.
    /// </summary>
    [Theory]
    [InlineData(typeof(SyncPayload), $$"""{"sourceId":"{{G}}","kind":"sync","trigger":"manual","phase":"list","force":false,"resumeFromCheckpoint":false}""")]
    [InlineData(typeof(SyncPayload), $$"""{"sourceId":"{{G}}","runId":"{{G}}","kind":"sync","trigger":"upload","phase":"reconcile","force":false,"resumeFromCheckpoint":false,"batchId":"{{G}}","traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}""")]
    [InlineData(typeof(SyncPayload), $$"""{"sourceId":"{{G}}","kind":"reprocess","trigger":"site-profile","phase":"list","force":true,"fromStage":"parse","resumeFromCheckpoint":false}""")]
    [InlineData(typeof(SyncPayload), $$"""{"sourceId":"{{G}}","runId":"{{G}}","kind":"retry-failed","trigger":"config-change","phase":"fetch","force":false,"resumeFromCheckpoint":false}""")]
    [InlineData(typeof(FetchPayload), $$"""{"sourceId":"{{G}}","runId":"{{G}}","urlHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData(typeof(ParsePayload), $$"""{"documentId":"{{G}}","level":"internal","executionId":"{{G}}","pipelineVersionId":"{{G}}","stage":"parse","inputKey":"0000000000000000000000000000000000000000000000000000000000000000","slice":2}""")]
    [InlineData(typeof(EmbedPayload), $$"""{"documentId":"{{G}}","level":"public","executionId":"{{G}}","pipelineVersionId":"{{G}}","runId":"{{G}}","inputKey":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"}""")]
    [InlineData(typeof(EvalOnDemandPayload), $$"""{"kind":"on-demand","collectionId":"{{G}}","variants":["vector","hybrid","hybrid-no-decompounder"]}""")]
    [InlineData(typeof(CancelRunCommand), $$"""{"kind":"cancel-run","runId":"{{G}}"}""")]
    [InlineData(typeof(RollbackSiteProfileCommand), $$"""{"kind":"rollback-site-profile","siteProfileId":"{{G}}","toVersion":2}""")]
    [InlineData(typeof(EditEvalQuestionCommand), $$"""{"kind":"edit-eval-question","action":"retire","collectionId":"{{G}}","questionId":"{{G}}","expectedDocumentIds":[]}""")]
    [InlineData(typeof(SyncResult), $$"""{"runId":"{{G}}","phase":"list","listed":1000,"enqueued":12,"unchanged":988,"failed":0,"deleted":0,"proposedDeletions":0,"continued":true}""")]
    [InlineData(typeof(DocumentJobResult), $$"""{"documentId":"{{G}}","outcome":"too-large","reason":"parser.too_large"}""")]
    [InlineData(typeof(CancelRunResult), """{"cancelledJobs":4}""")]
    [InlineData(typeof(EvalOnDemandResult), $$"""{"runs":[{"variant":"hybrid-no-decompounder","evaluationRunId":"{{G}}","questionCount":140,"found":101}]}""")]
    public void P1a_payloads_and_results_still_validate_and_bind(Type type, string json)
    {
        ContractSchemas.Validator(type).Validate(Repository.Json(json)).ShouldBeEmpty();
        Should.NotThrow(() => ContractJson.Deserialize(Repository.Json(json), type));
    }
}
