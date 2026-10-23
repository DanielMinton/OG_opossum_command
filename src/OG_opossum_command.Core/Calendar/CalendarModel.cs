namespace OG_opossum_command.Core.Calendar;

/// <summary>One event from the calendar file. Times carry their offset; the UI converts to local time.</summary>
/// <param name="Subject">Trimmed subject, never blank (<c>(no subject)</c> when the file had none).</param>
/// <param name="Start">Start; for all-day events the date part (in its own offset) is the day.</param>
/// <param name="End">End (exclusive); for all-day events Outlook sets the following midnight.</param>
/// <param name="IsAllDay">All-day events are shown by date, not by time.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="Link">Optional https link to open for the event (a Teams join link, or the Outlook event page).</param>
/// <param name="ShowAs">Optional free/busy state as the flow wrote it, lower-cased (<c>busy</c>, <c>tentative</c>, …).</param>
public sealed record CalendarEvent(
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string? Location,
    Uri? Link,
    string? ShowAs)
{
    /// <summary>Whether the event belongs to the calendar day <paramref name="day"/> (all-day events) or overlaps the window starting at <paramref name="dayStart"/> (timed events).</summary>
    public bool OccursOn(DateOnly day, DateTimeOffset dayStart)
    {
        if (IsAllDay)
        {
            var first = DateOnly.FromDateTime(Start.Date);
            var lastExclusive = DateOnly.FromDateTime(End.Date);
            return day >= first && (day < lastExclusive || day == first);
        }

        var dayEnd = dayStart.AddDays(1);
        return Start < dayEnd && End > dayStart;
    }

    /// <summary>Whether a timed event is already over at <paramref name="now"/>. All-day events never count as over during their day.</summary>
    public bool IsOverAt(DateTimeOffset now) => !IsAllDay && End <= now;

    /// <summary>Whether a timed event is in progress at <paramref name="now"/>.</summary>
    public bool IsInProgressAt(DateTimeOffset now) => !IsAllDay && Start <= now && End > now;
}

/// <summary>The parsed calendar file.</summary>
/// <param name="GeneratedAt">When the flow wrote the file (its <c>generatedAt</c>); staleness is judged from this, never from the file's modification time (sync mangles that).</param>
/// <param name="Source">Free text the flow wrote (<c>power-automate</c>), informational.</param>
/// <param name="TimeZone">Optional time zone name the flow wrote, informational.</param>
/// <param name="Events">Events sorted by start.</param>
public sealed record CalendarSnapshot(DateTimeOffset GeneratedAt, string? Source, string? TimeZone, IReadOnlyList<CalendarEvent> Events)
{
    /// <summary>Every event on the calendar day that contains <paramref name="now"/> (in <paramref name="now"/>'s own offset), all-day events first, then by start.</summary>
    public IReadOnlyList<CalendarEvent> EventsOn(DateTimeOffset now)
    {
        var dayStart = new DateTimeOffset(now.Date, now.Offset);
        var day = DateOnly.FromDateTime(now.Date);
        return Events
            .Where(e => e.OccursOn(day, dayStart))
            .OrderByDescending(e => e.IsAllDay)
            .ThenBy(e => e.Start)
            .ToList();
    }

    /// <summary>The events of today that are still relevant at <paramref name="now"/>: all-day events, events in progress, and events yet to start.</summary>
    public IReadOnlyList<CalendarEvent> UpcomingOn(DateTimeOffset now) => EventsOn(now).Where(e => !e.IsOverAt(now)).ToList();
}
