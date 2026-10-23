using System.Globalization;
using OG_opossum_command.Core.Actions.M365;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Calendar;

/// <summary>How a calendar read ended.</summary>
public enum CalendarStatus
{
    /// <summary>Fresh events.</summary>
    Loaded,

    /// <summary>Events, but the file is older than the allowed age (the flow may have stopped).</summary>
    Stale,

    /// <summary>No events: the file is missing, empty, half-written (not JSON), an online-only placeholder, or unreadable after a retry. Not an error for the run.</summary>
    Unavailable,

    /// <summary>The profile has no calendar file configured. Not an error for the run.</summary>
    NotConfigured,

    /// <summary>The profile's allowlist excludes <c>m365.calendar.readFile</c>. Not an error for the run.</summary>
    NotAllowed,

    /// <summary>The path belongs to the other context's OneDrive (ADR-012). The step fails.</summary>
    Refused,

    /// <summary>The file is well-formed JSON but not in the expected shape, even after a retry. The step fails so the flow gets fixed.</summary>
    Invalid,
}

/// <summary>What one read produced. Event content stays here in memory; history only ever sees <see cref="Message"/> and counts.</summary>
/// <param name="Attempts">How many times the file was opened (2 means the first attempt hit a mid-sync file).</param>
public sealed record CalendarReadResult(
    ProfileId Profile,
    CalendarStatus Status,
    string Message,
    CalendarSnapshot? Snapshot,
    int Attempts)
{
    /// <summary>Whether a workflow step should fail on this result (only <see cref="CalendarStatus.Refused"/> and <see cref="CalendarStatus.Invalid"/>).</summary>
    public bool IsFailure => Status is CalendarStatus.Refused or CalendarStatus.Invalid;

    /// <summary>Whether events are available (fresh or stale).</summary>
    public bool HasEvents => Snapshot is not null;
}

/// <summary>
/// Reads a profile's calendar file (ADR-011, ADR-039) and keeps the latest result per profile for the dashboard.
/// Tolerant by design: a missing, empty, locked, or half-written file is retried once and then reported as
/// unavailable; nothing here throws for a bad file. The file is only ever read, never created or written, and a
/// OneDrive online-only placeholder is never opened (that would start a download). The Work/Personal boundary is
/// enforced here, not only in the action, because the dashboard reads through this service too (ADR-012).
/// </summary>
public sealed class CalendarService
{
    /// <summary>A file older than this (by its <c>generatedAt</c>) counts as stale unless the step says otherwise.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(60);

    private readonly IAppPaths? _paths;
    private readonly TimeProvider _time;
    private readonly Dictionary<ProfileId, CalendarReadResult> _latest = [];
    private readonly Lock _gate = new();

    /// <param name="paths">Supplies the OneDrive roots for the cross-profile guard; <c>null</c> disables the guard (plain unit tests).</param>
    /// <param name="time">Clock for staleness; tests inject one.</param>
    public CalendarService(IAppPaths? paths = null, TimeProvider? time = null)
    {
        _paths = paths;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after every read with its result (on the reading thread).</summary>
    public event EventHandler<CalendarReadResult>? Changed;

    /// <summary>Pause before the single retry; tests shorten it.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>The most recent result for a profile, or <c>null</c> before the first read.</summary>
    public CalendarReadResult? GetLatest(ProfileId profile)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(profile, out var result) ? result : null;
        }
    }

    /// <summary>
    /// Reads the profile's calendar file. The first attempt runs synchronously; only a retry awaits. Never throws for
    /// file problems; cancellation propagates.
    /// </summary>
    public async Task<CalendarReadResult> ReadAsync(Profile profile, TimeSpan? maxAge = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var age = maxAge ?? DefaultMaxAge;

        CalendarReadResult result;
        if (!profile.IsActionAllowed(CalendarReadFileAction.ActionId))
        {
            result = new CalendarReadResult(profile.Id, CalendarStatus.NotAllowed, $"The {profile.DisplayName} profile is not allowed to read a calendar ({CalendarReadFileAction.ActionId} is outside its allowlist).", null, 0);
        }
        else if (profile.CalendarFilePath is null)
        {
            result = new CalendarReadResult(profile.Id, CalendarStatus.NotConfigured, $"No calendar file is configured for the {profile.DisplayName} profile. See docs/POWER_AUTOMATE_FLOW.md to set one up.", null, 0);
        }
        else
        {
            var expanded = Environment.ExpandEnvironmentVariables(profile.CalendarFilePath);
            if (profile.CalendarFilePath.Contains('\0', StringComparison.Ordinal))
            {
                // Expansion would cut the path at the NUL and Path.GetFullPath throws on it; a bad setting is reported, never thrown (ADR-039).
                result = new CalendarReadResult(profile.Id, CalendarStatus.Unavailable, $"Calendar unavailable: the calendar file path for the {profile.DisplayName} profile contains a NUL character (\\u0000).", null, 0);
            }
            else if (!Path.IsPathFullyQualified(expanded))
            {
                result = new CalendarReadResult(profile.Id, CalendarStatus.Unavailable, $"Calendar unavailable: '{profile.CalendarFilePath}' is not a full path on this PC (is OneDrive signed in?).", null, 0);
            }
            else
            {
                var path = Path.GetFullPath(expanded);
                var refusal = CrossProfileRefusal(profile, path);
                result = refusal is not null
                    ? new CalendarReadResult(profile.Id, CalendarStatus.Refused, refusal, null, 0)
                    : await ReadFileAsync(profile, path, age, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_gate)
        {
            _latest[profile.Id] = result;
        }

        Changed?.Invoke(this, result);
        return result;
    }

    /// <summary>Why <paramref name="profile"/> may not read <paramref name="fullPath"/>, or <c>null</c> when it may (ADR-012).</summary>
    public string? CrossProfileRefusal(Profile profile, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (_paths is null)
        {
            return null;
        }

        var (forbiddenRoot, owner) = OneDriveBoundary.ForbiddenRoot(profile.Id, _paths);
        if (forbiddenRoot is null || !OneDriveBoundary.IsInside(fullPath, forbiddenRoot))
        {
            return null;
        }

        return $"Refused: the {profile.DisplayName} profile may not read {fullPath}, which is inside the {owner} OneDrive ({forbiddenRoot}). Work and personal calendars never cross (ADR-012).";
    }

    private async Task<CalendarReadResult> ReadFileAsync(Profile profile, string path, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var (first, retry) = TryReadOnce(profile, path, maxAge, attempts: 1);
        if (!retry)
        {
            return first;
        }

        // A file that is missing, empty, locked, or half-written is usually OneDrive mid-sync: one retry, then give up quietly.
        await Task.Delay(RetryDelay, _time, cancellationToken).ConfigureAwait(false);
        return TryReadOnce(profile, path, maxAge, attempts: 2).Result;
    }

    /// <summary>One attempt. <c>Retry</c> is true for outcomes that a mid-sync file explains (missing, empty, locked, half-written).</summary>
    private (CalendarReadResult Result, bool Retry) TryReadOnce(Profile profile, string path, TimeSpan maxAge, int attempts)
    {
        var now = _time.GetUtcNow();
        CalendarReadResult Unavailable(string reason) =>
            new(profile.Id, CalendarStatus.Unavailable, $"Calendar unavailable: {reason}", null, attempts);

        try
        {
            if (!File.Exists(path))
            {
                return (Unavailable($"{path} does not exist yet. Build the flow per docs/POWER_AUTOMATE_FLOW.md, or check that OneDrive is syncing."), true);
            }

            if (OneDriveBoundary.IsOnlineOnly(File.GetAttributes(path)))
            {
                return (Unavailable($"{path} is an online-only OneDrive placeholder and the app never downloads files. In OneDrive, right-click the {AppPaths.DefaultFolderName} folder and choose 'Always keep on this device'."), false);
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (Unavailable($"{path} is empty (OneDrive may be mid-sync)."), true);
            }

            var parsed = CalendarFileParser.Parse(text, path);
            if (parsed.IsMalformedJson)
            {
                // Truncated or half-written text is what a mid-sync file looks like; JSON in the wrong shape is a flow problem (below).
                return (Unavailable($"{path} is not complete JSON yet (OneDrive may be mid-sync). {parsed.Errors[0].Message}"), true);
            }

            if (!parsed.IsValid)
            {
                var shown = string.Join("; ", parsed.Errors.Take(3).Select(e => $"{e.JsonPath}: {e.Message}"));
                return (new CalendarReadResult(profile.Id, CalendarStatus.Invalid, $"Calendar file {path} is not in the expected shape: {shown} See docs/POWER_AUTOMATE_FLOW.md for the exact JSON.", null, attempts), true);
            }

            var snapshot = parsed.Snapshot!;
            var age = now - snapshot.GeneratedAt;
            var updated = snapshot.GeneratedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            var count = snapshot.Events.Count;
            if (age > maxAge)
            {
                return (new CalendarReadResult(profile.Id, CalendarStatus.Stale, $"Loaded {count} event(s), but the file is {DescribeAge(age)} old (updated {updated}); is the flow still running?", snapshot, attempts), false);
            }

            return (new CalendarReadResult(profile.Id, CalendarStatus.Loaded, $"Loaded {count} event(s); updated {updated}.", snapshot, attempts), false);
        }
        catch (IOException ex)
        {
            return (Unavailable($"could not read {path} ({ex.Message}); OneDrive may be mid-sync."), true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (Unavailable($"could not read {path} ({ex.Message})."), true);
        }
    }

    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromHours(2))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min";
        }

        if (age < TimeSpan.FromDays(2))
        {
            return $"{(int)age.TotalHours} h";
        }

        return $"{(int)age.TotalDays} days";
    }
}
