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

    /// <summary>The sync has no gateway configured (<c>Gateway:BaseUrl</c> and <c>Gateway:Key</c>).</summary>
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

    /// <summary>V8: the gateway could not be asked (unreachable, 5xx, a refused key); retry.</summary>
    public static AdminErrorCode GatewayUnavailable { get; } = new("gateway_unavailable", 503, "The AI gateway could not be reached to check the model. Try again.");

    /// <summary>
    /// V8: this service holds no gateway key for <c>{ceiling}</c>, so the model cannot be checked or called there (#237): a
    /// deployment setting (the credential key <c>Gateway:Key</c>, or its allowed residencies in the gateway), not a retry.
    /// </summary>
    public static AdminErrorCode GatewayKeyMissing { get; } = new("gateway_key_missing", 503, "No AI gateway key is configured for {ceiling}, so {model} cannot be checked there.");

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
    public static AdminErrorCode TemplateUnsupported { get; } = new("template_unsupported", 400, "There is no pipeline for the source type {type} that the running workers support.");

    /// <summary>A field only platform admins may change.</summary>
    public static AdminErrorCode AdminOnly { get; } = new("admin_only", 403, "Only a platform administrator can change this.");

    /// <summary>The run is not running (<c>{state}</c>), so it cannot be cancelled.</summary>
    public static AdminErrorCode RunNotActive { get; } = new("run_not_active", 409, "The run has already ended ({state}).");

    /// <summary>V26: a free-text reason shorter than <c>{min}</c> characters (field error).</summary>
    public static AdminErrorCode ReasonTooShort { get; } = new("reason_too_short", 400, "The reason must be at least {min} characters.");

    /// <summary>V28: the run is no longer waiting for a decision, or its proposal is not the one the user saw (connectors §13.3).</summary>
    public static AdminErrorCode DeletionSuperseded { get; } = new("deletion_superseded", 409, "This deletion proposal is no longer current. Open the source's latest run.");

    /// <summary>PII clear on a document with no flagged chunk set (nothing to release).</summary>
    public static AdminErrorCode PiiNotFlagged { get; } = new("pii_not_flagged", 409, "The document is not flagged for personal data.");

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

    /// <summary>The deployment's own client (identity design §5.2): its key is the deployment's, not the admin's (M-17 C4).</summary>
    public static AdminErrorCode ClientManagedByDeployment { get; } = new("client_managed_by_deployment", 409, "The deployment manages this client's key; deploy a new one to replace it.");

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
    /// <summary>The site profile breaks <c>contracts/site-profiles/v1</c> at <c>{field}</c>: <c>{problem}</c>.</summary>
    public static AdminErrorCode SiteProfileInvalid { get; } = new("site_profile_invalid", 400, "The site profile field {field} is invalid: {problem}");

    /// <summary>A rollback to the version that is already applied.</summary>
    public static AdminErrorCode SiteProfileVersionApplied { get; } = new("site_profile_version_applied", 400, "That version is already applied.");

    /// <summary>No department exists yet to queue the work for (run the organisation sync first).</summary>
    public static AdminErrorCode NoDepartment { get; } = new("no_department", 409, "There are no departments yet. Run the organisation sync first.");

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

    /// <summary>V11: a collection of another department that is not shared with the assistant's; <c>affected</c> lists them.</summary>
    public static AdminErrorCode CollectionNotShared { get; } = new("collection_not_shared", 409, "A knowledge bank belongs to another department and is not shared with this one.");

    /// <summary>V11: a link names more than 20 topics, or a topic outside the chunk tag format; <c>affected</c> lists the collections.</summary>
    public static AdminErrorCode TopicInvalid { get; } = new("topic_invalid", 409, "A topic is invalid: at most 20 per knowledge bank, each lower case letters, digits and hyphens, at most 50 characters.");

    /// <summary>V35: narrowing the sharing would cut off published assistants of other departments; <c>affected</c> lists them.</summary>
    public static AdminErrorCode CollectionSharedInUse { get; } = new("collection_shared_in_use", 409, "Assistants of other departments use the knowledge bank. They lose it if the sharing is narrowed.");

    /// <summary>V7: an audience client's ceiling is below the assistant's level <c>{level}</c>; <c>affected</c> lists the clients.</summary>
    public static AdminErrorCode AudienceAboveCeiling { get; } = new("audience_above_ceiling", 409, "A client cannot show an assistant at level {level}.");

    /// <summary>V9: a sensitive assistant for a client that stores history.</summary>
    public static AdminErrorCode ClientStoresHistory { get; } = new("client_stores_history", 409, "A sensitive assistant cannot be offered to a client that stores chat history.");

    /// <summary>V17 (and V15's text lengths): <c>{field}</c> is outside its range.</summary>
    public static AdminErrorCode QueryStepsInvalid { get; } = new("query_steps_invalid", 409, "The setting {field} is outside its allowed range.");

    /// <summary>
    /// V36 (#445): a <c>public</c> assistant with general knowledge other than <c>off</c> needs an explicit confirmation
    /// (<c>confirmGeneralKnowledge</c>) on publish, restore and a level change to <c>public</c>; a warning on draft save.
    /// </summary>
    public static AdminErrorCode GeneralKnowledgePublicUnconfirmed { get; } = new("general_knowledge_public_unconfirmed", 409,
        "General knowledge ({generalKnowledge}) on a public assistant is not recommended: citizens may get general answers that are wrong for Umeå kommun. Confirm to go ahead.");

    /// <summary>V15: a not-found link is not https on an allowed host, or there are more than 5.</summary>
    public static AdminErrorCode LinkHostNotAllowed { get; } = new("link_host_not_allowed", 409, "A link must be https on an allowed site, at most 5 links.");

    /// <summary>Generating test questions needs <c>Settings.EvaluationModel</c> (processing design §13.1: no implicit default).</summary>
    public static AdminErrorCode EvaluationModelUnset { get; } = new("evaluation_model_unset", 409, "No model is chosen for generating test questions.");

    /// <summary>Restore takes an earlier published version: not the draft and not the version that is live.</summary>
    public static AdminErrorCode VersionNotRestorable { get; } = new("version_not_restorable", 409, "Version {number} is the draft or the published version and cannot be restored.");

    /// <summary>V10: the history mode or the retention days are outside the level's data-protection policy.</summary>
    public static AdminErrorCode RetentionOutsidePolicy { get; } = new("retention_outside_policy", 409, "The level {level} allows history {modes} and at most {maxDays} days.");
    /// <summary>The retrieval test could not search: the database or the query embedding is unavailable (knowledge design §5.5).</summary>
    public static AdminErrorCode SearchUnavailable { get; } = new("search_unavailable", 503, "The search could not run right now. Try again.");

    /// <summary>The test panel's answer failed at the model (chat design §3.6: <c>{reason}</c>).</summary>
    public static AdminErrorCode TestAnswerFailed { get; } = new("test_answer_failed", 502, "The model could not answer the test question ({reason}). Try again.");

    /// <summary>V24: the pipeline's parameters are not valid for its template version.</summary>
    public static AdminErrorCode PipelineParamsInvalid { get; } = new("pipeline_params_invalid", 400, "The setting {param} is not valid for this processing: {message}");

    /// <summary>The plan is not ready (still running, failed or cancelled) or was made for another draft.</summary>
    public static AdminErrorCode PublicationNotReady { get; } = new("publication_not_ready", 409, "The plan is not ready. Wait for it or make a new one.");

    /// <summary>The pipeline changed after the plan was made (admin design §6.6): plan again.</summary>
    public static AdminErrorCode PublicationStale { get; } = new("publication_stale", 409, "The processing changed after the plan was made. Make a new plan.");

    /// <summary>Rollback takes a superseded version of this pipeline.</summary>
    public static AdminErrorCode VersionNotRollbackable { get; } = new("version_not_rollbackable", 409, "Only an earlier published version can be restored.");

    /// <summary>
    /// V32: a platform model setting (<c>{setting}</c>) names no <c>chat</c> row of the model mirror servable at the platform
    /// ceiling <c>{ceiling}</c> (<c>Eu</c> until M2).
    /// </summary>
    public static AdminErrorCode SettingsModelResidency { get; } = new("settings_model_residency", 409, "The model {model} cannot be used for {setting}: it is not a {kind} model the gateway serves at {ceiling}.");

    /// <summary>
    /// V37: a host's politeness profile in <c>Settings.Crawl.HostOverrides</c> (connectors design §6.6) has an unknown member,
    /// a value outside its range or the deployment's ceilings, <c>robots: ignore</c> without a reason, a bad User-Agent, or a
    /// host key that is not a canonical host without a port.
    /// </summary>
    public static AdminErrorCode CrawlPolitenessInvalid { get; } = new("crawl_politeness_invalid", 400, "The politeness setting {field} for {host} is invalid: {problem}");

    /// <summary>A file restore names a slot that is not deleted, past the 14-day grace, or whose bytes are gone (connectors §9.3).</summary>
    public static AdminErrorCode FileNotRestorable { get; } = new("file_not_restorable", 409, "The file {name} cannot be restored: it is not deleted, or its 14 days have passed.");

    /// <summary>
    /// A chunk set uses the embedding profile <c>{profile}</c>: only its <c>maxResidency</c> may change; a new model or dimension
    /// count is the profile migration (P3; Decisions "Editable embedding profiles before use").
    /// </summary>
    public static AdminErrorCode ProfileInUse { get; } = new("profile_in_use", 409, "The embedding profile {profile} is in use: only its residency ceiling can change.");

    /// <summary>The save's canary embedding with the new values failed (<c>{error}</c>); the problem's <c>test</c> holds the outcome.</summary>
    public static AdminErrorCode ProfileTestFailed { get; } = new("profile_test_failed", 422, "The profile was not saved: the test embedding failed ({error}).");

    /// <summary>V38: a member of <c>Settings.Chat.prompts</c> (<c>{field}</c>) breaks a rule (<c>{problem}</c>, chat design §7.3).</summary>
    public static AdminErrorCode PromptTextInvalid { get; } = new("prompt_text_invalid", 400, "The prompt text {field} is not valid: {problem}.");

    /// <summary>V39: the key is unknown, not usable (not active, owner inactive, or no residency in common with Ume.Rag's key) or of another department; <c>{reason}</c> says which.</summary>
    public static AdminErrorCode GatewayKeyInvalid { get; } = new("gateway_key_invalid", 409, "The AI gateway key {key} cannot be used: {reason}.");

    /// <summary>V39: every residency the key allows is above the object's ceiling.</summary>
    public static AdminErrorCode GatewayKeyResidency { get; } = new("gateway_key_residency", 409, "The AI gateway key {key} allows no residency within the ceiling {ceiling}.");

    /// <summary>V39: the key's model list lacks models the object uses; <c>affected</c> lists them.</summary>
    public static AdminErrorCode GatewayKeyModel { get; } = new("gateway_key_model", 409, "The AI gateway key {key} does not allow every model this uses.");

    /// <summary>
    /// V39 as the key (#720): the key allows <c>{model}</c>, but the model runs within the ceiling only at
    /// <c>{modelResidencies}</c> and the key allows none of them (<c>{keyResidencies}</c>); <c>affected</c> is the model.
    /// </summary>
    public static AdminErrorCode GatewayKeyModelResidency { get; } = new("gateway_key_model_residency", 409,
        "The AI gateway key {key} reaches {model} only at {modelResidencies}, which the key does not allow (it allows {keyResidencies}).");

    /// <summary><see cref="GatewayKeyModelResidency"/> for an embedding model, which an object uses as its knowledge base's embedding profile's (#720).</summary>
    public static AdminErrorCode GatewayKeyEmbeddingResidency { get; } = new("gateway_key_embedding_residency", 409,
        "The AI gateway key {key} reaches {model}, the knowledge base's embedding model, only at {modelResidencies}, which the key does not allow (it allows {keyResidencies}).");

    /// <summary>V39: the object has no key. A warning until the gateway acts as keys (feature <c>act-as-key</c>), then publish refuses it.</summary>
    public static AdminErrorCode GatewayKeyRequired { get; } = new("gateway_key_required", 409, "No AI gateway key is chosen: the assistant can't be published without one, and until then its calls are charged to the platform's owner team.");

    /// <summary>V39, a warning only: the key has a system prompt.</summary>
    public static AdminErrorCode GatewayKeySystemPrompt { get; } = new("gateway_key_system_prompt", 409, "The AI gateway key {key} adds its team's system prompt to every call, which changes the assistant's prompts.");

    /// <summary>A cost report's period beyond 400 days by day or 36 months by month (admin design §8.6).</summary>
    public static AdminErrorCode PeriodTooLong { get; } = new("period_too_long", 400, "The period is too long: at most 400 days by day, or 36 months by month.");

    /// <summary>The test panel's turn met an unusable gateway key (chat §3.6, B-44); <c>{reason}</c> is chat's message.</summary>
    public static AdminErrorCode TestAssistantUnavailable { get; } = new("test_assistant_unavailable", 503, "{reason}");

    /// <summary>The test panel's turn met a spent budget (chat §3.6, B-44); <c>{reason}</c> is the gateway's message, which names the scope.</summary>
    public static AdminErrorCode TestBudgetExceeded { get; } = new("test_budget_exceeded", 402, "{reason}");

    /// <summary>
    /// A sign-in challenge while rag-admin has no usable identity provider (identity design §19): no effective authority, an
    /// invalid one, or a stored client secret it cannot read. <c>/bff/user</c> reports the same as <c>signIn.configured</c>.
    /// </summary>
    public static AdminErrorCode SignInNotConfigured { get; } = new("sign_in_not_configured", 409, "Sign-in is not set up yet.");

    /// <summary>A wrong setup code (identity design §19). It never voids the code.</summary>
    public static AdminErrorCode SetupCodeInvalid { get; } = new("setup_code_invalid", 400, "The setup code is not right.");

    /// <summary>More than 20 code checks in a minute on this instance; answered 429 with <c>Retry-After</c> to the minute's end.</summary>
    public static AdminErrorCode SetupAttemptsExhausted { get; } = new("setup_attempts_exhausted", 429,
        "Too many setup code attempts. Wait a minute and try again.");

    /// <summary>A setup route that needs the setup session: none, an expired one, or one of an earlier opening.</summary>
    public static AdminErrorCode SetupSessionRequired { get; } = new("setup_session_required", 401, "Enter the setup code first.");

    /// <summary>
    /// A test sign-in could not start (identity design §19); <c>{reason}</c> is the step: <c>authority_invalid</c>,
    /// <c>authority_unreachable</c>, <c>tls</c>, <c>redirected</c>, <c>not_discovery</c>, <c>issuer_mismatch</c>,
    /// <c>insecure_endpoint</c>, <c>par_required</c>, <c>keys_unavailable</c>, <c>stored_secret_unreadable</c>, <c>secret_required</c>
    /// or <c>deployment_secret_without_authority</c>.
    /// </summary>
    public static AdminErrorCode SetupTestRefused { get; } = new("setup_test_refused", 400, "The test sign-in could not start ({reason}).");

    /// <summary>
    /// Saving needs the session's latest test, passed, by a platform admin, with exactly the submitted values; <c>{reason}</c> is
    /// <c>unknown_test</c>, <c>not_latest</c>, <c>not_passed</c>, <c>not_platform_admin</c> or <c>values_changed</c>.
    /// </summary>
    public static AdminErrorCode SetupTestRequired { get; } = new("setup_test_required", 409,
        "Saving needs a passed test sign-in by a platform admin with exactly these values ({reason}).");

    public static IReadOnlyList<AdminErrorCode> All { get; } =
        [InvalidValue, PlatformRule, NotFound, ConcurrentChange, AlreadyExists, InUse, ConfigBusy, SyncOff, SyncFailed, ModelResidency, ModelNotListed,
            GatewayUnavailable, GatewayKeyMissing, PolicyRule, NotInScope, NoEmbeddingProfile, CollectionInUse, CollectionLevelConflict, AssistantBelowCollection,
            RetrievalSettingsInvalid, SourceAboveCollection, SourceSettingsInvalid, ImpactNotConfirmed, TemplateUnsupported, AdminOnly, RunNotActive, ReasonTooShort, DeletionSuperseded, PiiNotFlagged,
            NotAnUploadSource, UploadFormatDeferred, UploadTypeRefused, UploadTooLarge, UploadFileExists, UploadFileMissing, UploadBatchTooLarge,
            SourcePaused, SlugTaken, UploadBatchCommitted, KeyNotRotatable, ClientManagedByDeployment, KeyRevoked, KeySecretUnavailable, KeyScopeInvalid, KeyScopeRequired,
            KeysUnavailable, NoEvaluationSet, QuestionNotHuman, QuestionEditPending, SiteProfileInvalid, SiteProfileVersionApplied, NoDepartment,
            NoDraft, DraftIsOnlyVersion, DraftBaseChanged, AssistantContactMissing, VersionLinkMissing, TooManyCollections, CollectionNotShared, TopicInvalid, CollectionSharedInUse,
            AudienceAboveCeiling, ClientStoresHistory, QueryStepsInvalid, GeneralKnowledgePublicUnconfirmed, LinkHostNotAllowed, EvaluationModelUnset, SearchUnavailable, VersionNotRestorable, RetentionOutsidePolicy, TestAnswerFailed, PipelineParamsInvalid, PublicationNotReady, PublicationStale, VersionNotRollbackable, SettingsModelResidency, CrawlPolitenessInvalid, FileNotRestorable, ProfileInUse, ProfileTestFailed, PromptTextInvalid,
            GatewayKeyInvalid, GatewayKeyResidency, GatewayKeyModel, GatewayKeyModelResidency, GatewayKeyEmbeddingResidency, GatewayKeyRequired, GatewayKeySystemPrompt, PeriodTooLong, TestAssistantUnavailable, TestBudgetExceeded, SignInNotConfigured,
            SetupCodeInvalid, SetupAttemptsExhausted, SetupSessionRequired, SetupTestRefused, SetupTestRequired];
}
