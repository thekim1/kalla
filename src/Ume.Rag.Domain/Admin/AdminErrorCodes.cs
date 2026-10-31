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

    public static IReadOnlyList<AdminErrorCode> All { get; } =
        [InvalidValue, PlatformRule, NotFound, ConcurrentChange, AlreadyExists, InUse, ConfigBusy, SyncOff, SyncFailed, ModelResidency, ModelNotListed,
            GatewayUnavailable, PolicyRule];
}
