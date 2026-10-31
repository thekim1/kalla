using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Pipelines.Sources;

namespace Ume.Rag.Pipelines.Schemas;

/// <summary>One exported contract: its file under <c>contracts/</c> and the record it is generated from.</summary>
public sealed record ContractSchema(string Path, Type Type, string Title)
{
    public string Id => $"https://ume-rag.local/contracts/{Path}";

    public JsonObject Export() => ContractSchemaExporter.Export(Type, Id, Title);
}

/// <summary>
/// The worker contracts of P1a (P1 README §3 item 4, hand-off J3) and P1b (P1b README §3 item 2, hand-off L1; additive to
/// J3). The records are the source; each file in
/// <c>contracts/</c> is the record's export, and <c>WorkerContractTests</c> fails when a file and its record differ
/// (<c>UME_RAG_UPDATE_CONTRACTS=1</c> rewrites them).
/// </summary>
public static class ContractSchemas
{
    private static readonly ConcurrentDictionary<Type, SchemaValidator> Validators = new();

    public static IReadOnlyList<ContractSchema> All { get; } =
    [
        new("sources/v1/url-list.schema.json", typeof(UrlListSettings), "URL-list source settings"),
        new("sources/v1/crawl.schema.json", typeof(CrawlSettings), "Crawl source settings"),
        new("sources/v1/upload.schema.json", typeof(UploadSettings), "Upload source settings"),
        new("pipeline/v2/definition.schema.json", typeof(PipelineDefinition), "Pipeline definition"),
        new("pipeline/v2/template.schema.json", typeof(PipelineTemplate), "Pipeline template"),
        new("site-profiles/v1/site-profile.schema.json", typeof(SiteProfile), "Site profile"),
        new("jobs/v1/sync.payload.schema.json", typeof(SyncPayload), "sync job payload"),
        new("jobs/v1/sync.result.schema.json", typeof(SyncResult), "sync job result"),
        new("jobs/v1/fetch.payload.schema.json", typeof(FetchPayload), "fetch job payload"),
        new("jobs/v1/parse.payload.schema.json", typeof(ParsePayload), "parse job payload"),
        new("jobs/v1/embed.payload.schema.json", typeof(EmbedPayload), "embed job payload"),
        new("jobs/v1/document.result.schema.json", typeof(DocumentJobResult), "fetch, parse and embed job result"),
        new("jobs/v1/command.cancel-run.payload.schema.json", typeof(CancelRunCommand), "cancel-run command payload"),
        new("jobs/v1/command.cancel-run.result.schema.json", typeof(CancelRunResult), "cancel-run command result"),
        new("jobs/v1/command.edit-site-profile.payload.schema.json", typeof(EditSiteProfileCommand), "edit-site-profile command payload"),
        new("jobs/v1/command.rollback-site-profile.payload.schema.json", typeof(RollbackSiteProfileCommand), "rollback-site-profile command payload"),
        new("jobs/v1/command.site-profile.result.schema.json", typeof(SiteProfileCommandResult), "edit-site-profile and rollback-site-profile command result"),
        new("jobs/v1/command.edit-eval-question.payload.schema.json", typeof(EditEvalQuestionCommand), "edit-eval-question command payload"),
        new("jobs/v1/command.edit-eval-question.result.schema.json", typeof(EditEvalQuestionResult), "edit-eval-question command result"),
        new("jobs/v1/eval.on-demand.payload.schema.json", typeof(EvalOnDemandPayload), "eval on-demand job payload"),
        new("jobs/v1/eval.on-demand.result.schema.json", typeof(EvalOnDemandResult), "eval on-demand job result"),
        new("jobs/v1/preview.stage-preview.payload.schema.json", typeof(StagePreviewPayload), "preview stage-preview job payload"),
        new("jobs/v1/preview.stage-preview.result.schema.json", typeof(StagePreviewResult), "preview stage-preview job result"),
        new("jobs/v1/preview.preflight.payload.schema.json", typeof(PreflightPayload), "preview preflight job payload"),
        new("jobs/v1/preview.preflight.result.schema.json", typeof(PreflightResult), "preview preflight job result"),
        new("jobs/v1/preview.publish-plan.payload.schema.json", typeof(PublishPlanPayload), "preview publish-plan job payload"),
        new("jobs/v1/preview.publish-plan.result.schema.json", typeof(PublishPlanResult), "preview publish-plan job result"),
        new("jobs/v1/eval.publish-report.payload.schema.json", typeof(EvalPublishReportPayload), "eval publish-report job payload"),
        new("jobs/v1/eval.nightly.payload.schema.json", typeof(EvalNightlyPayload), "eval nightly job payload"),
        new("jobs/v1/eval.run.result.schema.json", typeof(EvalRunResult), "eval publish-report and nightly job result"),
        new("jobs/v1/eval.generate-set.payload.schema.json", typeof(EvalGenerateSetPayload), "eval generate-set job payload"),
        new("jobs/v1/eval.top-up-set.payload.schema.json", typeof(EvalTopUpSetPayload), "eval top-up-set job payload"),
        new("jobs/v1/eval.set.result.schema.json", typeof(EvalSetResult), "eval generate-set and top-up-set job result"),
        new("jobs/v1/command.confirm-deletion.payload.schema.json", typeof(ConfirmDeletionCommand), "confirm-deletion command payload"),
        new("jobs/v1/command.reject-deletion.payload.schema.json", typeof(RejectDeletionCommand), "reject-deletion command payload"),
        new("jobs/v1/command.deletion.result.schema.json", typeof(DeletionCommandResult), "confirm-deletion and reject-deletion command result"),
        new("jobs/v1/command.release-hold.payload.schema.json", typeof(ReleaseHoldCommand), "release-hold command payload"),
        new("jobs/v1/command.release-hold.result.schema.json", typeof(ReleaseHoldResult), "release-hold command result"),
        new("jobs/v1/command.clear-pii-flag.payload.schema.json", typeof(ClearPiiFlagCommand), "clear-pii-flag command payload"),
        new("jobs/v1/command.clear-pii-flag.result.schema.json", typeof(ClearPiiFlagResult), "clear-pii-flag command result"),
        new("jobs/v1/command.reprocess-document.payload.schema.json", typeof(ReprocessDocumentCommand), "reprocess-document command payload"),
        new("jobs/v1/command.reprocess-document.result.schema.json", typeof(ReprocessDocumentResult), "reprocess-document command result"),
        new("jobs/v1/command.edit-parse-rule.payload.schema.json", typeof(EditParseRuleCommand), "edit-parse-rule command payload"),
        new("jobs/v1/command.disable-parse-rule.payload.schema.json", typeof(DisableParseRuleCommand), "disable-parse-rule command payload"),
        new("jobs/v1/command.parse-rule.result.schema.json", typeof(ParseRuleCommandResult), "edit-parse-rule and disable-parse-rule command result"),
        new("jobs/v1/upload-batch.manifest.schema.json", typeof(UploadBatchManifest), "Upload batch manifest"),
    ];

    public static ContractSchema For(Type type) =>
        All.FirstOrDefault(c => c.Type == type) ?? throw new ArgumentException($"{type.Name} is not a worker contract.", nameof(type));

    /// <summary>The compiled schema of a contract record, for the admin's checks (V25, site-profile edits) and the worker.</summary>
    public static SchemaValidator Validator(Type type) => Validators.GetOrAdd(type, t => SchemaValidator.Compile(For(t).Export()));
}
