using Microsoft.Extensions.Logging;

namespace Kalla.Infrastructure.Security;

/// <summary>
/// Security events for SIEMs (identity design §10, as the gateway's <c>SecurityEvents</c>): everything is logged under
/// one category so an OpenTelemetry Collector can route it separately from operational logs. Event ids, names and
/// attribute names are a public contract: add, never rename or remove. Never log key material or content here.
/// The full table follows with <c>docs/security-events.md</c> (P0 #19); events are added with the code that raises them.
/// </summary>
public static partial class SecurityEvents
{
    public const string Category = "Kalla.Security";

    private static readonly System.Buffers.SearchValues<char> ControlCharacters = System.Buffers.SearchValues.Create(
        [.. Enumerable.Range(0, 0x20).Select(c => (char)c), .. Enumerable.Range(0x7F, 0x21).Select(c => (char)c)]);

    /// <summary>
    /// <paramref name="value"/> with control characters replaced by '?', for values from outside that are logged (copied
    /// from the gateway): a newline could otherwise start a forged log line in a plain-text log.
    /// </summary>
    public static string Printable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsSpan().IndexOfAny(ControlCharacters) < 0
            ? value
            : string.Create(value.Length, value, static (span, v) =>
            {
                v.AsSpan().CopyTo(span);
                int i;
                while ((i = span.IndexOfAny(ControlCharacters)) >= 0)
                {
                    span[i] = '?';
                    span = span[(i + 1)..];
                }
            });
    }

    public static ILogger CreateLogger(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateLogger(Category);
    }

    /// <summary>A refused credential; for the admin, one per refused sign-in (credential <c>oidc-signin</c>).</summary>
    /// The api aggregates: one event per bucket (reason, credential, endpoint, key, address) per minute with the refusals it
    /// stands for (identity design §6.3); the presented value is never logged.
    [LoggerMessage(5001, LogLevel.Warning,
        "Authentication failed: {Reason} ({Credential}) on {Endpoint} from {SourceAddress}, key {KeyId} {KeyPrefix} of client {ClientId}, x{Count} from {FirstSeen} to {LastSeen}",
        EventName = "rag.auth.failed")]
    public static partial void AuthFailed(ILogger logger, string reason, string credential, string endpoint, string? sourceAddress, Guid? keyId,
        string? keyPrefix, Guid? clientId, int count, DateTimeOffset firstSeen, DateTimeOffset lastSeen);

    /// <summary>5002: an access-policy denial on an explicit request, at most once a minute per subject, reason and resource.</summary>
    [LoggerMessage(5002, LogLevel.Warning,
        "Access denied: {Reason} for {Action} on {ResourceType} {ResourceId} by {SubjectKind} {SubjectId} (client {ClientId}, key {KeyId}, config {ConfigVersion}) from {SourceAddress}, x{Count}",
        EventName = "rag.access.denied")]
    public static partial void AccessDenied(ILogger logger, string reason, string action, string resourceType, string resourceId, string subjectKind,
        string subjectId, Guid? clientId, Guid? keyId, long configVersion, string? sourceAddress, int count);

    /// <summary>5005: a ceiling denial (identity §4.3 step 7), instead of 5002, with the levels.</summary>
    [LoggerMessage(5005, LogLevel.Warning,
        "Ceiling refused: {ResourceType} {ResourceId} at {ResourceLevel} above client ceiling {ClientCeiling} for {Action} by {SubjectKind} {SubjectId} (client {ClientId}, key {KeyId}, config {ConfigVersion}) from {SourceAddress}, reason {Reason}, x{Count}",
        EventName = "rag.ceiling.refused")]
    public static partial void CeilingRefused(ILogger logger, string resourceType, string resourceId, string resourceLevel, string clientCeiling, string action,
        string subjectKind, string subjectId, Guid? clientId, Guid? keyId, long configVersion, string? sourceAddress, string reason, int count);

    /// <summary>5006: an address crossed <c>Api:AuthFailuresPerMinute</c>, once per address per window.</summary>
    [LoggerMessage(5006, LogLevel.Warning, "Address {SourceAddress} throttled on {Endpoint} after {Count} refused credentials in a minute",
        EventName = "rag.auth.throttled")]
    public static partial void AuthThrottled(ILogger logger, string? sourceAddress, string endpoint, int count);

    /// <summary>
    /// The source address as events record it (<c>Api:Security:SourceAddress</c>): <c>Full</c>, or <c>Truncated</c> (default)
    /// to the IPv4 /24 or IPv6 /64 network, so events identify a network, not a person's device.
    /// </summary>
    public static string? SourceAddress(System.Net.IPAddress? address, bool truncated = true)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!truncated)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        var keep = bytes.Length == 4 ? 3 : 8;
        for (var i = keep; i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        return new System.Net.IPAddress(bytes) + (bytes.Length == 4 ? "/24" : "/64");
    }

    [LoggerMessage(5003, LogLevel.Warning,
        "Gateway answer discarded: residency {Observed} above the ceiling {Ceiling} on {Endpoint} (gateway request {GatewayRequestId})",
        EventName = "rag.residency.refused")]
    public static partial void ResidencyRefused(ILogger logger, string ceiling, string observed, string endpoint, string? gatewayRequestId);

    /// <summary>
    /// 5011 (identity design §10, gateway-changes §5.2): a <c>2xx</c> or <c>304</c> answer to a call made as an assigned key
    /// lacks the <c>x-fors-on-behalf-of</c> echo of the key id sent, or echoes another; the call was aborted before use.
    /// </summary>
    [LoggerMessage(5011, LogLevel.Warning,
        "Gateway call {Step} aborted: no echo of the gateway key {GatewayKeyId} (assistant {AssistantId}, collection {CollectionId}, gateway request {GatewayRequestId})",
        EventName = "rag.gateway.delegation_missing")]
    public static partial void DelegationMissing(ILogger logger, string? step, Guid? assistantId, Guid? collectionId, Guid gatewayKeyId, string? gatewayRequestId);

    [LoggerMessage(5004, LogLevel.Warning,
        "Security-relevant decisions refused: config not confirmed since {LastConfirmedAt} (cached version {CachedVersion}) on {Service}, x{Count}",
        EventName = "rag.config.stale_refused")]
    public static partial void ConfigStaleRefused(ILogger logger, string service, long cachedVersion, DateTimeOffset? lastConfirmedAt, int count);

    /// <summary>
    /// A fetch refused by the egress guard (connectors design §7), at most once a minute per (source, reason) with the count.
    /// <paramref name="hostHash"/> is the hex HMAC of the host name under <c>Security:CrawlKeyPepper</c>; <paramref name="host"/>
    /// is set only for <c>public</c> sources.
    /// </summary>
    [LoggerMessage(5101, LogLevel.Warning,
        "Egress blocked: {Reason} ({AddressClass}) for {Purpose} of source {SourceId} run {RunId} ({Classification}) host {HostHash} {Host}, x{Count}",
        EventName = "rag.egress.blocked")]
    public static partial void EgressBlocked(ILogger logger, string reason, string? addressClass, string purpose, Guid sourceId,
        Guid? runId, string classification, string hostHash, string? host, int count);

    /// <summary>ClamAV found malware in a fetched or uploaded file; the file is quarantined (connectors design §11).</summary>
    [LoggerMessage(5102, LogLevel.Warning,
        "Malware detected: {Signature} in {Sha256} ({Origin}) of source {SourceId} run {RunId} document {DocumentId} ({Classification})",
        EventName = "rag.malware.detected")]
    public static partial void MalwareDetected(ILogger logger, string signature, string sha256, string origin, Guid sourceId,
        Guid? runId, Guid? documentId, string classification);

    /// <summary>A published pipeline version failed validation and was not run (engine design §4); one per refused version.</summary>
    [LoggerMessage(5105, LogLevel.Warning,
        "Pipeline refused: version {PipelineVersionId} of pipeline {PipelineId} failed validation: {Reason}",
        EventName = "rag.pipeline.refused")]
    public static partial void PipelineRefused(ILogger logger, Guid pipelineId, Guid pipelineVersionId, string reason);

    /// <summary>
    /// The worker started with <c>ClamAv:Mode = off</c>: files are stored without a malware scan (connectors design §11).
    /// Once at every start.
    /// </summary>
    [LoggerMessage(5106, LogLevel.Warning,
        "Malware scan is off (ClamAv:Mode = off): files are stored without a scan",
        EventName = "rag.malware.scan_off")]
    public static partial void MalwareScanOff(ILogger logger);

    /// <summary>Every audit entry the admin writes (identity design §10).</summary>
    [LoggerMessage(6001, LogLevel.Information,
        "Admin {Actor}: {Action} {EntityType} {EntityId}", EventName = "rag.admin.change")]
    public static partial void AdminChange(ILogger logger, string actor, string action, string entityType, string? entityId);

    [LoggerMessage(6002, LogLevel.Information,
        "Admin {Actor} signed in from {SourceAddress} with roles {Roles}, departments {Departments}; session {SessionId}", EventName = "rag.admin.signin")]
    public static partial void AdminSignIn(ILogger logger, string actor, string? sourceAddress, string roles, string departments, string? sessionId);

    [LoggerMessage(6003, LogLevel.Information,
        "Admin {Actor} signed out from {SourceAddress}; session {SessionId}", EventName = "rag.admin.signout")]
    public static partial void AdminSignOut(ILogger logger, string actor, string? sourceAddress, string? sessionId);

    [LoggerMessage(6005, LogLevel.Information,
        "Admin {Actor} extended session {SessionId} until {ExpiresAt} from {SourceAddress}", EventName = "rag.admin.session.extended")]
    public static partial void AdminSessionExtended(ILogger logger, string actor, string? sessionId, DateTimeOffset expiresAt, string? sourceAddress);

    /// <summary>A 403 for a signed-in user, at most once a minute per user with the count since the last.</summary>
    [LoggerMessage(6006, LogLevel.Warning,
        "Admin {Actor} was refused {Method} {Path} x{Count} from {SourceAddress} (roles {Roles})", EventName = "rag.admin.access.denied")]
    public static partial void AdminAccessDenied(ILogger logger, string actor, string method, string path, int count, string? sourceAddress, string roles);

    /// <summary>A read of a sensitive list (audit log, keys, feedback, access log, conversation views).</summary>
    [LoggerMessage(6007, LogLevel.Information,
        "Admin {Actor} read {Path}{Query} from {SourceAddress}", EventName = "rag.admin.data.read")]
    public static partial void AdminDataRead(ILogger logger, string actor, string path, string query, string? sourceAddress);

    /// <summary>Setup mode opened (identity design §19): <c>first_run</c> or <c>reopen</c>. Never the setup code.</summary>
    [LoggerMessage(6008, LogLevel.Warning,
        "Setup mode opened ({Kind}) until {EndsAt}", EventName = "rag.admin.setup.opened")]
    public static partial void SetupOpened(ILogger logger, string kind, DateTimeOffset endsAt);

    /// <summary>Wrong setup codes, sampled: at most one event per 10 seconds per instance, <c>Count</c> the wrong codes since the last one.</summary>
    [LoggerMessage(6009, LogLevel.Warning,
        "Setup code refused from {SourceAddress}: {Count} wrong codes since the last event", EventName = "rag.admin.setup.code_refused")]
    public static partial void SetupCodeRefused(ILogger logger, string? sourceAddress, int count);

    /// <summary>A setup session started for a correct setup code.</summary>
    [LoggerMessage(6010, LogLevel.Warning,
        "Setup session {SessionId} started from {SourceAddress} until {ExpiresAt}", EventName = "rag.admin.setup.session")]
    public static partial void SetupSessionStarted(ILogger logger, string sessionId, string? sourceAddress, DateTimeOffset expiresAt);

    /// <summary>
    /// A setup test finished: <c>Target</c> <c>rag-admin</c> (a test sign-in, with the tested person's subject) or <c>open-webui</c>
    /// (a back-channel test, no person); <c>Result</c> <c>passed</c> or <c>failed</c> with its reason. Callbacks with an unknown test
    /// are sampled like 6009, <c>Count</c> the number since the last event (1 otherwise).
    /// </summary>
    [LoggerMessage(6011, LogLevel.Warning,
        "Setup test of {Target} {Result} ({Reason}) for {Subject} as platform admin {PlatformAdmin}; setup session {SessionId} from {SourceAddress}, x{Count}",
        EventName = "rag.admin.setup.test")]
    public static partial void SetupTestSignIn(ILogger logger, string target, string result, string reason, string? subject, bool platformAdmin, string? sessionId,
        string? sourceAddress, int count);

    /// <summary>Setup completed: the sign-in values the tested person signed in with are saved and setup is closed.</summary>
    [LoggerMessage(6012, LogLevel.Warning,
        "Setup completed by {Actor} in setup session {SessionId} from {SourceAddress}", EventName = "rag.admin.setup.completed")]
    public static partial void SetupCompleted(ILogger logger, string actor, string? sessionId, string? sourceAddress);

    /// <summary>
    /// Open WebUI's sign-in values saved in setup mode (identity design §19), setup still open; <c>Actor</c> is the person of the session's
    /// passed rag-admin test, <c>ClientVerified</c> whether the test confirmed the client's credentials. Never a value.
    /// </summary>
    [LoggerMessage(6013, LogLevel.Warning,
        "Open WebUI's sign-in values saved by {Actor} in setup session {SessionId} from {SourceAddress}; client verified {ClientVerified}",
        EventName = "rag.admin.setup.openwebui_saved")]
    public static partial void SetupOpenWebUiSaved(ILogger logger, string actor, string? sessionId, string? sourceAddress, bool clientVerified);
}
