using OG_opossum_command.Core.Scheduling;

namespace OG_opossum_command.Core.Tests.Scheduling;

/// <summary>Windows zone IDs so the tests do not depend on ICU data. US Pacific 2026: gap 8 Mar 02:00→03:00, fold 1 Nov 02:00→01:00.</summary>
public sealed class OccurrenceCalculatorTests
{
    private const string Pacific = "Pacific Standard Time";

    private static ScheduleDefinition Daily(string time, string zone = Pacific, params DayOfWeek[] days) =>
        new(
            [TimeOnly.ParseExact(time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture)],
            days.Length == 0 ? Enum.GetValues<DayOfWeek>() : days,
            zone,
            MissedRunPolicy.Skip,
            1,
            TimeSpan.FromMinutes(5));

    [Fact]
    public void A_daily_time_keeps_its_wall_clock_across_the_spring_forward_transition()
    {
        var schedule = Daily("09:00");
        var from = new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);

        var occurrences = OccurrenceCalculator.OccurrencesBetween(schedule, from, to);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 3, 6, 17, 0, 0, TimeSpan.Zero), // 09:00 PST
                new DateTimeOffset(2026, 3, 7, 17, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 8, 16, 0, 0, TimeSpan.Zero), // 09:00 PDT: one hour earlier in UTC
                new DateTimeOffset(2026, 3, 9, 16, 0, 0, TimeSpan.Zero),
            ],
            occurrences);
    }

    [Fact]
    public void A_time_inside_the_spring_forward_gap_runs_at_the_first_minute_that_exists()
    {
        var schedule = Daily("02:30");
        var gapDay = new DateOnly(2026, 3, 8);

        var instant = OccurrenceCalculator.ToInstant(gapDay, new TimeOnly(2, 30), TimeZoneInfo.FindSystemTimeZoneById(Pacific));

        // 02:30 PST does not exist on 8 March; the clock jumps 02:00 → 03:00 PDT, i.e. 10:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero), instant);
        var dayBefore = OccurrenceCalculator.ToInstant(gapDay.AddDays(-1), new TimeOnly(2, 30), TimeZoneInfo.FindSystemTimeZoneById(Pacific));
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 10, 30, 0, TimeSpan.Zero), dayBefore);
    }

    [Fact]
    public void A_time_inside_the_fall_back_fold_fires_once_at_its_first_occurrence()
    {
        var schedule = Daily("01:30");
        var from = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        var occurrences = OccurrenceCalculator.OccurrencesBetween(schedule, from, to);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero),  // 01:30 PDT (first pass), not 09:30 UTC (second pass)
                new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero),  // 01:30 PST
            ],
            occurrences);
    }

    [Fact]
    public void A_daily_time_keeps_its_wall_clock_across_the_fall_back_transition()
    {
        var schedule = Daily("09:00");
        var occurrences = OccurrenceCalculator.OccurrencesBetween(
            schedule,
            new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            [
                new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), // 09:00 PDT
                new DateTimeOffset(2026, 11, 1, 17, 0, 0, TimeSpan.Zero),  // 09:00 PST
            ],
            occurrences);
    }

    [Fact]
    public void Weekdays_filter_occurrences_and_NextAfter_skips_the_weekend()
    {
        var schedule = Daily("08:30", Pacific, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday);
        var fridayAfternoon = new DateTimeOffset(2026, 10, 23, 23, 0, 0, TimeSpan.Zero); // Fri 23 Oct 2026 16:00 PDT

        var next = OccurrenceCalculator.NextAfter(schedule, fridayAfternoon);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 15, 30, 0, TimeSpan.Zero), next); // Mon 26 Oct 08:30 PDT
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTime(next!.Value, TimeZoneInfo.FindSystemTimeZoneById(Pacific)).DayOfWeek);
    }

    [Fact]
    public void Bounds_are_exclusive_at_the_start_and_inclusive_at_the_end()
    {
        var schedule = Daily("12:00", "UTC");
        var noon = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(OccurrenceCalculator.OccurrencesBetween(schedule, noon, noon.AddHours(1)));
        Assert.Equal([noon], OccurrenceCalculator.OccurrencesBetween(schedule, noon.AddMinutes(-1), noon));
        Assert.Empty(OccurrenceCalculator.OccurrencesBetween(schedule, noon, noon));
    }

    [Fact]
    public void Accepts_IANA_zone_ids_too()
    {
        var schedule = Daily("09:00", "America/Los_Angeles");

        var next = OccurrenceCalculator.NextAfter(schedule, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero), next);
    }
}
