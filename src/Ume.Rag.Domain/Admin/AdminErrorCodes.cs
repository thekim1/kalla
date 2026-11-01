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

    public static IReadOnlyList<AdminErrorCode> All { get; } =
        [InvalidValue, PlatformRule, NotFound, ConcurrentChange, AlreadyExists, InUse, ConfigBusy, SyncOff, SyncFailed, ModelResidency, ModelNotListed,
            GatewayUnavailable, PolicyRule, NotInScope, NoEmbeddingProfile, CollectionInUse, CollectionLevelConflict, AssistantBelowCollection,
            RetrievalSettingsInvalid];
}
