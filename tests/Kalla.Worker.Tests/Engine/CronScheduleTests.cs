using System.Globalization;
using Cronos;
using Kalla.Domain;
using Kalla.Worker.Engine;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// to-verify #16 (engine design §9.6, §23): the pinned Cronos (0.13.0) as the scheduler uses it, 5 fields
/// (<see cref="CronFormat.Standard"/>) in Europe/Stockholm. On the spring-forward night (2027-03-28, 02:00 CET → 03:00 CEST)
/// a 02:30 schedule runs once, at 03:00 CEST; on the fall-back night (2027-10-31, 03:00 CEST → 02:00 CET) it runs once, at the
/// first 02:30 (CEST). An interval expression (a <c>*</c>, range or step in the minute or hour field) runs at both 02:30s.
/// A 6-field or otherwise unparsable schedule (Cronos's <c>H</c> as well: it needs a seed) is never due; the admin refuses one
/// when it is saved (<c>AdminRequestValidationTests</c>).
/// </summary>
public sealed class CronScheduleTests
{
    private static readonly CronExpression HalfPastTwo = CronExpression.Parse("30 2 * * *", CronFormat.Standard);

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void A_2_30_schedule_runs_at_3_00_on_the_spring_forward_night()
    {
        var before = At("2027-03-27T02:30:00+01:00");

        var gap = HalfPastTwo.GetNextOccurrence(before, StockholmTime.Zone);

        gap.ShouldBe(At("2027-03-28T03:00:00+02:00"));
        gap!.Value.Offset.ShouldBe(TimeSpan.FromHours(2));
        HalfPastTwo.GetNextOccurrence(gap.Value, StockholmTime.Zone).ShouldBe(At("2027-03-29T02:30:00+02:00"));
    }

    [Fact]
    public void A_2_30_schedule_runs_once_on_the_fall_back_night_at_the_first_2_30()
    {
        var before = At("2027-10-30T02:30:00+02:00");

        var first = HalfPastTwo.GetNextOccurrence(before, StockholmTime.Zone);

        first.ShouldBe(At("2027-10-31T02:30:00+02:00"));
        // The second 02:30 (CET, an hour later in UTC) is skipped: the next run is the following night.
        HalfPastTwo.GetNextOccurrence(first!.Value, StockholmTime.Zone).ShouldBe(At("2027-11-01T02:30:00+01:00"));
    }

    [Fact]
    public void An_interval_schedule_runs_at_both_offsets_of_the_repeated_hour()
    {
        var everyHalfHour = CronExpression.Parse("*/30 * * * *", CronFormat.Standard);
        var occurrences = new List<DateTimeOffset>();
        var cursor = At("2027-10-31T01:45:00+02:00");
        for (var i = 0; i < 5; i++)
        {
            cursor = everyHalfHour.GetNextOccurrence(cursor, StockholmTime.Zone)!.Value;
            occurrences.Add(cursor);
        }

        occurrences.ShouldBe([
            At("2027-10-31T02:00:00+02:00"), At("2027-10-31T02:30:00+02:00"),
            At("2027-10-31T02:00:00+01:00"), At("2027-10-31T02:30:00+01:00"), At("2027-10-31T03:00:00+01:00"),
        ]);
    }

    [Fact]
    public void The_scheduler_runs_a_2_30_source_once_on_each_dst_night()
    {
        var values = EngineSettingsValues.Default with { ScheduleJitterMinutes = 0 };
        var source = new ScheduledSource(Guid.NewGuid(), "url-list", Classification.Public, Guid.NewGuid(), "30 2 * * *", At("2027-01-01T00:00:00Z"),
            At("2027-03-27T02:30:00+01:00"), Running: false);

        // Spring: due at 03:00 CEST, not before.
        SyncScheduler.Due(source, values, At("2027-03-28T00:59:00Z")).ShouldBeNull();
        SyncScheduler.Due(source, values, At("2027-03-28T01:00:00Z"))!.Value.Occurrence.ShouldBe(At("2027-03-28T03:00:00+02:00"));

        // Fall: the first 02:30 is due; once it ran, the second 02:30 is not.
        var autumn = source with { LastScheduledFor = At("2027-10-30T02:30:00+02:00") };
        SyncScheduler.Due(autumn, values, At("2027-10-31T00:30:00Z"))!.Value.Occurrence.ShouldBe(At("2027-10-31T02:30:00+02:00"));
        var ran = autumn with { LastScheduledFor = At("2027-10-31T02:30:00+02:00") };
        SyncScheduler.Due(ran, values, At("2027-10-31T01:30:00Z")).ShouldBeNull();
        SyncScheduler.Due(ran, values, At("2027-10-31T02:00:00Z")).ShouldBeNull();
    }

    [Theory]
    [InlineData("0 30 2 * * *")] // 6 fields: seconds are not part of the standard format
    [InlineData("60 2 * * *")]
    [InlineData("30 2 * *")]
    [InlineData("30 2 * * * x")]
    public void A_6_field_or_invalid_expression_does_not_parse_and_is_never_due(string schedule)
    {
        CronExpression.TryParse(schedule, CronFormat.Standard, out _).ShouldBeFalse();
        Should.Throw<CronFormatException>(() => CronExpression.Parse(schedule, CronFormat.Standard));
        NeverDue(schedule);
    }

    [Fact]
    public void The_jitter_character_without_a_seed_does_not_parse_and_is_never_due()
    {
        // Cronos's H needs a jitter seed, which the scheduler does not pass: TryParse answers false (Parse would throw
        // MissingSeedException, not a CronFormatException), so one such schedule does not fail the scheduler's pass.
        CronExpression.TryParse("H 2 * * *", CronFormat.Standard, out _).ShouldBeFalse();
        Should.Throw<MissingSeedException>(() => CronExpression.Parse("H 2 * * *", CronFormat.Standard));
        NeverDue("H 2 * * *");
    }

    private static void NeverDue(string schedule)
    {
        var source = new ScheduledSource(Guid.NewGuid(), "url-list", Classification.Public, Guid.NewGuid(), schedule, At("2027-01-01T00:00:00Z"), null, false);
        SyncScheduler.Due(source, EngineSettingsValues.Default, At("2027-06-01T00:00:00Z")).ShouldBeNull();
    }
}
