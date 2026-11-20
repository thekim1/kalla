namespace Kalla.Domain;

/// <summary>
/// The platform's local time, Europe/Stockholm: the day of the usage and statistics tables (data-model §2.3), of cost
/// periods and of retention, and the zone of the nightly jobs and schedules.
/// </summary>
public static class StockholmTime
{
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    /// <summary>The Stockholm day of <paramref name="at"/>.</summary>
    public static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);

    /// <summary>Today in Stockholm by <paramref name="clock"/>.</summary>
    public static DateOnly Today(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return DayOf(clock.GetUtcNow());
    }
}
