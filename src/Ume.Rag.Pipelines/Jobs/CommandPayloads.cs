using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.Pipelines.SiteProfiles;

namespace Ume.Rag.Pipelines.Jobs;

/// <summary>
/// The P1 <c>command</c> kinds (engine design §9.1, admin design §4.7): admin decisions the worker applies, because
/// the admin only reads <c>content</c>. <c>kind</c> selects the handler. <c>approve-site-profile</c>,
/// <c>reject-site-profile</c>, <c>approve-profile</c> and <c>reject-profile</c> are P2.
/// </summary>
public static class CommandKinds
{
    public const string CancelRun = "cancel-run";
    public const string EditSiteProfile = "edit-site-profile";
    public const string RollbackSiteProfile = "rollback-site-profile";
    public const string EditEvalQuestion = "edit-eval-question";
    public const string ConfirmDeletion = "confirm-deletion";
    public const string RejectDeletion = "reject-deletion";
    public const string ReleaseHold = "release-hold";
    public const string ClearPiiFlag = "clear-pii-flag";
    public const string ReprocessDocument = "reprocess-document";

    public static IReadOnlyList<string> All { get; } =
    [
        CancelRun, EditSiteProfile, RollbackSiteProfile, EditEvalQuestion,
        ConfirmDeletion, RejectDeletion, ReleaseHold, ClearPiiFlag, ReprocessDocument,
    ];
}

/// <summary><c>cancel-run</c> (engine design §10.1): dedup <c>cancel:{runId}</c>, <c>Jobs.RunId</c> = the run.</summary>
[Description("Payload of the command cancel-run (engine design §10.1): cancels the run's queued and running jobs and sets Runs.State to cancelled. Dedup cancel:{runId}.")]
public sealed record CancelRunCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"cancel-run\"")]
    public string Kind { get; init; } = CommandKinds.CancelRun;

    public required Guid RunId { get; init; }
}

/// <summary>
/// <c>edit-site-profile</c> (connectors design §5.6, admin design §4.5): a platform admin's profile for a host becomes a
/// new <c>manual</c> version and is applied. The host's level is the job's <c>Classification</c>. The first edit of a
/// host creates its <c>SiteProfiles</c> row (P1a sets up the umea.se profile this way).
/// </summary>
[Description("Payload of the command edit-site-profile (connectors design §5.6): the profile becomes a new manual version of the host's site profile at the job's level and is applied. Dedup site-profile:{siteProfileId}, or site-profile:{level}:{host} for a host without a profile.")]
public sealed record EditSiteProfileCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"edit-site-profile\"")]
    public string Kind { get; init; } = CommandKinds.EditSiteProfile;

    [Description("Present when the host already has a profile; must match the host.")]
    public Guid? SiteProfileId { get; init; }

    [Description("Lower-case host (A-label), as in content.SiteProfiles.Host.")]
    [MaxLength(253)]
    [RegularExpression("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")]
    public required string Host { get; init; }

    [Description("Optimistic check: the number of the applied version the edit is based on; null when the host has none. A mismatch fails the command (site_profile.version_conflict).")]
    [Range(1, int.MaxValue)]
    public int? BaseVersion { get; init; }

    [Description("The built-in CMS preset the profile starts from (SiteProfiles.PresetId).")]
    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public string? PresetId { get; init; }

    public required SiteProfile Profile { get; init; }
}

/// <summary><c>rollback-site-profile</c> (connectors design §5.6): apply an earlier version again, one click.</summary>
[Description("Payload of the command rollback-site-profile (connectors design §5.6): applies an earlier version of the site profile again. Dedup site-profile:{siteProfileId}.")]
public sealed record RollbackSiteProfileCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"rollback-site-profile\"")]
    public string Kind { get; init; } = CommandKinds.RollbackSiteProfile;

    public required Guid SiteProfileId { get; init; }

    [Description("SiteProfileVersions.Number to apply.")]
    [Range(1, int.MaxValue)]
    public required int ToVersion { get; init; }
}

/// <summary>
/// <c>edit-eval-question</c> (processing design §13.4): a <c>human</c> question of a collection's evaluation set. The
/// admin generates <c>questionId</c> (v7) for a create, so a retried command is idempotent. A question with no expected
/// documents expects "not found" (pilot gold set).
/// </summary>
[Description("Payload of the command edit-eval-question (processing design §13.4): creates, updates or retires a human question of the collection's evaluation set. The admin generates questionId for a create.")]
[SchemaKeyword("allOf", """
    [
      { "if": { "properties": { "action": { "enum": ["create", "update"] } }, "required": ["action"] }, "then": { "required": ["question"] } }
    ]
    """)]
public sealed record EditEvalQuestionCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"edit-eval-question\"")]
    public string Kind { get; init; } = CommandKinds.EditEvalQuestion;

    public required QuestionAction Action { get; init; }

    public required Guid CollectionId { get; init; }

    public required Guid QuestionId { get; init; }

    [Description("Required for create and update. The lengths keep the payload within 4 KB.")]
    [MinLength(1)]
    [MaxLength(500)]
    public string? Question { get; init; }

    [Description("The facts a correct answer contains.")]
    [MaxLength(1000)]
    public string? ExpectedAnswer { get; init; }

    [Description("Documents a correct retrieval returns; empty = the question expects not found.")]
    [MaxLength(10)]
    [SchemaKeyword("uniqueItems", "true")]
    public IReadOnlyList<Guid> ExpectedDocumentIds { get; init; } = [];
}

/// <summary>
/// <c>confirm-deletion</c> (connectors design §13.3): the waiting proposals of a run in
/// <c>awaiting-deletion-confirmation</c> are re-validated and the rest soft-deleted. Dedup <see cref="JobDedupKeys.Deletion"/>.
/// </summary>
[Description("Payload of the command confirm-deletion (connectors design §13.3): re-validates the run's waiting deletion proposals (a document seen again by a later run is dropped) and soft-deletes the rest. Dedup deletion:{runId}.")]
public sealed record ConfirmDeletionCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"confirm-deletion\"")]
    public string Kind { get; init; } = CommandKinds.ConfirmDeletion;

    public required Guid RunId { get; init; }

    [Description("The deciding admin's oid, written to DeletionProposals.DecidedBy.")]
    [MinLength(1)]
    [MaxLength(200)]
    public required string DecidedBy { get; init; }
}

/// <summary>
/// <c>reject-deletion</c> (connectors design §13.3): nothing is deleted; the proposed documents are held until seen again
/// or released (<see cref="ReleaseHoldCommand"/>). Dedup <see cref="JobDedupKeys.Deletion"/>.
/// </summary>
[Description("Payload of the command reject-deletion (connectors design §13.3): deletes nothing, holds the proposed documents (Documents.HeldAt/HeldBy) and ends the run degraded with deletion.rejected. Dedup deletion:{runId}.")]
public sealed record RejectDeletionCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"reject-deletion\"")]
    public string Kind { get; init; } = CommandKinds.RejectDeletion;

    public required Guid RunId { get; init; }

    [Description("The deciding admin's oid, written to DeletionProposals.DecidedBy and Documents.HeldBy.")]
    [MinLength(1)]
    [MaxLength(200)]
    public required string DecidedBy { get; init; }
}

/// <summary><c>release-hold</c> (connectors design §13.3): the source's held documents become candidates at the next reconcile.</summary>
[Description("Payload of the command release-hold (connectors design §13.3): clears the holds of the source's documents kept by a rejected deletion; they become deletion candidates at the next reconcile. Dedup release-hold:{sourceId}.")]
public sealed record ReleaseHoldCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"release-hold\"")]
    public string Kind { get; init; } = CommandKinds.ReleaseHold;

    public required Guid SourceId { get; init; }
}

/// <summary>
/// <c>clear-pii-flag</c> (processing design §9.5): a person's decision on the document; the admin requires and audits the
/// reason (6001 <c>pii.clear</c>), so the payload carries only who decided. Dedup <see cref="JobDedupKeys.PiiClear"/>.
/// </summary>
[Description("Payload of the command clear-pii-flag (processing design §9.5): records the clearance on the document (Documents.PiiClearedAt, PiiClearedBy, PiiClearedCounts = the held set's PiiCounts) and releases its held set without re-processing. Dedup pii-clear:{documentId}.")]
public sealed record ClearPiiFlagCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"clear-pii-flag\"")]
    public string Kind { get; init; } = CommandKinds.ClearPiiFlag;

    public required Guid DocumentId { get; init; }

    [Description("The deciding admin's oid, written to Documents.PiiClearedBy.")]
    [MinLength(1)]
    [MaxLength(200)]
    public required string DecidedBy { get; init; }
}

/// <summary>
/// <c>reprocess-document</c> (admin design §4.7, <c>POST /api/documents/{id}/reprocess</c>): one document re-run from
/// <c>fromStage</c> with the stage caches bypassed, in a <c>reprocess</c> run of its source. Dedup
/// <see cref="JobDedupKeys.Reprocess"/>.
/// </summary>
[Description("Payload of the command reprocess-document (admin design §4.7): re-runs one document from fromStage with a new execution id, bypassing the stage caches from there on (engine design §8); fetch re-fetches, later stages start from the stored content. Dedup reprocess:{documentId}.")]
public sealed record ReprocessDocumentCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"reprocess-document\"")]
    public string Kind { get; init; } = CommandKinds.ReprocessDocument;

    public required Guid DocumentId { get; init; }

    [SchemaKeyword("enum", ContractPatterns.StageEnum)]
    public string FromStage { get; init; } = "parse";
}
