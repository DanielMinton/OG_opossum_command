using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Workflows;

/// <summary>Schema v2 additions (ADR-032): schedule triggers, step locks, aliases; v1 files keep loading and cannot use them.</summary>
public sealed class SchemaV2Tests : IDisposable
{
    private readonly TempDataRoot _root = new();
    private readonly ActionRegistry _registry = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);
    private readonly Profile _work;

    public SchemaV2Tests()
    {
        _work = _root.Profile(ProfileId.Work, _registry);
    }

    private static string File(int schemaVersion, string trigger, string extraTop = "", string extraStep = "") => $$"""
        {
          "schemaVersion": {{schemaVersion}}, "id": "demo", "name": "Demo", "profileId": "work",
          "trigger": {{trigger}}{{extraTop}},
          "steps": [ { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.com/" }{{extraStep}} } ]
        }
        """;

    [Fact]
    public void A_v1_file_still_loads_with_a_manual_trigger_and_no_v2_features()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, File(1, """{ "type": "manual" }"""));

        Assert.Equal(1, workflow.SchemaVersion);
        Assert.Equal(TriggerType.Manual, workflow.Trigger.Type);
        Assert.Null(workflow.Schedule);
        Assert.Empty(workflow.Aliases);
        Assert.Empty(workflow.Steps[0].Locks);
    }

    [Fact]
    public void Parses_a_full_schedule_trigger()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, File(2, """
            { "type": "schedule", "at": ["13:00", "08:30", "08:30"], "days": ["Mon", "fri", "Tuesday"], "timeZone": "Pacific Standard Time",
              "missedRuns": "catch-up-limited", "catchUpLimit": 3, "expiresAfterMinutes": 15 }
            """));

        var schedule = workflow.Schedule!;
        Assert.Equal(TriggerType.Schedule, workflow.Trigger.Type);
        Assert.Equal([new TimeOnly(8, 30), new TimeOnly(13, 0)], schedule.Times);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday], schedule.Days);
        Assert.Equal("Pacific Standard Time", schedule.TimeZoneId);
        Assert.Equal(MissedRunPolicy.CatchUpLimited, schedule.MissedRuns);
        Assert.Equal(3, schedule.CatchUpLimit);
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.ExpiresAfter);
        Assert.Equal(64, schedule.ConfigHash.Length);
        Assert.Equal("08:30, 13:00 on Mon, Tue, Fri (Pacific Standard Time)", schedule.Describe());
    }

    [Fact]
    public void Schedule_defaults_are_every_day_skip_and_a_five_minute_window()
    {
        var schedule = TestSupport.LoadValid(_registry, _work, File(2, """{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC" }""")).Schedule!;

        Assert.Equal(7, schedule.Days.Count);
        Assert.Equal(MissedRunPolicy.Skip, schedule.MissedRuns);
        Assert.Equal(1, schedule.CatchUpLimit);
        Assert.Equal(TimeSpan.FromMinutes(ScheduleDefinition.DefaultExpiresAfterMinutes), schedule.ExpiresAfter);
    }

    [Theory]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "Mars/Olympus" }""", "not a time zone")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"] }""", "timeZone")]
    [InlineData("""{ "type": "schedule", "at": ["9am"], "timeZone": "UTC" }""", "HH:mm")]
    [InlineData("""{ "type": "schedule", "at": [], "timeZone": "UTC" }""", "1 to 24")]
    [InlineData("""{ "type": "schedule", "timeZone": "UTC" }""", "'at'")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "days": ["funday"], "timeZone": "UTC" }""", "not a weekday")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "days": [], "timeZone": "UTC" }""", "at least one weekday")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC", "missedRuns": "retry" }""", "missedRuns")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC", "missedRuns": "catch-up-limited" }""", "catchUpLimit")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC", "missedRuns": "skip", "catchUpLimit": 2 }""", "only applies")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC", "catchUpLimit": 99, "missedRuns": "catch-up-limited" }""", "between 1 and 10")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC", "expiresAfterMinutes": 0 }""", "expiresAfterMinutes")]
    [InlineData("""{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC", "cron": "* * * * *" }""", "Unknown property")]
    [InlineData("""{ "type": "cron" }""", "\"manual\" or \"schedule\"")]
    public void Rejects_bad_schedules_with_readable_errors(string trigger, string expectedFragment)
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, File(2, trigger));

        Assert.Contains(errors, e => e.Message.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void A_v1_file_cannot_use_schedules_locks_or_aliases()
    {
        var scheduleErrors = TestSupport.LoadInvalid(_registry, _work, File(1, """{ "type": "schedule", "at": ["09:00"], "timeZone": "UTC" }"""));
        var lockErrors = TestSupport.LoadInvalid(_registry, _work, File(1, """{ "type": "manual" }""", extraStep: """, "locks": ["window:x"]"""));
        var aliasErrors = TestSupport.LoadInvalid(_registry, _work, File(1, """{ "type": "manual" }""", extraTop: """, "aliases": ["d"]"""));

        Assert.Contains(scheduleErrors, e => e.Message.Contains("needs \"schemaVersion\": 2", StringComparison.Ordinal));
        Assert.Contains(lockErrors, e => e.Message.Contains("needs \"schemaVersion\": 2", StringComparison.Ordinal));
        Assert.Contains(aliasErrors, e => e.Message.Contains("needs \"schemaVersion\": 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_schema_versions_this_build_does_not_know()
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, File(3, """{ "type": "manual" }"""));

        Assert.Contains(errors, e => e.Message.Contains("understands schemaVersion 1 to 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Parses_locks_and_aliases()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, File(2, """{ "type": "manual" }""", extraTop: """, "aliases": ["swd", " Workday ", "swd"]""", extraStep: """, "locks": ["window:Outlook", "file:C:\\Notes\\today.txt", "window:Outlook"]"""));

        Assert.Equal(["swd", "Workday"], workflow.Aliases);
        Assert.Equal(["window:Outlook", @"file:C:\NOTES\TODAY.TXT"], workflow.Steps[0].Locks);
    }

    [Theory]
    [InlineData("""["outlook"]""", "<kind>:<name>")]
    [InlineData("""["printer:hp"]""", "unknown kind")]
    [InlineData("""["file:notes.txt"]""", "full path")]
    [InlineData("\"x\"", "array of strings")]
    public void Rejects_bad_locks(string locks, string expectedFragment)
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, File(2, """{ "type": "manual" }""", extraStep: $", \"locks\": {locks}"));

        Assert.Contains(errors, e => e.Message.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void The_shipped_examples_still_validate_and_none_of_them_is_scheduled()
    {
        var loader = new WorkflowLoader(_registry);
        var personal = _root.Profile(ProfileId.Personal, _registry);
        foreach (var (profile, folder) in new[] { (_work, "work"), (personal, "personal") })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(TempDataRoot.ExamplesRoot, folder), "*.json"))
            {
                var result = loader.LoadFile(file, profile);
                Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
                Assert.Null(result.Workflow!.Schedule);
            }
        }
    }

    public void Dispose() => _root.Dispose();
}
