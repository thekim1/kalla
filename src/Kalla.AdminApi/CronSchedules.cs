using System.Security.Cryptography;
using System.Text.Json;
using Cronos;
using Kalla.Domain;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// Schedules as the worker's scheduler reads them (engine design §9.6, to-verify #16): Cronos, 5 fields
/// (<see cref="CronFormat.Standard"/>). The scheduler skips a schedule that does not parse without a word, so the admin
/// refuses it when it is saved. <see cref="InForce"/> and <see cref="NextRun"/> repeat the scheduler's rule
/// (<c>Kalla.Worker.Engine.SyncScheduler.Due</c> and <c>Jitter</c>, <c>EngineSettings.DefaultSchedules</c>) so the source page
/// can say when the next scheduled run starts; a change there changes these too.
/// </summary>
public static class CronSchedules
{
    public const string Invalid = "The schedule must be a cron expression of 5 fields (minute hour day-of-month month day-of-week).";

    /// <summary>The design's defaults when <c>Settings.DefaultSchedules</c> names none (engine design §9.6).</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SourceTypes.UrlList] = "0 2 * * *",
        [SourceTypes.Crawl] = "0 1 * * 6",
    };

    public static bool IsValid(string schedule) => CronExpression.TryParse(schedule, CronFormat.Standard, out _);

    /// <summary>
    /// The schedule a source runs on: its own, else <c>Settings.DefaultSchedules</c> for its type (the design's defaults
    /// when the setting names no type at all, as the worker reads it); uploads never.
    /// </summary>
    public static string? InForce(Source source, PlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (source.Schedule is { } own)
        {
            return own;
        }

        if (source.Type == SourceTypes.Upload)
        {
            return null;
        }

        var defaults = ParseDefaults(settings.DefaultSchedules);
        return (defaults.Count > 0 ? defaults : Defaults).GetValueOrDefault(source.Type);
    }

    /// <summary>
    /// When the scheduler enqueues the source's next scheduled sync: the next occurrence after the last scheduled run (else
    /// after <c>CreatedAt</c>), missed ones collapsed into the latest that has passed, plus the per-source jitter. A time
    /// not after <paramref name="now"/> means it is due (it starts at the next scheduler tick, or when the running run
    /// ends). Null for a paused source or one without a schedule.
    /// </summary>
    public static DateTimeOffset? NextRun(Source source, string? schedule, int jitterMinutes, DateTimeOffset? lastScheduledFor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.State != SourceStates.Active || schedule is null || !CronExpression.TryParse(schedule, CronFormat.Standard, out var cron) ||
            cron.GetNextOccurrence(lastScheduledFor ?? source.CreatedAt, StockholmTime.Zone) is not { } next)
        {
            return null;
        }

        var jitter = Jitter(source.Id, jitterMinutes);
        while (cron.GetNextOccurrence(next, StockholmTime.Zone) is { } later && later + jitter <= now)
        {
            next = later;
        }

        return next + jitter;
    }

    /// <summary>Deterministic per source: the first eight bytes of SHA-256 of the id, modulo the jitter minutes (as the worker).</summary>
    public static TimeSpan Jitter(Guid sourceId, int jitterMinutes)
    {
        if (jitterMinutes <= 0)
        {
            return TimeSpan.Zero;
        }

        var hash = SHA256.HashData(sourceId.ToByteArray());
        return TimeSpan.FromMinutes((double)(BitConverter.ToUInt64(hash, 0) % (ulong)jitterMinutes));
    }

    /// <summary>As the worker reads the setting (<c>EngineSettings.ReadAsync</c>).</summary>
    private static Dictionary<string, string> ParseDefaults(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? [];
}
