using System.Text;
using System.Text.Json;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.Pipelines.SiteProfiles;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>
/// The job contracts (engine design §9.1, admin design §4.7): what the records write validates against their schemas,
/// the cross-member rules hold, readers tolerate unknown members, and the payloads stay within 4 KB.
/// </summary>
public sealed class JobContractTests
{
    private static readonly Guid Id = Guid.Parse("0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a");

    private static IReadOnlyList<Ume.Rag.Pipelines.Json.SchemaError> Check<T>(string json) =>
        ContractSchemas.Validator(typeof(T)).Validate(Repository.Json(json));

    [Fact]
    public void Every_record_writes_what_its_schema_accepts()
    {
        object[] samples =
        [
            new SyncPayload { SourceId = Id, Kind = SyncKind.Sync, Trigger = RunTrigger.Manual },
            new SyncPayload { SourceId = Id, RunId = Id, Kind = SyncKind.Sync, Trigger = RunTrigger.Upload, BatchId = Id, Phase = SyncPhase.Reconcile, Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" },
            new SyncPayload { SourceId = Id, Kind = SyncKind.Reprocess, Trigger = RunTrigger.SiteProfile, Force = true, FromStage = "parse" },
            new FetchPayload { SourceId = Id, RunId = Id, UrlHash = new string('a', 64) },
            new FetchPayload { SourceId = Id, RunId = Id, UploadId = Id, ExecutionId = Id },
            new ParsePayload { DocumentId = Id, Level = "internal", ExecutionId = Id, PipelineVersionId = Id, InputKey = new string('0', 64), Slice = 2 },
            new EmbedPayload { DocumentId = Id, Level = "public", ExecutionId = Id, PipelineVersionId = Id, RunId = Id, InputKey = new string('f', 64) },
            new EvalOnDemandPayload { CollectionId = Id, Variants = [EvaluationVariant.Vector, EvaluationVariant.Hybrid, EvaluationVariant.HybridNoDecompounder] },
            new CancelRunCommand { RunId = Id },
            new EditSiteProfileCommand { Host = "www.umea.se", PresetId = "sitevision", Profile = Umea },
            new RollbackSiteProfileCommand { SiteProfileId = Id, ToVersion = 2 },
            new EditEvalQuestionCommand { Action = QuestionAction.Create, CollectionId = Id, QuestionId = Id, Question = "Hur söker jag bygglov?", ExpectedDocumentIds = [Id] },
            new EditEvalQuestionCommand { Action = QuestionAction.Retire, CollectionId = Id, QuestionId = Id },
            new SyncResult { RunId = Id, Phase = SyncPhase.List, Listed = 1000, Enqueued = 12, Unchanged = 988, Continued = true },
            new DocumentJobResult { DocumentId = Id, ExecutionId = Id, Continuation = Continuation.Embed, Strategy = "parse=html(profile=www.umea.se@3)" },
            new DocumentJobResult { DocumentId = Id, Outcome = RunItemOutcome.TooLarge, Reason = "parser.too_large" },
            new DocumentJobResult { DocumentId = Id, Outcome = RunItemOutcome.Added, ChunkSetId = Id, Chunks = 14, Swap = SwapKind.New },
            new CancelRunResult { CancelledJobs = 4 },
            new SiteProfileCommandResult { SiteProfileId = Id, AppliedVersionId = Id, AppliedVersion = 3 },
            new EditEvalQuestionResult { QuestionId = Id, SetId = Id },
            new EvalOnDemandResult { Runs = [new EvaluationRunRef { Variant = EvaluationVariant.HybridNoDecompounder, EvaluationRunId = Id, QuestionCount = 140, Found = 101 }] },
        ];

        foreach (var sample in samples)
        {
            var json = JsonSerializer.SerializeToElement(sample, sample.GetType(), ContractJson.Options);
            ContractSchemas.Validator(sample.GetType()).Validate(json).ShouldBeEmpty(json.GetRawText());
            JsonSerializer.Serialize(ContractJson.Deserialize(json, sample.GetType()), sample.GetType(), ContractJson.Options).ShouldBe(json.GetRawText());
        }
    }

    [Fact]
    public void Names_and_enums_are_the_documented_strings()
    {
        var json = ContractJson.Serialize(new SyncPayload { SourceId = Id, RunId = Id, Kind = SyncKind.RetryFailed, Trigger = RunTrigger.ConfigChange, Phase = SyncPhase.Fetch });
        json.ShouldBe($$"""{"sourceId":"{{Id}}","runId":"{{Id}}","kind":"retry-failed","trigger":"config-change","phase":"fetch","force":false,"resumeFromCheckpoint":false}""");
        ContractJson.Serialize(new EvalOnDemandPayload { CollectionId = Id, Variants = [EvaluationVariant.HybridNoDecompounder] })
            .ShouldBe($$"""{"kind":"on-demand","collectionId":"{{Id}}","variants":["hybrid-no-decompounder"]}""");
    }

    [Fact]
    public void Readers_ignore_members_they_do_not_know()
    {
        var payload = ContractJson.Deserialize<CancelRunCommand>(Repository.Json($$"""{"kind":"cancel-run","runId":"{{Id}}","reason":"added later"}"""));
        payload.RunId.ShouldBe(Id);
        Check<CancelRunCommand>($$"""{"kind":"cancel-run","runId":"{{Id}}","reason":"added later"}""").ShouldBeEmpty();
    }

    [Fact]
    public void An_upload_sync_names_its_batch() =>
        Check<SyncPayload>($$"""{"sourceId":"{{Id}}","kind":"sync","trigger":"upload"}""").ShouldNotBeEmpty();

    [Fact]
    public void A_fetch_or_reconcile_sync_belongs_to_a_run() =>
        Check<SyncPayload>($$"""{"sourceId":"{{Id}}","kind":"sync","trigger":"manual","phase":"reconcile"}""").ShouldNotBeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData(""","urlHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","uploadId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a" """)]
    [InlineData(""","urlHash":"AAAA" """)]
    public void A_fetch_names_exactly_one_frontier_entry_or_upload_slot(string target) =>
        Check<FetchPayload>($$"""{"sourceId":"{{Id}}","runId":"{{Id}}"{{target}}}""").ShouldNotBeEmpty();

    [Theory]
    [InlineData("""{"kind":"edit-eval-question","action":"create","collectionId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a","questionId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a"}""")]
    [InlineData("""{"kind":"edit-eval-question","action":"delete","collectionId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a","questionId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a"}""")]
    [InlineData("""{"kind":"cancel-run","action":"retire","collectionId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a","questionId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a"}""")]
    public void An_eval_question_edit_is_complete(string json) =>
        Check<EditEvalQuestionCommand>(json).ShouldNotBeEmpty();

    [Fact]
    public void Kind_is_required_on_commands_and_eval_jobs()
    {
        Check<RollbackSiteProfileCommand>($$"""{"siteProfileId":"{{Id}}","toVersion":1}""").ShouldNotBeEmpty();
        Check<EvalOnDemandPayload>($$"""{"collectionId":"{{Id}}"}""").ShouldNotBeEmpty();
    }

    [Fact]
    public void The_largest_p1a_payloads_stay_within_4_kb()
    {
        var question = new EditEvalQuestionCommand
        {
            Action = QuestionAction.Update,
            CollectionId = Id,
            QuestionId = Id,
            Question = new string('å', 500),
            ExpectedAnswer = new string('ö', 1000),
            ExpectedDocumentIds = [.. Enumerable.Range(0, 10).Select(_ => Guid.CreateVersion7())],
        };
        Encoding.UTF8.GetByteCount(ContractJson.Serialize(question)).ShouldBeLessThanOrEqualTo(4096);
        Encoding.UTF8.GetByteCount(ContractJson.Serialize(new EditSiteProfileCommand { Host = "www.umea.se", BaseVersion = 3, Profile = Umea })).ShouldBeLessThanOrEqualTo(4096);
    }

    [Fact]
    public void Dedup_keys_follow_admin_4_7() =>
        new[] { JobDedupKeys.Sync(Id), JobDedupKeys.SyncUpload(Id), JobDedupKeys.Eval(Id), JobDedupKeys.CancelRun(Id), JobDedupKeys.SiteProfile(Id) }
            .ShouldBe([$"sync:{Id}", $"sync-upload:{Id}", $"eval:{Id}", $"cancel:{Id}", $"site-profile:{Id}"]);

    /// <summary>A Sitevision-shaped manual profile (the hand-checked umea.se profile itself is P1a item 8).</summary>
    internal static SiteProfile Umea { get; } = new()
    {
        Cms = "sitevision",
        MainRoot = "main",
        Boilerplate = new SiteProfileBoilerplate { Selectors = [".sv-share", ".sv-shorturl"], Phrases = ["Dela Facebook Twitter LinkedIn Mail Stäng dela"] },
        LinkBlocks = [".sv-subpages"],
        Breadcrumb = "nav[aria-label=\"Brödsmulor\"] ol.sol-breadcrumbs",
        DatePattern = "Sidan uppdaterades (?<d>\\d{4}-\\d{2}-\\d{2})",
        IdentityRule = new SiteProfileIdentityRule { Pattern = "^/(?:.*/)?[^/]*?\\.?(?<id>\\d+\\.[0-9a-f]+)\\.html$", Replacement = "/${id}.html" },
        IgnoreQueryParams = ["sv.state", "contactUserId"],
    };
}
