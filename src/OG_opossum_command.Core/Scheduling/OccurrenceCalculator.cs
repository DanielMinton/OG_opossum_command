namespace OG_opossum_command.Core.Scheduling;

/// <summary>
/// Pure wall-clock → instant arithmetic for schedules (spec §08, DST-safe). Rules:
/// a time that does not exist on a day (spring-forward gap) runs at the first minute that does exist;
/// a time that exists twice (fall-back fold) runs once, at its first (daylight-time) occurrence.
/// The machine's local zone is never consulted; every schedule names its own.
/// </summary>
public static class OccurrenceCalculator
{
    /// <summary>Furthest the calculator searches for the next occurrence (a weekly schedule needs at most 7 days).</summary>
    private static readonly TimeSpan SearchHorizon = TimeSpan.FromDays(8);

    /// <summary>Every occurrence with <paramref name="afterExclusive"/> &lt; instant ≤ <paramref name="untilInclusive"/>, ascending.</summary>
    public static IReadOnlyList<DateTimeOffset> OccurrencesBetween(ScheduleDefinition schedule, DateTimeOffset afterExclusive, DateTimeOffset untilInclusive)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (untilInclusive <= afterExclusive)
        {
            return [];
        }

        var zone = schedule.TimeZone;
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterExclusive, zone).DateTime).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(untilInclusive, zone).DateTime).AddDays(1);

        var result = new List<DateTimeOffset>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (!schedule.Days.Contains(day.DayOfWeek))
            {
                continue;
            }

            foreach (var time in schedule.Times)
            {
                var instant = ToInstant(day, time, zone);
                if (instant > afterExclusive && instant <= untilInclusive)
                {
                    result.Add(instant);
                }
            }
        }

        result.Sort();
        return result.Distinct().ToList();
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or <c>null</c> when the schedule has no days or times.</summary>
    public static DateTimeOffset? NextAfter(ScheduleDefinition schedule, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var candidates = OccurrencesBetween(schedule, after, after + SearchHorizon);
        return candidates.Count == 0 ? null : candidates[0];
    }

    /// <summary>The instant of <paramref name="time"/> on <paramref name="day"/> in <paramref name="zone"/>, applying the gap and fold rules.</summary>
    public static DateTimeOffset ToInstant(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);

        // Spring forward: 02:30 does not exist; run at the first minute that does (03:00).
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 24 * 60)
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Fall back: 01:30 happens twice; take the first pass (the larger UTC offset is the earlier instant).
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
