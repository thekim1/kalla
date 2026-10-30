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

    public static ILogger CreateLogger(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateLogger(Category);
    }

    [LoggerMessage(5004, LogLevel.Warning,
        "Security-relevant decisions refused: config not confirmed since {LastConfirmedAt} (cached version {CachedVersion}) on {Service}, x{Count}",
        EventName = "rag.config.stale_refused")]
    public static partial void ConfigStaleRefused(ILogger logger, string service, long cachedVersion, DateTimeOffset? lastConfirmedAt, int count);
}
