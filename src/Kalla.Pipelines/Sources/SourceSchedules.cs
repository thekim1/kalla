using System.Security.Cryptography;
using System.Text.Json;
using Cronos;
using Kalla.Domain;

namespace Kalla.Pipelines.Sources;

/// <summary>A source's next scheduled sync: the cron occurrence it is for, and when it starts (the occurrence plus the jitter).</summary>
public readonly record struct ScheduledRun(DateTimeOffset Occurrence, DateTimeOffset At);

/// <summary>
/// When a source is fetched again (engine design §9.6), once for the worker's scheduler (<c>SyncScheduler</c>) and the
/// admin's next-run row (#935): the schedule in force, its next occurrence in Europe/Stockholm with Cronos (5 fields,
/// <see cref="CronFormat.Standard"/>), and the deterministic per-source jitter. A schedule that does not parse is never due;
/// the admin refuses one when it is saved (to-verify #16).
/// </summary>
public static class SourceSchedules
{
    /// <summary>The design's defaults, used when <c>Settings.DefaultSchedules</c> names no type at all (engine design §9.6).</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SourceTypes.UrlList] = "0 2 * * *",
        [SourceTypes.Crawl] = "0 1 * * 6",
    };

    /// <summary>Whether <paramref name="schedule"/> is a 5-field cron expression the scheduler can run.</summary>
    public static bool IsValid(string schedule) => CronExpression.TryParse(schedule, CronFormat.Standard, out _);

    /// <summary>
    /// The defaults by type from the stored <c>Settings.DefaultSchedules</c> (a JSON object): the setting when it names at
    /// least one type, else <see cref="Defaults"/>. Malformed JSON throws <see cref="JsonException"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadDefaults(string? json)
    {
        var stored = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return stored is { Count: > 0 } ? stored : Defaults;
    }

    /// <summary>The schedule a source runs on: its own, else the default for its type; an upload has no default.</summary>
    public static string? InForce(string type, string? own, IReadOnlyDictionary<string, string> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return own ?? (type == SourceTypes.Upload ? null : defaults.GetValueOrDefault(type));
    }

    /// <summary>
    /// The source's next scheduled sync: the next occurrence after its last scheduled run (else after its
    /// <c>CreatedAt</c>, never its <c>UpdatedAt</c>, which every edit moves), with missed occurrences collapsed into the
    /// latest one whose start has passed, so an outage gives one run. Due when <see cref="ScheduledRun.At"/> is not after
    /// <paramref name="now"/>. DST gaps run once at the next valid time, overlaps once (Cronos). Null without a schedule or
    /// with one that does not parse.
    /// </summary>
    public static ScheduledRun? Next(string? schedule, Guid sourceId, int jitterMinutes, DateTimeOffset createdAt, DateTimeOffset? lastScheduledFor,
        DateTimeOffset now)
    {
        if (schedule is null || !CronExpression.TryParse(schedule, CronFormat.Standard, out var cron) ||
            cron.GetNextOccurrence(lastScheduledFor ?? createdAt, StockholmTime.Zone) is not { } next)
        {
            return null;
        }

        var jitter = Jitter(sourceId, jitterMinutes);
        while (cron.GetNextOccurrence(next, StockholmTime.Zone) is { } later && later + jitter <= now)
        {
            next = later;
        }

        return new ScheduledRun(next, next + jitter);
    }

    /// <summary>
    /// Deterministic per source, so sources on the same schedule don't all start at once: the first eight bytes of SHA-256
    /// of the id, modulo <c>Settings.ScheduleJitterMinutes</c>.
    /// </summary>
    public static TimeSpan Jitter(Guid sourceId, int jitterMinutes)
    {
        if (jitterMinutes <= 0)
        {
            return TimeSpan.Zero;
        }

        var hash = SHA256.HashData(sourceId.ToByteArray());
        return TimeSpan.FromMinutes((double)(BitConverter.ToUInt64(hash, 0) % (ulong)jitterMinutes));
    }
}
