using System.ComponentModel;
using System.Text.Json.Serialization;
using Kalla.Pipelines.Schemas;

namespace Kalla.Pipelines.Jobs;

/// <summary>
/// The <c>eval</c> kinds the worker runs (engine design §9.1; processing design §13, §14): retrieval evaluation and set
/// generation only. The answer-level <c>answers</c> run is P2 and runs in <c>rag-admin</c> (processing design §14.8).
/// </summary>
public static class EvalKinds
{
    public const string GenerateSet = "generate-set";
    public const string TopUpSet = "top-up-set";
    public const string PublishReport = "publish-report";
    public const string Nightly = "nightly";
    public const string OnDemand = EvalOnDemandPayload.KindName;

    public static IReadOnlyList<string> All { get; } = [GenerateSet, TopUpSet, PublishReport, Nightly, OnDemand];
}

/// <summary>
/// <c>publish-report</c> (engine design §10.4, §12.1 step 6; processing design §14.1): enqueued by the finalizer when the
/// <c>reprocess</c> run of a publish or rollback finishes. Dedup <see cref="JobDedupKeys.EvalReport"/>.
/// </summary>
[Description("Payload of an eval job of kind publish-report (processing design §14.1): the collection's full set on the full collection after the reprocess run of a publish or rollback, compared with the last run before the publish. Dedup eval-report:{runId}.")]
public sealed record EvalPublishReportPayload : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"publish-report\"")]
    public string Kind { get; init; } = EvalKinds.PublishReport;

    public required Guid CollectionId { get; init; }

    [Description("The finished reprocess run (Runs.Id); the report is shown next to it.")]
    public required Guid RunId { get; init; }

    [Description("The version the run published or rolled back to (processing design §14: EvaluationSubject.PipelineVersionId).")]
    public required Guid PipelineVersionId { get; init; }
}

/// <summary>
/// <c>nightly</c> (processing design §14.8): enqueued by the scheduler per published collection. Dedup
/// <see cref="JobDedupKeys.EvalNightly"/>.
/// </summary>
[Description("Payload of an eval job of kind nightly (processing design §14.8): the full set of one published collection, for the trend, rag.evaluation.recall_at_10 and the recall alert. Dedup eval-nightly:{collectionId}.")]
public sealed record EvalNightlyPayload : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"nightly\"")]
    public string Kind { get; init; } = EvalKinds.Nightly;

    public required Guid CollectionId { get; init; }

    [Description("The schedule occurrence the run is for (engine design §9.6).")]
    public required DateTimeOffset ScheduledFor { get; init; }
}

/// <summary>
/// <c>generate-set</c> and <c>top-up-set</c> (processing design §13.1, §13.3): the collection's generated layer. The model,
/// judge and target size come from <c>Settings</c> and the collection, not the payload. Dedup
/// <see cref="JobDedupKeys.EvalGenerate"/>.
/// </summary>
public abstract record EvalSetPayload : JobPayload
{
    public required Guid CollectionId { get; init; }
}

[Description("Payload of an eval job of kind generate-set (processing design §13.1): the first generated layer of the collection's evaluation set, after its first syncs or on demand. Dedup eval-generate:{collectionId}.")]
public sealed record EvalGenerateSetPayload : EvalSetPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"generate-set\"")]
    public string Kind { get; init; } = EvalKinds.GenerateSet;
}

[Description("Payload of an eval job of kind top-up-set (processing design §13.3): retires stale generated questions and regenerates to the target size with a new round seed (weekly check or on demand). Dedup eval-generate:{collectionId}.")]
public sealed record EvalTopUpSetPayload : EvalSetPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"top-up-set\"")]
    public string Kind { get; init; } = EvalKinds.TopUpSet;
}
