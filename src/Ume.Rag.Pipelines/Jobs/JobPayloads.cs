using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Jobs;

/// <summary>
/// What every job payload may carry (engine design §9.1, §17). Payloads hold ids and parameters only (≤ 4 KB) and are
/// read tolerantly: members a reader does not know are ignored, so changes are additive (P1 README §6).
/// </summary>
public abstract record JobPayload
{
    [Description("The oldest worker build that may run the job; an older instance requeues it without an attempt (engine design §9.1).")]
    [MaxLength(100)]
    public string? MinBuild { get; init; }

    [Description("traceparent of the job that enqueued this continuation; the job's span links to it (engine design §17).")]
    [RegularExpression(ContractPatterns.TraceParent)]
    public string? Traceparent { get; init; }
}

/// <summary>
/// <c>sync</c> (engine design §9.1, §9.2; connectors design §2.2, §9.1): listing, then reconcile, of one source. The admin
/// enqueues the first job of a run (sync now and retry failed: dedup <c>sync:{sourceId}</c>, priority 10; upload commit:
/// dedup <c>sync-upload:{batchId}</c>, <c>trigger</c> <c>upload</c> with <c>batchId</c>; publish confirm and rollback: a
/// <c>reprocess</c> per source, dedup <c>sync:{sourceId}</c>, <c>trigger</c> <c>publish</c>/<c>rollback</c>); the worker
/// opens the run and carries <c>runId</c> into its continuations. <c>retry-failed</c> lists only the documents whose last
/// run item failed; <c>reprocess</c> iterates the live documents and never fetches (engine design §9.2, §12).
/// </summary>
[Description("Payload of a sync job (engine design §9.1, §9.2; connectors design §2.2, §9.1).")]
[SchemaKeyword("allOf", """
    [
      { "if": { "properties": { "trigger": { "const": "upload" } }, "required": ["trigger"] }, "then": { "required": ["batchId"] } },
      { "if": { "properties": { "phase": { "enum": ["fetch", "reconcile"] } }, "required": ["phase"] }, "then": { "required": ["runId"] } },
      { "if": { "properties": { "trigger": { "enum": ["publish", "rollback"] } }, "required": ["trigger"] }, "then": { "properties": { "kind": { "const": "reprocess" } } } },
      { "if": { "properties": { "incremental": { "const": true } }, "required": ["incremental"] }, "then": { "properties": { "kind": { "const": "sync" } } } }
    ]
    """)]
public sealed record SyncPayload : JobPayload
{
    public required Guid SourceId { get; init; }

    [Description("The run; absent on the first job of a run (the worker opens it), set on every continuation.")]
    public Guid? RunId { get; init; }

    public required SyncKind Kind { get; init; }

    public required RunTrigger Trigger { get; init; }

    public SyncPhase Phase { get; init; } = SyncPhase.List;

    [Description("The cron occurrence a scheduled run is for (engine design §9.6).")]
    public DateTimeOffset? ScheduledFor { get; init; }

    [Description("reprocess: bypass the stage caches from fromStage on, with a new execution id (engine design §8).")]
    public bool Force { get; init; }

    [SchemaKeyword("enum", ContractPatterns.StageEnum)]
    public string? FromStage { get; init; }

    [Description("A continuation resumes listing from the source's run checkpoint in content.ConnectorCheckpoints (connectors design §2.3).")]
    public bool ResumeFromCheckpoint { get; init; }

    [Description("Upload commit: the batch whose manifest uploads/{sourceId}/batches/{batchId}.json the sync reads.")]
    public Guid? BatchId { get; init; }

    [Description("Crawl: an incremental run (incrementalSchedule, connectors design §5.4) fetches only seeds with a newer sitemap lastmod and new sitemap URLs, and never marks a traversal complete.")]
    public bool Incremental { get; init; }
}

/// <summary>
/// <c>fetch</c> (engine design §9.1, connectors design §2.2): one frontier entry of a URL-list or crawl source, or one
/// upload slot. Dedup <see cref="JobDedupKeys.FetchUrl"/> / <see cref="JobDedupKeys.FetchUpload"/>.
/// </summary>
[Description("Payload of a fetch job: one frontier entry (urlHash) or one upload slot (uploadId) of a source (engine design §9.1).")]
[SchemaKeyword("oneOf", """[{ "required": ["urlHash"] }, { "required": ["uploadId"] }]""")]
public sealed record FetchPayload : JobPayload
{
    public required Guid SourceId { get; init; }

    public required Guid RunId { get; init; }

    [Description("SHA-256 (hex) of the canonical URL: the key of the source's CrawlFrontier row (connectors design §6.0).")]
    [RegularExpression(ContractPatterns.Sha256Hex)]
    public string? UrlHash { get; init; }

    [Description("The upload slot (content.Uploads.Id).")]
    public Guid? UploadId { get; init; }

    [Description("Present once the document's execution is known (engine design §7.5).")]
    public Guid? ExecutionId { get; init; }
}

/// <summary>
/// <c>parse</c> (engine design §7.4, §9.1, §9.5): the parser-bound segment of one document. Dedup
/// <see cref="JobDedupKeys.Parse"/>.
/// </summary>
[Description("Payload of a parse job: the parser-bound segment (parse, clean, chunk, personal-data) of one document (engine design §7.4, §9.1, §9.5).")]
public sealed record ParsePayload : JobPayload
{
    public required Guid DocumentId { get; init; }

    [SchemaKeyword("enum", ContractPatterns.LevelEnum)]
    public required string Level { get; init; }

    public required Guid ExecutionId { get; init; }

    public required Guid PipelineVersionId { get; init; }

    [Description("The first stage to run.")]
    [SchemaKeyword("enum", ContractPatterns.StageEnum)]
    public string Stage { get; init; } = "parse";

    [Description("Cache key (hex) of the stage's input (engine design §3.3).")]
    [RegularExpression(ContractPatterns.Sha256Hex)]
    public required string InputKey { get; init; }

    [Description("A parse job yields after 10 minutes at a range boundary and re-enqueues itself with slice + 1 (engine design §9.5).")]
    [Range(0, 10_000)]
    public int Slice { get; init; }
}

/// <summary><c>embed</c> (engine design §7.4, §9.1, §14): embed and index one document. Dedup <see cref="JobDedupKeys.Embed"/>.</summary>
[Description("Payload of an embed job: the embed and index stages of one document (engine design §7.4, §9.1, §14).")]
public sealed record EmbedPayload : JobPayload
{
    public required Guid DocumentId { get; init; }

    [SchemaKeyword("enum", ContractPatterns.LevelEnum)]
    public required string Level { get; init; }

    public required Guid ExecutionId { get; init; }

    public required Guid PipelineVersionId { get; init; }

    public required Guid RunId { get; init; }

    [Description("Cache key (hex) of the embed stage's input (engine design §3.3).")]
    [RegularExpression(ContractPatterns.Sha256Hex)]
    public required string InputKey { get; init; }
}

/// <summary>
/// <c>eval</c> <c>on-demand</c> (engine design §9.1; processing design §13.4–§13.6, §14.5): retrieval evaluation of one
/// collection, optionally comparing unsaved retrieval settings (<c>CandidateSettings</c>) with the current ones.
/// </summary>
[Description("Payload of an eval job of kind on-demand (processing design §13.4-§13.6, §14.5): the collection's evaluation set, once per variant, over its live sets; with candidateSettings once more per variant under those settings. Dedup eval:{collectionId}.")]
public sealed record EvalOnDemandPayload : JobPayload
{
    public const string KindName = "on-demand";

    [JsonRequired]
    [SchemaKeyword("const", "\"on-demand\"")]
    public string Kind { get; init; } = KindName;

    public required Guid CollectionId { get; init; }

    [Description("One EvaluationRuns row per variant, run against the same index; the P1a three-way gold-set run lists all three.")]
    [MinLength(1)]
    [MaxLength(3)]
    [SchemaKeyword("uniqueItems", "true")]
    public IReadOnlyList<EvaluationVariant> Variants { get; init; } = [EvaluationVariant.Hybrid];

    [Description("Unsaved Collections.RetrievalSettings (knowledge design §12.1, checked by the admin against V16): each variant runs with the current and with these settings, current vs candidate (processing design §14.5).")]
    [SchemaKeyword("type", "[\"object\", \"null\"]")]
    [SchemaKeyword("maxProperties", "30")]
    public JsonElement? CandidateSettings { get; init; }
}

/// <summary>The dedup keys of the job kinds (admin design §4.7, engine design §7.6, §9.2).</summary>
public static class JobDedupKeys
{
    public static string Sync(Guid sourceId) => $"sync:{sourceId}";

    public static string SyncUpload(Guid batchId) => $"sync-upload:{batchId}";

    public static string FetchUrl(Guid sourceId, string urlHash) => $"fetch:{sourceId}:{urlHash}";

    public static string FetchUpload(Guid sourceId, Guid uploadId) => $"fetch:{sourceId}:upload:{uploadId}";

    public static string Parse(Guid documentId, Guid executionId) => $"parse:{documentId}:{executionId}";

    public static string Embed(Guid documentId, Guid executionId, Guid pipelineVersionId) => $"embed:{documentId}:{executionId}:{pipelineVersionId}";

    public static string Eval(Guid collectionId) => $"eval:{collectionId}";

    public static string CancelRun(Guid runId) => $"cancel:{runId}";

    public static string SiteProfile(Guid siteProfileId) => $"site-profile:{siteProfileId}";

    /// <summary>An <c>edit-site-profile</c> for a host that has no profile yet has no id to key on.</summary>
    public static string SiteProfileHost(string level, string host) => $"site-profile:{level}:{host}";

    /// <summary>A <c>stage-preview</c> with <c>piiSpans</c> for one document's PII highlights; other previews have no key.</summary>
    public static string PiiPreview(Guid documentId) => $"pii-preview:{documentId}";

    public static string Plan(Guid pipelineId) => $"plan:{pipelineId}";

    /// <summary><c>generate-set</c> and <c>top-up-set</c> share one key: a top-up never runs during a generation.</summary>
    public static string EvalGenerate(Guid collectionId) => $"eval-generate:{collectionId}";

    /// <summary>One <c>publish-report</c> per finished <c>reprocess</c> run (admin §4.7 names none).</summary>
    public static string EvalReport(Guid runId) => $"eval-report:{runId}";

    /// <summary>Kept apart from <see cref="Eval"/>, so a queued on-demand run and the nightly run never drop each other (admin §4.7 names none).</summary>
    public static string EvalNightly(Guid collectionId) => $"eval-nightly:{collectionId}";

    /// <summary><c>confirm-deletion</c> and <c>reject-deletion</c> of one run share one key.</summary>
    public static string Deletion(Guid runId) => $"deletion:{runId}";

    public static string ReleaseHold(Guid sourceId) => $"release-hold:{sourceId}";

    public static string PiiClear(Guid documentId) => $"pii-clear:{documentId}";

    public static string Reprocess(Guid documentId) => $"reprocess:{documentId}";
}
