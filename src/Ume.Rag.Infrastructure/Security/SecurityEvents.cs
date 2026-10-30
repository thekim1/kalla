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

    [LoggerMessage(6002, LogLevel.Information,
        "Admin {Actor} signed in from {SourceAddress} with roles {Roles}, departments {Departments}; session {SessionId}", EventName = "rag.admin.signin")]
    public static partial void AdminSignIn(ILogger logger, string actor, string? sourceAddress, string roles, string departments, string? sessionId);

    [LoggerMessage(6003, LogLevel.Information,
        "Admin {Actor} signed out from {SourceAddress}; session {SessionId}", EventName = "rag.admin.signout")]
    public static partial void AdminSignOut(ILogger logger, string actor, string? sourceAddress, string? sessionId);

    [LoggerMessage(6005, LogLevel.Information,
        "Admin {Actor} extended session {SessionId} until {ExpiresAt} from {SourceAddress}", EventName = "rag.admin.session.extended")]
    public static partial void AdminSessionExtended(ILogger logger, string actor, string? sessionId, DateTimeOffset expiresAt, string? sourceAddress);
}
