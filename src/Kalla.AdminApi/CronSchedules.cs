using Kalla.Pipelines.Sources;

namespace Kalla.AdminApi;

/// <summary>
/// The save-time check of a schedule (engine design §9.6, to-verify #16): the worker's scheduler skips a schedule that does
/// not parse without a word, so the admin refuses it when it is saved. Which schedule is in force and when the next run
/// starts are the shared rule's (<see cref="SourceSchedules"/>, A-43), which the worker's scheduler uses too.
/// </summary>
public static class CronSchedules
{
    public const string Invalid = "The schedule must be a cron expression of 5 fields (minute hour day-of-month month day-of-week).";

    public static bool IsValid(string schedule) => SourceSchedules.IsValid(schedule);
}
