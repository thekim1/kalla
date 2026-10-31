using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.ServiceDefaults;

namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// The config version this process has applied and when the database last confirmed it (data-model §5). Written by
/// <see cref="ConfigVersionMonitor"/>; read by every security-relevant decision through <see cref="EnsureConfirmed"/>,
/// by readiness and by the log scope.
/// </summary>
public sealed class ConfigVersionState(IOptions<ConfigOptions> options, TimeProvider time, IHostEnvironment environment, ILoggerFactory loggers)
{
    /// <summary>The <c>code</c> of the 503 refusal (identity design §4.3 step 0).</summary>
    public const string UnconfirmedCode = "config_unconfirmed";

    private static readonly TimeSpan EventInterval = TimeSpan.FromMinutes(1);
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);
    private readonly Lock _gate = new();
    private long _version = -1;
    private DateTimeOffset? _lastConfirmedAt;
    private int _refusedSinceEvent;
    private DateTimeOffset _lastEventAt = DateTimeOffset.MinValue;

    /// <summary>The applied config version; -1 before the first load.</summary>
    public long Version => Volatile.Read(ref _version);

    public DateTimeOffset? LastConfirmedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastConfirmedAt;
            }
        }
    }

    public TimeSpan MaxUnconfirmed => TimeSpan.FromSeconds(options.Value.MaxUnconfirmedSeconds);

    /// <summary>Confirmed within the grace window, so security-relevant decisions may use the cached snapshot.</summary>
    public bool IsConfirmed => LastConfirmedAt is { } at && time.GetUtcNow() - at <= MaxUnconfirmed;

    /// <summary>
    /// Throws a 503 <c>config_unconfirmed</c> fault when the snapshot is outside the grace window; call before every
    /// security-relevant decision (key authentication, any access decision). Raises security event 5004 at most once a
    /// minute with the number of refusals since the last one.
    /// </summary>
    public void EnsureConfirmed()
    {
        if (IsConfirmed)
        {
            return;
        }

        RecordStaleRefusal();
        throw new ApiFaultException(StatusCodes.Status503ServiceUnavailable,
            "The service cannot confirm its configuration. Try again shortly.",
            new Dictionary<string, object?> { ["code"] = UnconfirmedCode });
    }

    /// <summary>
    /// Counts a <c>config_unconfirmed</c> refusal made elsewhere (the knowledge role's own 503): event 5004 at most once a
    /// minute with the refusals since the last one, as <see cref="EnsureConfirmed"/> does.
    /// </summary>
    public void RecordStaleRefusal()
    {
        int count;
        DateTimeOffset? lastConfirmedAt;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            _refusedSinceEvent++;
            lastConfirmedAt = _lastConfirmedAt;
            if (now - _lastEventAt < EventInterval)
            {
                count = 0;
            }
            else
            {
                count = _refusedSinceEvent;
                _refusedSinceEvent = 0;
                _lastEventAt = now;
            }
        }

        if (count > 0)
        {
            SecurityEvents.ConfigStaleRefused(_security, environment.ApplicationName, Version, lastConfirmedAt, count);
        }
    }

    /// <summary>The database confirmed <paramref name="version"/> (applied, or unchanged since the last check).</summary>
    internal void Confirm(long version)
    {
        lock (_gate)
        {
            Volatile.Write(ref _version, version);
            _lastConfirmedAt = time.GetUtcNow();
        }
    }
}
