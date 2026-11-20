using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kalla.Infrastructure.Config;

/// <summary>
/// The config version lock wait on meter <c>Kalla</c> (admin design §4.4, §15; to-verify #23): how long each config
/// write waited for <c>SELECT … FOR UPDATE</c> on <c>config."ConfigVersion"</c>, including the waits that end in
/// <c>config_busy</c> after <see cref="ConfigWrites.LockTimeout"/>. One measurement per attempt, no tags.
/// </summary>
public sealed class ConfigLockMetrics
{
    /// <summary>The histogram's name.</summary>
    public const string WaitName = "rag.admin.config_lock.wait";

    private readonly Histogram<double> _wait;

    public ConfigLockMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _wait = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
            .CreateHistogram<double>(WaitName, "s", "Time an admin config write waited for the config version lock.");
    }

    /// <summary>Records the wait since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    internal void Record(long startTimestamp) => _wait.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
}
