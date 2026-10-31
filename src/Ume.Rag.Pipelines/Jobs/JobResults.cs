using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

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

    [Range(0, int.MaxValue)]
    public required int QuestionCount { get; init; }

    [Description("Answerable questions with an expected document in the top k.")]
    [Range(0, int.MaxValue)]
    public required int Found { get; init; }
}
