using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Kalla.Pipelines.Schemas;
using Kalla.Pipelines.SiteProfiles;

namespace Kalla.Pipelines.Jobs;

/// <summary>
/// The P1 <c>command</c> kinds (engine design §9.1, admin design §4.7): admin decisions the worker applies, because
/// the admin only reads <c>content</c>, and the worker's own <c>check-hnsw-index</c> (knowledge design §6.4). <c>kind</c>
/// selects the handler. <c>approve-site-profile</c>,
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
    public const string EditParseRule = "edit-parse-rule";
    public const string DisableParseRule = "disable-parse-rule";
    public const string CheckHnswIndex = "check-hnsw-index";
    public const string DeleteSource = "delete-source";

    public static IReadOnlyList<string> All { get; } =
    [
        CancelRun, EditSiteProfile, RollbackSiteProfile, EditEvalQuestion,
        ConfirmDeletion, RejectDeletion, ReleaseHold, ClearPiiFlag, ReprocessDocument, EditParseRule, DisableParseRule,
        CheckHnswIndex, DeleteSource,
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

    [Description("The deciding platform admin's oid, written to SiteProfileVersions.CreatedBy and AppliedBy; absent = system:worker.")]
    [MinLength(1)]
    [MaxLength(200)]
    public string? DecidedBy { get; init; }
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

    [Description("The deciding platform admin's oid, written to SiteProfileVersions.AppliedBy; absent = system:worker.")]
    [MinLength(1)]
    [MaxLength(200)]
    public string? DecidedBy { get; init; }
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

/// <summary>
/// <c>delete-source</c> (Decisions "Deleting knowledge bases, sources and assistants"; data-model §8): queued by the admin's
/// delete of a source, with it. Dedup <see cref="JobDedupKeys.DeleteSource"/>.
/// </summary>
[Description("Payload of the command delete-source (data-model §8): the deleted source's running runs are cancelled, its documents and the chunks of their live sets soft-deleted (PurgeAfter after the 14-day grace), and its upload slots deleted with their blobs tombstoned. Refused for a source that is not deleted. Dedup delete-source:{sourceId}.")]
public sealed record DeleteSourceCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"delete-source\"")]
    public string Kind { get; init; } = CommandKinds.DeleteSource;

    public required Guid SourceId { get; init; }
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

/// <summary>
/// <c>edit-parse-rule</c> (processing design §3.5, admin design §4.7): a platform admin's parse rule becomes the content of
/// the <c>manual</c> <c>ParseProfiles</c> row <c>profileId</c> at the job's level, active. The admin generates
/// <c>profileId</c> (v7) for a new rule, so a retried create is idempotent. A save that changes the rule is a new revision
/// (its id <c>profileId@revision</c> is a content hash), audited, and enqueues a <c>sync</c> <c>reprocess</c> for each
/// active source with parser-route documents in the old or the new scope. Dedup <see cref="JobDedupKeys.ParseRule"/>.
/// </summary>
[Description("Payload of the command edit-parse-rule (processing design §3.5): creates or changes the manual parse rule profileId (content.ParseProfiles, Origin manual, State active) at the job's level; a change is a new revision, audited, and re-processes the sources with parser-route documents in the old or the new scope. The lengths keep the payload within 4 KB. Dedup parse-rule:{profileId}.")]
public sealed record EditParseRuleCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"edit-parse-rule\"")]
    public string Kind { get; init; } = CommandKinds.EditParseRule;

    [Description("ParseProfiles.Id; the admin generates it (v7) for a new rule.")]
    public required Guid ProfileId { get; init; }

    [Description("The department scope (ParseProfiles.DepartmentId): the rule applies to the sources of that department's collections only; absent = every department.")]
    public Guid? DepartmentId { get; init; }

    public required ParseRuleMatch Match { get; init; }

    public ParseRuleStrategy Strategy { get; init; } = new();

    [Description("Structure hints for clean and split by headings; absent = the defaults.")]
    public ParseRuleCleanup? CleanupRules { get; init; }

    [Description("The deciding platform admin's oid, written to the audit entry; absent = system:worker.")]
    [MinLength(1)]
    [MaxLength(200)]
    public string? DecidedBy { get; init; }
}

/// <summary>
/// Where a parse rule applies (<c>ParseProfiles.Match</c>; processing design §3.5), read by the worker's
/// <c>AdminParseRuleReader</c>: at least one member. Domain, path glob and media type select the documents whose parse key
/// the rule enters; a text pattern then decides on the parsed text whether the rule is the one applied.
/// </summary>
[Description("Where the rule applies (ParseProfiles.Match): at least one member. An unset member matches everything; documents without a URL (uploads) match no domain or path.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[SchemaKeyword("minProperties", "1")]
public sealed record ParseRuleMatch
{
    [Description("A lower-case host (A-label), or *. and a host for its subdomains only.")]
    [MaxLength(255)]
    [RegularExpression("^(\\*\\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")]
    public string? Domain { get; init; }

    [Description("Glob over the whole unescaped URL path, case-insensitive: * any run of characters (/ included), ? one character, e.g. /kommun/protokoll/*.")]
    [MinLength(1)]
    [MaxLength(300)]
    public string? PathGlob { get; init; }

    [Description("A media type or type/*, as detected (Documents.MediaType).")]
    [MaxLength(100)]
    [RegularExpression("^[a-z0-9.+-]+/([a-z0-9.+-]+|\\*)$")]
    public string? MediaType { get; init; }

    [Description(".NET regular expression (linear-time subset: no lookarounds or backreferences) matched on the title or the first 2,000 characters of the parsed text, case-insensitive.")]
    [MinLength(1)]
    [MaxLength(500)]
    [SchemaKeyword("format", "\"regex\"")]
    public string? TextPattern { get; init; }
}

/// <summary>Strategy overrides of a parse rule (<c>ParseProfiles.Strategy</c>; processing design §3.1, §3.5); unset = the template's.</summary>
[Description("Strategy overrides (ParseProfiles.Strategy); an unset member keeps the pipeline template's value.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParseRuleStrategy
{
    [SchemaKeyword("enum", """["auto", "force", "off"]""")]
    public string? Ocr { get; init; }

    [SchemaKeyword("enum", """["fast", "accurate"]""")]
    public string? Tables { get; init; }

    [Description("Render scale of OCR pages.")]
    [Range(1.0, 4.0)]
    public double? ImageScale { get; init; }
}

/// <summary>Structure hints of a parse rule (<c>ParseProfiles.CleanupRules</c>; processing design §4.2, §5.2).</summary>
[Description("Structure hints (ParseProfiles.CleanupRules) consumed by clean and split by headings.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParseRuleCleanup
{
    [Description("Headings deeper than this become bold body text; absent = 4.")]
    [Range(1, 6)]
    public int? MaxLevel { get; init; }

    [Description("Each § N item is a level-1 section (as the built-in protokoll rule).")]
    public bool ParagraphItemsAreSections { get; init; }

    [Description(".NET regular expressions (linear-time subset) of lines that clean drops as furniture, e.g. signature blocks.")]
    [MaxLength(10)]
    [SchemaItems(MinLength = 1, MaxLength = 150)]
    public IReadOnlyList<string> Furniture { get; init; } = [];
}

/// <summary>
/// <c>disable-parse-rule</c> (processing design §3.5, admin design §4.7): the <c>manual</c> rule stops applying; audited, and
/// the sources with parser-route documents in its scope are re-processed. Dedup <see cref="JobDedupKeys.ParseRule"/>.
/// </summary>
[Description("Payload of the command disable-parse-rule (processing design §3.5): sets the manual parse rule's ParseProfiles.State to disabled, audited, and re-processes the sources with parser-route documents in its scope. An edit-parse-rule enables it again. Dedup parse-rule:{profileId}.")]
public sealed record DisableParseRuleCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"disable-parse-rule\"")]
    public string Kind { get; init; } = CommandKinds.DisableParseRule;

    public required Guid ProfileId { get; init; }

    [Description("The deciding platform admin's oid, written to the audit entry; absent = system:worker.")]
    [MinLength(1)]
    [MaxLength(200)]
    public string? DecidedBy { get; init; }
}

/// <summary>
/// <c>check-hnsw-index</c> (knowledge design §6.4): the worker measures the recall of an embedding profile's HNSW index
/// against exact search and rebuilds it below <c>Settings.Retrieval.hnswRecallFloor</c>. Enqueued by the worker itself,
/// nightly and after a run that made more than 10 % of the profile's chunks live. Dedup <see cref="JobDedupKeys.HnswCheck"/>.
/// </summary>
[Description("Payload of the command check-hnsw-index (knowledge design §6.4): measures recall@10 of the embedding profile's HNSW index against exact search on sampled live child chunks of each HNSW-searched collection, and rebuilds the index concurrently below Settings.Retrieval.hnswRecallFloor. Dedup hnsw-check:{embeddingProfileId}.")]
public sealed record CheckHnswIndexCommand : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"check-hnsw-index\"")]
    public string Kind { get; init; } = CommandKinds.CheckHnswIndex;

    public required Guid EmbeddingProfileId { get; init; }

    public required HnswCheckTrigger Trigger { get; init; }
}
