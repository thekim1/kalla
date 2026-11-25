namespace Kalla.AdminApi.Tests;

/// <summary>
/// The save-time check of a schedule (engine design §9.6, to-verify #16). The schedule in force, the next run and the
/// jitter are the shared rule's, tested in <c>Kalla.Pipelines.Tests</c> (<c>SourceSchedulesTests</c>, A-43); the source
/// page's use of them is in <c>SourceEndpointsTests</c>.
/// </summary>
public sealed class CronSchedulesTests
{
    [Theory]
    [InlineData("0 2 * * *", true)]
    [InlineData("*/15 6-18 * * 1-5", true)]
    [InlineData("0 0 2 * * *", false)] // six fields: seconds are not a scheduler field
    [InlineData("not cron", false)]
    [InlineData("", false)]
    public void A_schedule_is_saved_only_when_the_scheduler_can_run_it(string schedule, bool valid) => CronSchedules.IsValid(schedule).ShouldBe(valid);
}
