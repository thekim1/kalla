using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Jobs;

// Jobs.Result of a succeeded job (data-model §4.1): small JSON, ids and counts only, never content. A failed or dead
// job has no result; its LastError carries the <area>.<code> reason (admin design §4.7). Read tolerantly, like payloads.

/// <summary>Result of a <c>sync</c> job: what this job (one listing segment or the reconcile) did.</summary>
[Description("Result of a sync job: what this job (one listing segment, or the reconcile phase) did. The run's totals are in Runs.")]
public sealed record SyncResult
{
    public required Guid RunId { get; init; }

    public required SyncPhase Phase { get; init; }

    [Description("Items the listing yielded in this job.")]
    [Range(0, int.MaxValue)]
    public int Listed { get; init; }

    [Description("fetch jobs or frontier entries enqueued.")]
    [Range(0, int.MaxValue)]
    public int Enqueued { get; init; }

    [Range(0, int.MaxValue)]
    public int Unchanged { get; init; }

    [Range(0, int.MaxValue)]
    public int Failed { get; init; }

    [Description("Reconcile: documents soft-deleted, and deletions waiting for a decision (connectors design §13).")]
    [Range(0, int.MaxValue)]
    public int Deleted { get; init; }

    [Range(0, int.MaxValue)]
    public int ProposedDeletions { get; init; }

    [Description("The job yielded and enqueued its continuation (1,000 items or 2 minutes, engine design §9.2).")]
    public bool Continued { get; init; }
}

/// <summary>Result of a <c>fetch</c>, <c>parse</c> or <c>embed</c> job for one document.</summary>
[Description("Result of a fetch, parse or embed job: the document's outcome if it ended here, or the continuation enqueued in the same commit (engine design §7.4).")]
public sealed record DocumentJobResult
{
    public Guid? DocumentId { get; init; }

    public Guid? ExecutionId { get; init; }

    [Description("Present when the document ended in this job (its run item).")]
    public RunItemOutcome? Outcome { get; init; }

    [Description("<area>.<code> (engine design §10.2), e.g. http.404, parser.timeout.")]
    [MaxLength(100)]
    [RegularExpression(ContractPatterns.Reason)]
    public string? Reason { get; init; }

    [Description("RunItems.Strategy: the route taken (engine design §7.2).")]
    [MaxLength(500)]
    public string? Strategy { get; init; }

    [Description("Class of the continuation job enqueued; absent when the document ended here.")]
    public Continuation? Continuation { get; init; }

    [Description("parse: the slice this job ran (engine design §9.5).")]
    [Range(0, 10_000)]
    public int? Slice { get; init; }

    [Description("embed: the chunk set written or swapped back, its size and what the swap did.")]
    public Guid? ChunkSetId { get; init; }

    [Range(0, int.MaxValue)]
    public int? Chunks { get; init; }

    public SwapKind? Swap { get; init; }
}

/// <summary>Result of <c>cancel-run</c> (as built in P0 #20).</summary>
[Description("Result of the command cancel-run.")]
public sealed record CancelRunResult
{
    [Range(0, int.MaxValue)]
    public required int CancelledJobs { get; init; }
}

/// <summary>Result of <c>edit-site-profile</c> and <c>rollback-site-profile</c>: the version now applied.</summary>
[Description("Result of the commands edit-site-profile and rollback-site-profile: the version now applied (SiteProfiles.AppliedVersionId).")]
public sealed record SiteProfileCommandResult
{
    public required Guid SiteProfileId { get; init; }

    public required Guid AppliedVersionId { get; init; }

    [Range(1, int.MaxValue)]
    public required int AppliedVersion { get; init; }
}

/// <summary>Result of <c>edit-eval-question</c>.</summary>
[Description("Result of the command edit-eval-question.")]
public sealed record EditEvalQuestionResult
{
    public required Guid QuestionId { get; init; }

    [Description("EvaluationSets.Id of the collection (created with its first question).")]
    public required Guid SetId { get; init; }
}

/// <summary>Result of <c>eval</c> <c>on-demand</c>: one <c>EvaluationRuns</c> row per variant; the numbers are in the rows.</summary>
[Description("Result of an eval job of kind on-demand: one EvaluationRuns row per requested variant.")]
public sealed record EvalOnDemandResult
{
    [MinLength(1)]
    public required IReadOnlyList<EvaluationRunRef> Runs { get; init; }
}

public sealed record EvaluationRunRef
{
    public required EvaluationVariant Variant { get; init; }

    public required Guid EvaluationRunId { get; init; }

    [Description("Answerable questions (with expected documents): the denominator of recall@k (EvaluationRuns.QuestionCount).")]
    [Range(0, int.MaxValue)]
    public required int QuestionCount { get; init; }

    [Description("Answerable questions with an expected document in the top k.")]
    [Range(0, int.MaxValue)]
    public required int Found { get; init; }

    [Description("on-demand with candidateSettings: the run used the candidate settings, not the current ones.")]
    public bool Candidate { get; init; }

    [Description("The run per question layer, human (the gold set) first (processing design §13.4, weakness review §2).")]
    [MaxLength(2)]
    public IReadOnlyList<EvaluationLayerResult> Layers { get; init; } = [];

    [Description("The coverage-threshold proposal from the human layer's answerable and unanswerable questions (processing design §13.6); absent without human questions.")]
    public CoverageProposal? Coverage { get; init; }
}

/// <summary>One question layer of a run: recall@k and MRR@k over its answerable questions (processing design §13.6).</summary>
public sealed record EvaluationLayerResult
{
    public required EvaluationLayer Layer { get; init; }

    [Description("Answerable questions (with expected documents): the denominator of recall@k.")]
    [Range(0, int.MaxValue)]
    public required int Questions { get; init; }

    [Description("Answerable questions with an expected document in the top k.")]
    [Range(0, int.MaxValue)]
    public required int Found { get; init; }

    [Description("Questions without expected documents (expected outcome \"not found\"); not counted in recall@k and MRR@k.")]
    [Range(0, int.MaxValue)]
    public int Unanswerable { get; init; }

    [Description("Found / Questions; absent without answerable questions.")]
    [Range(0.0, 1.0)]
    public double? RecallAtK { get; init; }

    [Range(0.0, 1.0)]
    public double? Mrr { get; init; }
}

/// <summary>
/// The coverage thresholds that separate the human layer's answerable from its unanswerable questions best (fewest
/// misclassified; knowledge design §7, weakness review §2), and the not-found counts under the run's own thresholds.
/// </summary>
public sealed record CoverageProposal
{
    [Range(0, int.MaxValue)]
    public required int Answerable { get; init; }

    [Range(0, int.MaxValue)]
    public required int Unanswerable { get; init; }

    [Description("Answerable human questions whose coverage was low (false not-found).")]
    [Range(0, int.MaxValue)]
    public int FalseNotFound { get; init; }

    [Description("Unanswerable human questions whose coverage was low (correct not-found).")]
    [Range(0, int.MaxValue)]
    public int UnanswerableNotFound { get; init; }

    [Description("Proposed coverageMaxDistance; absent until the layer has answerable and unanswerable questions.")]
    public ThresholdProposal? CoverageMaxDistance { get; init; }

    [Description("Proposed coverageMinKeywordScore; absent in vector runs and until both groups have a keyword score.")]
    public ThresholdProposal? CoverageMinKeywordScore { get; init; }
}

/// <summary>A proposed threshold and how many human questions it misclassifies.</summary>
public sealed record ThresholdProposal
{
    [Range(0.0, 1_000_000.0)]
    public required double Value { get; init; }

    [Range(0, int.MaxValue)]
    public required int Misclassified { get; init; }
}

/// <summary>Result of <c>stage-preview</c> (engine design §11): per document and stage; the outputs are blobs under <c>previews/{jobId}/</c>.</summary>
[Description("Result of a preview job of kind stage-preview (engine design §11): per document and stage the state, duration, cache use, facts, cost and error, and the blob paths of the outputs the admin streams on demand.")]
public sealed record StagePreviewResult
{
    [MaxLength(5)]
    public required IReadOnlyList<PreviewDocumentResult> Documents { get; init; }

    [Description("When the preview blobs expire (24 h after the job).")]
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>One document of a preview: as named in the payload (document, URL or upload slot).</summary>
public sealed record PreviewDocumentResult
{
    public Guid? DocumentId { get; init; }

    [MaxLength(2048)]
    public string? Url { get; init; }

    public Guid? UploadId { get; init; }

    [Description("In stage order, up to untilStage.")]
    public required IReadOnlyList<PreviewStageResult> Stages { get; init; }

    [Description("Crawl sources: the extraction without and with the host's site profile.")]
    public PreviewSiteProfile? SiteProfile { get; init; }
}

public sealed record PreviewStageResult
{
    [SchemaKeyword("enum", ContractPatterns.StageEnum)]
    public required string Stage { get; init; }

    public required PreviewStageState State { get; init; }

    [Range(0, int.MaxValue)]
    public int DurationMs { get; init; }

    [Description("Served from the stage cache or from the base preview's outputs.")]
    public bool CacheHit { get; init; }

    [Description("The stage's facts (engine design §2.2), e.g. embed's chunk count and tokens, index's \"would write N chunks\".")]
    [SchemaKeyword("type", "[\"object\", \"null\"]")]
    public JsonElement? Facts { get; init; }

    [Range(0.0, 1_000_000.0)]
    public decimal? EstimatedCostUsd { get; init; }

    [Range(0.0, 1_000_000.0)]
    public decimal? CostUsd { get; init; }

    [Description("<area>.<code> of a failed stage (engine design §10.2).")]
    [MaxLength(100)]
    [RegularExpression(ContractPatterns.Reason)]
    public string? Error { get; init; }

    [Description("Blob path of the stage's output: extracted text with breadcrumb and page markers, sections, chunks with prefixes and token counts, or PII counts (and spans with piiSpans).")]
    [MaxLength(500)]
    [RegularExpression(ContractPatterns.PreviewBlob)]
    public string? Output { get; init; }

    [Description("The output exceeded 2 MB and was cut with a marker.")]
    public bool Truncated { get; init; }
}

/// <summary>The site profile's before/after of a crawl preview (admin design §5.4 step 5; P1: a manual profile or the preset).</summary>
public sealed record PreviewSiteProfile
{
    [MaxLength(253)]
    public required string Host { get; init; }

    public required SiteProfileOrigin Origin { get; init; }

    [Description("The applied manual version (SiteProfileVersions.Id), when origin is manual.")]
    public Guid? SiteProfileVersionId { get; init; }

    [Description("The built-in preset (e.g. sitevision), when origin is preset.")]
    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public string? PresetId { get; init; }

    [Description("Blob path of the extraction with the built-in defaults only.")]
    [MaxLength(500)]
    [RegularExpression(ContractPatterns.PreviewBlob)]
    public required string Before { get; init; }

    [Description("Blob path of the extraction with the site profile.")]
    [MaxLength(500)]
    [RegularExpression(ContractPatterns.PreviewBlob)]
    public required string After { get; init; }
}

/// <summary>Result of <c>preflight</c>: one entry per check; the admin maps each code to its Swedish text (labels.ts, messages.ts).</summary>
[Description("Result of a preview job of kind preflight (connectors design §2.2; admin design §5.4 step 4): the worst state and one entry per check. A fail blocks the save, a warn explains.")]
public sealed record PreflightResult
{
    [Description("The worst state of the checks.")]
    public required PreflightState State { get; init; }

    [MaxLength(100)]
    public required IReadOnlyList<PreflightCheckResult> Checks { get; init; }

    [Description("Crawl: in-scope sitemap URLs, compared with maxPages.")]
    [Range(0, int.MaxValue)]
    public int? EstimatedPages { get; init; }

    [Description("The CMS preset recognised on the start host (e.g. sitevision).")]
    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public string? Cms { get; init; }
}

public sealed record PreflightCheckResult
{
    [Description("The check: reachability, robots, sitemaps, pages, egress or cms (new names may be added).")]
    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public required string Check { get; init; }

    public required PreflightState State { get; init; }

    [Description("<area>.<code>, e.g. http.404, robots.disallow, egress.blocked; the admin shows its Swedish text and remediation.")]
    [MaxLength(100)]
    [RegularExpression(ContractPatterns.Reason)]
    public required string Code { get; init; }

    [Description("The URL or host the entry is about.")]
    [MaxLength(2048)]
    public string? Target { get; init; }

    [Description("Small facts for the text (counts, status codes); never page content.")]
    [SchemaKeyword("type", "[\"object\", \"null\"]")]
    public JsonElement? Facts { get; init; }
}

/// <summary>Result of <c>publish-plan</c> (engine design §12.2): in P1 every live document of the affected sources, re-processed from <c>parse</c>.</summary>
[Description("Result of a preview job of kind publish-plan (engine design §12.2): the documents to re-process and the forecast. P1: every live document of the affected sources from parse; unchanged stages are then served from the caches.")]
public sealed record PublishPlanResult
{
    public required Guid PipelineVersionId { get; init; }

    [Range(0, int.MaxValue)]
    public required int Documents { get; init; }

    public required IReadOnlyList<PublishPlanSource> Sources { get; init; }

    [Description("The first stage the forecast counts from (P1: parse; per-stage groups are P2).")]
    [SchemaKeyword("enum", ContractPatterns.StageEnum)]
    public string FromStage { get; init; } = "parse";

    [Range(0, int.MaxValue)]
    public int ParserPages { get; init; }

    [Range(0, int.MaxValue)]
    public int EmbeddingTokens { get; init; }

    [Description("Conservative time estimate in minutes.")]
    [Range(0, int.MaxValue)]
    public int EstimatedMinutes { get; init; }

    [Range(0.0, 1_000_000.0)]
    public decimal EstimatedCostUsd { get; init; }
}

public sealed record PublishPlanSource
{
    public required Guid SourceId { get; init; }

    [Range(0, int.MaxValue)]
    public required int Documents { get; init; }
}

/// <summary>Result of <c>eval</c> <c>publish-report</c> and <c>nightly</c>: the <c>EvaluationRuns</c> row and its comparison.</summary>
[Description("Result of an eval job of kind publish-report or nightly: the EvaluationRuns row written and its comparison with the previous run; per-question results are in the row's results.")]
public sealed record EvalRunResult
{
    public required Guid EvaluationRunId { get; init; }

    [Description("The run compared with (EvaluationRuns.ComparedRunId); absent for a collection's first run.")]
    public Guid? ComparedRunId { get; init; }

    [Description("Answerable questions (with expected documents): the denominator of recall@k (EvaluationRuns.QuestionCount).")]
    [Range(0, int.MaxValue)]
    public required int QuestionCount { get; init; }

    [Description("Answerable questions with an expected document in the top k.")]
    [Range(0, int.MaxValue)]
    public required int Found { get; init; }

    [Description("Questions found now and not in the compared run, and the reverse.")]
    [Range(0, int.MaxValue)]
    public int Wins { get; init; }

    [Range(0, int.MaxValue)]
    public int Losses { get; init; }

    [Description("nightly: recall@10 fell 5 points or more below the 4-week median (processing design §14.8).")]
    public bool RecallAlert { get; init; }

    [Description("The run per question layer, human (the gold set) first (processing design §13.4, weakness review §2).")]
    [MaxLength(2)]
    public IReadOnlyList<EvaluationLayerResult> Layers { get; init; } = [];
}

/// <summary>Result of <c>generate-set</c> and <c>top-up-set</c> (processing design §13.1, §13.3).</summary>
[Description("Result of an eval job of kind generate-set or top-up-set: the round written to the collection's evaluation set.")]
public sealed record EvalSetResult
{
    public required Guid SetId { get; init; }

    [Description("The generation round; its seed is SHA-256(setId || round).")]
    [Range(1, int.MaxValue)]
    public required int Round { get; init; }

    [Description("Candidate questions generated before filtering.")]
    [Range(0, int.MaxValue)]
    public int Generated { get; init; }

    [Description("Questions kept after filtering and added to the generated layer.")]
    [Range(0, int.MaxValue)]
    public int Kept { get; init; }

    [Description("top-up-set: stale questions retired.")]
    [Range(0, int.MaxValue)]
    public int Retired { get; init; }

    [Description("Model tokens used (generation and judge).")]
    [Range(0, int.MaxValue)]
    public int Tokens { get; init; }
}

/// <summary>Result of <c>confirm-deletion</c> and <c>reject-deletion</c> (connectors design §13.3).</summary>
[Description("Result of the commands confirm-deletion and reject-deletion: documents soft-deleted, proposals dropped because the document was seen again, and documents held.")]
public sealed record DeletionCommandResult
{
    [Range(0, int.MaxValue)]
    public int Deleted { get; init; }

    [Range(0, int.MaxValue)]
    public int Dropped { get; init; }

    [Range(0, int.MaxValue)]
    public int Held { get; init; }
}

[Description("Result of the command release-hold: holds cleared.")]
public sealed record ReleaseHoldResult
{
    [Range(0, int.MaxValue)]
    public required int Released { get; init; }
}

/// <summary>Result of <c>check-hnsw-index</c> (knowledge design §6.4).</summary>
[Description("Result of the command check-hnsw-index: the HNSW-searched collections and query chunks measured, recall@10 of the index against exact search (absent when no collection of the profile is searched through HNSW or the profile has no index), and, after a rebuild, the recall measured again.")]
public sealed record CheckHnswIndexResult
{
    [Description("HNSW-searched collections of the profile that were measured.")]
    [Range(0, int.MaxValue)]
    public int Scopes { get; init; }

    [Description("Sampled live child chunks used as queries.")]
    [Range(0, int.MaxValue)]
    public int Queries { get; init; }

    [Range(0.0, 1.0)]
    public double? Recall { get; init; }

    [Description("Whether the index was rebuilt (REINDEX INDEX CONCURRENTLY) because recall was below the floor.")]
    public bool Rebuilt { get; init; }

    [Description("Recall measured again after the rebuild.")]
    [Range(0.0, 1.0)]
    public double? RecallAfter { get; init; }
}

[Description("Result of the command clear-pii-flag.")]
public sealed record ClearPiiFlagResult
{
    [Description("The held set released to live; absent when the document had none (the clearance still covers later sets).")]
    public Guid? ReleasedChunkSetId { get; init; }
}

[Description("Result of the command reprocess-document: the run its run item is in and the new execution.")]
public sealed record ReprocessDocumentResult
{
    public required Guid RunId { get; init; }

    public required Guid ExecutionId { get; init; }
}

/// <summary>Result of <c>edit-parse-rule</c> and <c>disable-parse-rule</c>: the rule as it is after the command.</summary>
[Description("Result of the commands edit-parse-rule and disable-parse-rule: the rule as it is now (its parse-stage id is profileId@revision) and the number of reprocess syncs enqueued for the sources it re-keys.")]
public sealed record ParseRuleCommandResult
{
    public required Guid ProfileId { get; init; }

    [Description("ParseProfiles.State after the command.")]
    public required ParseRuleState State { get; init; }

    [Description("The rule's version: the first 16 hex characters of SHA-256 over its Match, Strategy and CleanupRules as stored (processing design §3.5).")]
    [RegularExpression("^[0-9a-f]{16}$")]
    public required string Revision { get; init; }

    [Description("False when the rule already had this content and state: nothing was written, audited or enqueued.")]
    public required bool Changed { get; init; }

    [Description("sync reprocess jobs requested, one per active source with parser-route documents in the old or the new scope; one already queued under sync:{sourceId} absorbs its request.")]
    [Range(0, int.MaxValue)]
    public required int ReprocessSyncs { get; init; }
}
