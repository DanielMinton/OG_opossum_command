using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OG_opossum_command.Core.Scheduling;

/// <summary>What to do with occurrences that were due while the app was not running (or asleep) — spec §08.</summary>
public enum MissedRunPolicy
{
    /// <summary>Do nothing; the next occurrence runs on time.</summary>
    Skip,

    /// <summary>Run once, now, no matter how many occurrences were missed.</summary>
    RunOnce,

    /// <summary>Run the most recent missed occurrences one after another, at most <see cref="ScheduleDefinition.CatchUpLimit"/> of them.</summary>
    CatchUpLimited,
}

/// <summary>
/// A validated <c>trigger: { "type": "schedule" }</c> block (schema v2): wall-clock times on chosen weekdays in one
/// explicit time zone. Instants are derived by <see cref="OccurrenceCalculator"/>, which handles daylight-saving
/// transitions deterministically.
/// </summary>
/// <param name="Times">Distinct, sorted wall-clock times.</param>
/// <param name="Days">Distinct, sorted weekdays; all seven when the file omits <c>days</c>.</param>
/// <param name="TimeZoneId">A Windows or IANA zone ID accepted by <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>.</param>
/// <param name="MissedRuns">Policy for occurrences that were due while the app was not running.</param>
/// <param name="CatchUpLimit">Most recent missed occurrences to run under <see cref="MissedRunPolicy.CatchUpLimited"/>; 1 otherwise.</param>
/// <param name="ExpiresAfter">How long after its instant an occurrence still counts as on time; later than that it is "missed".</param>
public sealed record ScheduleDefinition(
    IReadOnlyList<TimeOnly> Times,
    IReadOnlyList<DayOfWeek> Days,
    string TimeZoneId,
    MissedRunPolicy MissedRuns,
    int CatchUpLimit,
    TimeSpan ExpiresAfter)
{
    /// <summary>Upper bound on <c>catchUpLimit</c>.</summary>
    public const int MaxCatchUpLimit = 10;

    /// <summary>Upper bound on <c>at</c> entries per schedule.</summary>
    public const int MaxTimes = 24;

    /// <summary>Default <c>expiresAfterMinutes</c>.</summary>
    public const int DefaultExpiresAfterMinutes = 5;

    /// <summary>Upper bound on <c>expiresAfterMinutes</c> (one day).</summary>
    public const int MaxExpiresAfterMinutes = 24 * 60;

    /// <summary>The resolved zone. Validated at load time, so this does not fail later.</summary>
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>Stable hash of every setting, so the scheduler notices when a file changed its schedule.</summary>
    public string ConfigHash
    {
        get
        {
            var canonical = string.Join(
                "|",
                string.Join(",", Times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))),
                string.Join(",", Days.Select(d => ((int)d).ToString(CultureInfo.InvariantCulture))),
                TimeZoneId,
                MissedRuns.ToString(),
                CatchUpLimit.ToString(CultureInfo.InvariantCulture),
                ExpiresAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    /// <summary>One human-readable line, e.g. <c>08:30, 13:00 on Mon–Fri (Pacific Standard Time)</c>.</summary>
    public string Describe()
    {
        var times = string.Join(", ", Times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        var days = Days.Count == 7
            ? "every day"
            : "on " + string.Join(", ", Days.Select(d => d.ToString()[..3]));
        return $"{times} {days} ({TimeZoneId})";
    }

    /// <summary>Parses a file value such as <c>"mon"</c>; case-insensitive three-letter or full names.</summary>
    public static bool TryParseDay(string? text, out DayOfWeek day)
    {
        foreach (var candidate in Enum.GetValues<DayOfWeek>())
        {
            var name = candidate.ToString();
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase) || string.Equals(name[..3], text, StringComparison.OrdinalIgnoreCase))
            {
                day = candidate;
                return true;
            }
        }

        day = default;
        return false;
    }

    /// <summary>Parses <c>"skip"</c>, <c>"run-once"</c>, <c>"catch-up-limited"</c>.</summary>
    public static bool TryParsePolicy(string? text, out MissedRunPolicy policy)
    {
        switch (text)
        {
            case "skip":
                policy = MissedRunPolicy.Skip;
                return true;
            case "run-once":
                policy = MissedRunPolicy.RunOnce;
                return true;
            case "catch-up-limited":
                policy = MissedRunPolicy.CatchUpLimited;
                return true;
            default:
                policy = default;
                return false;
        }
    }
}
