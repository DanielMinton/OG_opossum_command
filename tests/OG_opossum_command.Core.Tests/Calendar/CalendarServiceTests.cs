using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.M365;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Calendar;

/// <summary>Reads of a calendar file under a temp folder (rule 5c). No test ever points at a real OneDrive folder.</summary>
public sealed class CalendarServiceTests : IDisposable
{
    /// <summary>Fifteen minutes after the sample's generatedAt (2026-10-22T15:45Z).</summary>
    private static readonly DateTimeOffset FreshNow = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _root = new();
    private readonly ManualTimeProvider _clock = new(FreshNow);
    private readonly string _calendarFile;
    private readonly CalendarService _service;

    public CalendarServiceTests()
    {
        _calendarFile = Path.Combine(_root.Root, "fake-onedrive", "OG_opossum_command", "calendar.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_calendarFile)!);
        _service = new CalendarService(_root.Paths, _clock) { RetryDelay = TimeSpan.FromMilliseconds(10) };
    }

    private Profile WorkProfile(string? calendarFile = null) =>
        new(ProfileId.Work, _root.Paths.GetProfileWorkflowsDirectory(ProfileId.Work), _root.Paths.GetProfileScriptsDirectory(ProfileId.Work), [CalendarReadFileAction.ActionId], calendarFile ?? _calendarFile);

    [Fact]
    public async Task A_fresh_file_loads_on_the_first_attempt()
    {
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson);

        var result = await _service.ReadAsync(WorkProfile());

        Assert.Equal(CalendarStatus.Loaded, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(4, result.Snapshot!.Events.Count);
        Assert.False(result.IsFailure);
        Assert.Same(result, _service.GetLatest(ProfileId.Work));
    }

    [Fact]
    public async Task A_file_older_than_the_max_age_is_stale_but_still_shows_events()
    {
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson);
        _clock.Set(new DateTimeOffset(2026, 10, 22, 18, 0, 0, TimeSpan.Zero)); // 2 h 15 min after generatedAt

        var result = await _service.ReadAsync(WorkProfile());

        Assert.Equal(CalendarStatus.Stale, result.Status);
        Assert.True(result.HasEvents);
        Assert.False(result.IsFailure);
        Assert.Contains("is the flow still running", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staleness_follows_the_step_max_age_and_generatedAt_not_the_file_time()
    {
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson);
        File.SetLastWriteTimeUtc(_calendarFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // sync mangles mtimes; must not matter

        var withinDefault = await _service.ReadAsync(WorkProfile());
        var strict = await _service.ReadAsync(WorkProfile(), maxAge: TimeSpan.FromMinutes(10));

        Assert.Equal(CalendarStatus.Loaded, withinDefault.Status);
        Assert.Equal(CalendarStatus.Stale, strict.Status);
    }

    [Fact]
    public async Task A_missing_file_is_retried_once_then_unavailable_and_never_throws()
    {
        var result = await _service.ReadAsync(WorkProfile());

        Assert.Equal(CalendarStatus.Unavailable, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.False(result.IsFailure);
        Assert.Null(result.Snapshot);
        Assert.StartsWith("Calendar unavailable", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_calendarFile), "the app must never create the calendar file");
    }

    [Fact]
    public async Task A_missing_folder_is_unavailable_too()
    {
        var result = await _service.ReadAsync(WorkProfile(Path.Combine(_root.Root, "no-such-folder", "calendar.json")));

        Assert.Equal(CalendarStatus.Unavailable, result.Status);
        Assert.False(Directory.Exists(Path.Combine(_root.Root, "no-such-folder")), "the app must never create folders for the calendar");
    }

    [Fact]
    public async Task An_empty_file_is_unavailable_after_one_retry()
    {
        File.WriteAllText(_calendarFile, string.Empty);

        var result = await _service.ReadAsync(WorkProfile());

        Assert.Equal(CalendarStatus.Unavailable, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task A_half_written_file_is_unavailable_not_a_failure()
    {
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson[..200]);

        var result = await _service.ReadAsync(WorkProfile());

        Assert.Equal(CalendarStatus.Unavailable, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public async Task A_mid_sync_file_that_finishes_before_the_retry_loads_on_the_second_attempt()
    {
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson[..100]);
        _service.RetryDelay = TimeSpan.FromMilliseconds(300);

        var read = _service.ReadAsync(WorkProfile()); // first attempt runs synchronously and sees the half file
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson);
        var result = await read;

        Assert.Equal(CalendarStatus.Loaded, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task A_locked_file_is_retried_then_unavailable()
    {
        File.WriteAllText(_calendarFile, CalendarFileParserTests.SampleJson);
        using (new FileStream(_calendarFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _service.ReadAsync(WorkProfile());

            Assert.Equal(CalendarStatus.Unavailable, result.Status);
            Assert.Equal(2, result.Attempts);
        }
    }

    [Fact]
    public async Task Well_formed_json_in_the_wrong_shape_is_invalid_and_fails_the_step()
    {
        File.WriteAllText(_calendarFile, """{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00", "events": [] }""");

        var result = await _service.ReadAsync(WorkProfile());

        Assert.Equal(CalendarStatus.Invalid, result.Status);
        Assert.True(result.IsFailure);
        Assert.Contains("POWER_AUTOMATE_FLOW.md", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_without_a_calendar_file_is_not_configured()
    {
        var profile = new Profile(ProfileId.Work, _root.Paths.GetProfileWorkflowsDirectory(ProfileId.Work), _root.Paths.GetProfileScriptsDirectory(ProfileId.Work), [CalendarReadFileAction.ActionId]);

        var result = await _service.ReadAsync(profile);

        Assert.Equal(CalendarStatus.NotConfigured, result.Status);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public async Task An_unset_variable_in_the_path_is_unavailable_not_an_error()
    {
        var result = await _service.ReadAsync(WorkProfile(@"%OGOC_TEST_UNSET_VARIABLE_9F2C%\OG_opossum_command\calendar.json"));

        Assert.Equal(CalendarStatus.Unavailable, result.Status);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public async Task Changed_fires_with_each_result()
    {
        var seen = new List<CalendarReadResult>();
        _service.Changed += (_, r) => seen.Add(r);

        var result = await _service.ReadAsync(WorkProfile());

        Assert.Same(result, Assert.Single(seen));
    }

    [Fact]
    public void Default_registry_contains_both_calendar_actions()
    {
        var registry = ActionRegistry.CreateDefault(new Core.Actions.Launchers.RecordingLaunchers().Set);
        Assert.True(registry.Contains(CalendarReadFileAction.ActionId));
        Assert.True(registry.Contains(Core.Actions.Dashboard.ScheduleShowAction.ActionId));
    }

    public void Dispose() => _root.Dispose();
}
