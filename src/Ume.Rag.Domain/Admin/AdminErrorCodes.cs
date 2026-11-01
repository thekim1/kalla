namespace Ume.Rag.Domain.Admin;

/// <summary>
/// A machine-readable admin error (admin design §4.2): the API answers in English with <see cref="Code"/> as the problem's
/// <c>code</c> (or in <c>errorCodes</c> for a field), and the admin UI shows its Swedish text for the code from
/// <c>messages.ts</c>. <see cref="Message"/> may hold <c>{param}</c> placeholders filled from the problem's <c>params</c>.
/// </summary>
public sealed record AdminErrorCode(string Code, int Status, string Message);

/// <summary>
/// Every admin error code. Exported as <c>contracts/admin/v1/error-codes.json</c>; CI checks that the admin UI has a
/// Swedish text for each.
/// </summary>
public static class AdminErrorCodes
{
    public static AdminErrorCode InvalidValue { get; } = new("invalid_value", 400, "The value is invalid.");

    /// <summary>A CHECK constraint refused the change (23514); <c>{constraint}</c> names it.</summary>
    public static AdminErrorCode PlatformRule { get; } = new("platform_rule", 400, "The change breaks a platform rule ({constraint}).");

    public static AdminErrorCode NotFound { get; } = new("not_found", 404, "It does not exist, or you do not have access to it.");

    /// <summary>The row changed since it was read; the problem carries the current <c>version</c> (admin design §4.3).</summary>
    public static AdminErrorCode ConcurrentChange { get; } = new("concurrent_change", 409, "Someone else changed this while you were working. Reload and compare.");

    /// <summary>A unique constraint refused the change (23505).</summary>
    public static AdminErrorCode AlreadyExists { get; } = new("already_exists", 409, "Something with the same name or key already exists.");

    /// <summary>A foreign key refused the change (23503): something still refers to it, or it refers to something missing.</summary>
    public static AdminErrorCode InUse { get; } = new("in_use", 409, "The change conflicts with something that refers to it.");

    /// <summary>The config version lock was not granted within 5 s (admin design §4.4); retry.</summary>
    public static AdminErrorCode ConfigBusy { get; } = new("config_busy", 503, "Another change is being saved. Try again in a moment.");

    /// <summary>The organisation sync has no gateway Data API configured (<c>OrgSync:BaseUrl</c>).</summary>
    public static AdminErrorCode SyncOff { get; } = new("sync_off", 409, "The sync with the AI gateway is not configured.");

    /// <summary>The gateway did not answer or the mirror could not be written; the last mirror is kept. <c>{error}</c> is the error type.</summary>
    public static AdminErrorCode SyncFailed { get; } = new("sync_failed", 502, "The sync with the AI gateway failed ({error}). The last synced data is kept.");

    /// <summary>
    /// V8 (admin design §4.6): <c>{model}</c> is not in the gateway model mirror, or none of its residencies is within
    /// <c>{ceiling}</c>.
    /// </summary>
    public static AdminErrorCode ModelResidency { get; } = new("model_residency", 409, "The model {model} cannot run at the level's ceiling {ceiling}.");

    /// <summary>V8: the gateway's <c>/v1/models</c> at <c>{ceiling}</c> does not list <c>{model}</c> (also while its targets are drained).</summary>
    public static AdminErrorCode ModelNotListed { get; } = new("model_not_listed", 409, "The AI gateway does not offer {model} at {ceiling} right now. Try again later.");

    /// <summary>V8: the gateway could not be asked (unreachable, 5xx, or this service holds no key for <c>{ceiling}</c>); retry.</summary>
    public static AdminErrorCode GatewayUnavailable { get; } = new("gateway_unavailable", 503, "The AI gateway could not be reached to check the model. Try again.");

    /// <summary>V29: a data-protection policy breaks a rule (history modes, encryption, retention, blob key).</summary>
    public static AdminErrorCode PolicyRule { get; } = new("policy_rule", 400, "The data protection policy breaks a rule: {rule}");

    /// <summary>A field names a department (or something of one) outside the caller's departments (admin design §3.2).</summary>
    public static AdminErrorCode NotInScope { get; } = new("not_in_scope", 400, "That department is not one of yours.");

    /// <summary>No active default embedding profile to put a new collection on (V31; the migrator seeds it).</summary>
    public static AdminErrorCode NoEmbeddingProfile { get; } = new("no_embedding_profile", 409, "There is no default embedding profile. Ask the platform team.");

    /// <summary>A collection is still linked by an assistant's published version or draft, or still has sources; <c>affected</c> lists them.</summary>
    public static AdminErrorCode CollectionInUse { get; } = new("collection_in_use", 409, "The collection is still used. Remove it from the assistants and delete its sources first.");

    /// <summary>V2: lowering a collection to <c>{level}</c> would put a source above it; <c>affected</c> lists the sources.</summary>
    public static AdminErrorCode CollectionLevelConflict { get; } = new("collection_level_conflict", 409, "The collection cannot be lowered to {level}: a source has a higher level.");

    /// <summary>V3: an assistant's level is below the collection <c>{collection}</c> at <c>{level}</c>; <c>affected</c> lists the assistants.</summary>
    public static AdminErrorCode AssistantBelowCollection { get; } = new("assistant_below_collection", 409, "An assistant's level is below the collection {collection} ({level}).");

    /// <summary>V16: <c>{field}</c> must be <c>{expected}</c>.</summary>
    public static AdminErrorCode RetrievalSettingsInvalid { get; } = new("retrieval_settings_invalid", 400, "The retrieval setting {field} must be {expected}.");

    /// <summary>V1: a source above its collection <c>{collection}</c> (<c>{level}</c>).</summary>
    public static AdminErrorCode SourceAboveCollection { get; } = new("source_above_collection", 400, "A source cannot have a higher level than its collection {collection} ({level}).");

    /// <summary>V25: the source settings break a rule at <c>{field}</c>: <c>{problem}</c>.</summary>
    public static AdminErrorCode SourceSettingsInvalid { get; } = new("source_settings_invalid", 400, "The source setting {field} is invalid: {problem}");

    /// <summary>V25: a scope or identity change was saved without the token from the impact check.</summary>
    public static AdminErrorCode ImpactNotConfirmed { get; } = new("impact_not_confirmed", 409, "The change affects which documents the source holds. Check its impact and confirm.");

    /// <summary>V24: no published pipeline of a template for source type <c>{type}</c>.</summary>
    public static AdminErrorCode TemplateUnsupported { get; } = new("template_unsupported", 400, "There is no pipeline for the source type {type}.");

    /// <summary>A field only platform admins may change.</summary>
    public static AdminErrorCode AdminOnly { get; } = new("admin_only", 403, "Only a platform administrator can change this.");

    /// <summary>The run is not running (<c>{state}</c>), so it cannot be cancelled.</summary>
    public static AdminErrorCode RunNotActive { get; } = new("run_not_active", 409, "The run has already ended ({state}).");

    /// <summary>Upload batches belong to upload sources only.</summary>
    public static AdminErrorCode NotAnUploadSource { get; } = new("not_an_upload_source", 409, "Files can only be uploaded to an upload source.");

    /// <summary>Connectors §9.2: a legacy format (<c>{extension}</c>) deferred to P2.</summary>
    public static AdminErrorCode UploadFormatDeferred { get; } = new("upload_format_deferred", 415, "Files of type {extension} cannot be uploaded yet. Save the file as PDF or a newer Office format.");

    /// <summary>Connectors §9.2: an extension (<c>{extension}</c>) the platform does not accept.</summary>
    public static AdminErrorCode UploadTypeRefused { get; } = new("upload_type_refused", 415, "Files of type {extension} cannot be uploaded.");

    /// <summary>The file is larger than the source's limit (<c>{mb}</c> MB).</summary>
    public static AdminErrorCode UploadTooLarge { get; } = new("upload_too_large", 413, "The file is larger than {mb} MB.");

    /// <summary>File number <c>{n}</c> of the batch was already sent.</summary>
    public static AdminErrorCode UploadFileExists { get; } = new("upload_file_exists", 409, "File {n} of the batch has already been sent.");

    /// <summary>The commit names file <c>{n}</c>, which the batch does not hold.</summary>
    public static AdminErrorCode UploadFileMissing { get; } = new("upload_file_missing", 400, "File {n} has not been uploaded to the batch.");

    /// <summary>A batch holds 1–100 entries and at most 500 MB.</summary>
    public static AdminErrorCode UploadBatchTooLarge { get; } = new("upload_batch_too_large", 400, "A batch holds 1 to 100 files and at most 500 MB.");

    /// <summary>Sync now on a paused source: the worker would start no run. Resume it first.</summary>
    public static AdminErrorCode SourcePaused { get; } = new("source_paused", 409, "The source is paused. Resume it first.");

    /// <summary>The slug <c>{slug}</c> is taken (also by a deleted collection); <c>{suggestion}</c> is free.</summary>
    public static AdminErrorCode SlugTaken { get; } = new("slug_taken", 400, "The short name {slug} is already used. Try {suggestion}.");

    /// <summary>The batch is committed: no more files, and a commit with other entries is refused.</summary>
    public static AdminErrorCode UploadBatchCommitted { get; } = new("upload_batch_committed", 409, "The batch has already been committed with other files.");
    /// <summary>Only active keys that have not been rotated can be rotated.</summary>
    public static AdminErrorCode KeyNotRotatable { get; } = new("key_not_rotatable", 409, "Only an active key that has not been rotated can be rotated.");

    public static AdminErrorCode KeyRevoked { get; } = new("key_revoked", 409, "The key is revoked.");

    /// <summary>The key has no stored secret (created outside the admin, e.g. by a seed); rotate it to get one that can be shown.</summary>
    public static AdminErrorCode KeySecretUnavailable { get; } = new("key_secret_unavailable", 409, "The key's secret cannot be shown. Rotate the key to get one that can.");

    /// <summary>V23: a scoped assistant or collection is not of the key team's department.</summary>
    public static AdminErrorCode KeyScopeInvalid { get; } = new("key_scope_invalid", 400, "A key can only be limited to assistants and knowledge banks of its team's department.");

    /// <summary>A department admin's key must be limited to at least one assistant or knowledge bank of the team's department.</summary>
    public static AdminErrorCode KeyScopeRequired { get; } = new("key_scope_required", 400, "Limit the key to at least one assistant or knowledge bank of your department.");

    /// <summary>The admin has no valid <c>Security:KeyPepper</c>, so it cannot create keys.</summary>
    public static AdminErrorCode KeysUnavailable { get; } = new("keys_unavailable", 503, "Keys cannot be created right now. Ask the platform team.");
    /// <summary>The collection has no evaluation set yet (generate one or add human questions first).</summary>
    public static AdminErrorCode NoEvaluationSet { get; } = new("no_evaluation_set", 409, "The knowledge bank has no test questions yet.");

    /// <summary>Only questions of the human layer are edited by hand; generated ones are replaced by the generator.</summary>
    public static AdminErrorCode QuestionNotHuman { get; } = new("question_not_human", 409, "Only questions written by people can be changed here.");

    /// <summary>An edit of the same question is still waiting for the worker.</summary>
    public static AdminErrorCode QuestionEditPending { get; } = new("question_edit_pending", 409, "An earlier change of this question is still being saved. Try again in a moment.");

    /// <summary>Publish without a draft.</summary>
    public static AdminErrorCode NoDraft { get; } = new("no_draft", 409, "There is no draft to publish.");

    /// <summary>A never-published assistant's draft is its only version and cannot be dropped.</summary>
    public static AdminErrorCode DraftIsOnlyVersion { get; } = new("draft_is_only_version", 409, "The draft is the assistant's only version.");

    /// <summary>V34: the draft was created before the current published version (a restore since); confirm to publish it.</summary>
    public static AdminErrorCode DraftBaseChanged { get; } = new("draft_base_changed", 409, "The draft is based on an older version than the published one. Confirm to publish it anyway.");

    /// <summary>V34: description and contact are required to publish.</summary>
    public static AdminErrorCode AssistantContactMissing { get; } = new("assistant_contact_missing", 409, "Add a description and a contact before publishing.");

    /// <summary>V34: the definition does not resolve (<c>{link}</c>); <c>affected</c> lists the ids.</summary>
    public static AdminErrorCode VersionLinkMissing { get; } = new("version_link_missing", 409, "The assistant refers to something that is missing: {link}.");

    /// <summary>V11: at most 20 collections.</summary>
    public static AdminErrorCode TooManyCollections { get; } = new("too_many_collections", 409, "An assistant can use at most 20 knowledge banks.");

    /// <summary>V11: a collection of another department (shares are P2); <c>affected</c> lists them.</summary>
    public static AdminErrorCode CollectionNotShared { get; } = new("collection_not_shared", 409, "A knowledge bank belongs to another department.");

    /// <summary>V7: an audience client's ceiling is below the assistant's level <c>{level}</c>; <c>affected</c> lists the clients.</summary>
    public static AdminErrorCode AudienceAboveCeiling { get; } = new("audience_above_ceiling", 409, "A client cannot show an assistant at level {level}.");

    /// <summary>V9: a sensitive assistant for a client that stores history.</summary>
    public static AdminErrorCode ClientStoresHistory { get; } = new("client_stores_history", 409, "A sensitive assistant cannot be offered to a client that stores chat history.");

    /// <summary>V17 (and V15's text lengths): <c>{field}</c> is outside its range.</summary>
    public static AdminErrorCode QueryStepsInvalid { get; } = new("query_steps_invalid", 409, "The setting {field} is outside its allowed range.");

    /// <summary>V15: a not-found link is not https on an allowed host, or there are more than 5.</summary>
    public static AdminErrorCode LinkHostNotAllowed { get; } = new("link_host_not_allowed", 409, "A link must be https on an allowed site, at most 5 links.");

    public static IReadOnlyList<AdminErrorCode> All { get; } =
        [InvalidValue, PlatformRule, NotFound, ConcurrentChange, AlreadyExists, InUse, ConfigBusy, SyncOff, SyncFailed, ModelResidency, ModelNotListed,
            GatewayUnavailable, PolicyRule, NotInScope, NoEmbeddingProfile, CollectionInUse, CollectionLevelConflict, AssistantBelowCollection,
            RetrievalSettingsInvalid, SourceAboveCollection, SourceSettingsInvalid, ImpactNotConfirmed, TemplateUnsupported, AdminOnly, RunNotActive,
            NotAnUploadSource, UploadFormatDeferred, UploadTypeRefused, UploadTooLarge, UploadFileExists, UploadFileMissing, UploadBatchTooLarge,
            SourcePaused, SlugTaken, UploadBatchCommitted, KeyNotRotatable, KeyRevoked, KeySecretUnavailable, KeyScopeInvalid, KeyScopeRequired,
            KeysUnavailable, NoEvaluationSet, QuestionNotHuman, QuestionEditPending, NoDraft, DraftIsOnlyVersion, DraftBaseChanged,
            AssistantContactMissing, VersionLinkMissing, TooManyCollections, CollectionNotShared, AudienceAboveCeiling, ClientStoresHistory,
            QueryStepsInvalid, LinkHostNotAllowed];
}
