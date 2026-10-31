using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.Pipelines.SiteProfiles;

namespace Ume.Rag.Pipelines.Jobs;

/// <summary>
/// The P1a <c>command</c> kinds (engine design §9.1, admin design §4.7): admin decisions the worker applies, because
/// the admin only reads <c>content</c>. <c>kind</c> selects the handler. The other kinds of admin §4.7 are P1b and later.
/// </summary>
public static class CommandKinds
{
    public const string CancelRun = "cancel-run";
    public const string EditSiteProfile = "edit-site-profile";
    public const string RollbackSiteProfile = "rollback-site-profile";
    public const string EditEvalQuestion = "edit-eval-question";

    public static IReadOnlyList<string> All { get; } = [CancelRun, EditSiteProfile, RollbackSiteProfile, EditEvalQuestion];
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

    [Description("Set when the host already has a profile; must match the host.")]
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
