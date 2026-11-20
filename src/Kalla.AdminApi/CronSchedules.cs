using Cronos;

namespace Kalla.AdminApi;

/// <summary>
/// Schedules as the worker's scheduler reads them (engine design §9.6, to-verify #16): Cronos, 5 fields
/// (<see cref="CronFormat.Standard"/>). The scheduler skips a schedule that does not parse without a word, so the admin
/// refuses it when it is saved.
/// </summary>
public static class CronSchedules
{
    public const string Invalid = "The schedule must be a cron expression of 5 fields (minute hour day-of-month month day-of-week).";

    public static bool IsValid(string schedule) => CronExpression.TryParse(schedule, CronFormat.Standard, out _);
}
