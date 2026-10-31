using Microsoft.Extensions.Logging;

namespace Ume.Rag.Infrastructure.Security;

/// <summary>
/// Security events for SIEMs (identity design §10, as the gateway's <c>SecurityEvents</c>): everything is logged under
/// one category so an OpenTelemetry Collector can route it separately from operational logs. Event ids, names and
/// attribute names are a public contract: add, never rename or remove. Never log key material or content here.
/// The full table follows with <c>docs/security-events.md</c> (P0 #19); events are added with the code that raises them.
/// </summary>
public static partial class SecurityEvents
{
    public const string Category = "Ume.Rag.Security";

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
    [LoggerMessage(5001, LogLevel.Warning,
        "Authentication failed: {Reason} ({Credential}) on {Endpoint} from {SourceAddress}", EventName = "rag.auth.failed")]
    public static partial void AuthFailed(ILogger logger, string reason, string credential, string endpoint, string? sourceAddress);

    [LoggerMessage(5003, LogLevel.Warning,
        "Gateway answer discarded: residency {Observed} above the ceiling {Ceiling} on {Endpoint} (gateway request {GatewayRequestId})",
        EventName = "rag.residency.refused")]
    public static partial void ResidencyRefused(ILogger logger, string ceiling, string observed, string endpoint, string? gatewayRequestId);

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
}
