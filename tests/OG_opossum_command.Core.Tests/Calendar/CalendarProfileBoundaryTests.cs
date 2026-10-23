using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Dashboard;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.M365;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Calendar;

/// <summary>
/// ADR-012 / ADR-040: the Personal profile can never read the Work calendar, at three layers (default allowlist,
/// workflow loader + engine, and the path guard in <see cref="CalendarService"/>). "OneDrive" here is a folder under
/// %TEMP% handed to <see cref="AppPaths.Resolve"/>; the real environment is never consulted.
/// </summary>
public sealed class CalendarProfileBoundaryTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly string _workOneDrive;
    private readonly string _personalOneDrive;
    private readonly AppPaths _paths;

    public CalendarProfileBoundaryTests()
    {
        _workOneDrive = Path.Combine(_root.Root, "OneDrive - Work");
        _personalOneDrive = Path.Combine(_root.Root, "OneDrive - Personal");
        Directory.CreateDirectory(Path.Combine(_workOneDrive, AppPaths.DefaultFolderName));
        Directory.CreateDirectory(Path.Combine(_personalOneDrive, AppPaths.DefaultFolderName));
        File.WriteAllText(WorkCalendarFile, CalendarFileParserTests.SampleJson);

        var data = Path.Combine(_root.Root, "data");
        _paths = AppPaths.Resolve(
            name => name switch
            {
                AppPaths.DataDirVariable => data,
                AppPaths.OneDriveCommercialVariable => _workOneDrive,
                AppPaths.OneDriveConsumerVariable => _personalOneDrive,
                _ => null,
            },
            localAppDataPath: string.Empty);
    }

    private string WorkCalendarFile => Path.Combine(_workOneDrive, AppPaths.DefaultFolderName, AppPaths.CalendarFileName);

    private static string PersonalWorkflow(string id) => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "Sneaky",
          "profileId": "personal",
          "trigger": { "type": "manual" },
          "steps": [ { "id": "read", "action": "m365.calendar.readFile", "inputs": {} } ]
        }
        """;

    private void WritePolicy(ProfileId profile, string json)
    {
        var folder = _paths.GetProfileDirectory(profile);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ProfileCatalog.PolicyFileName), json);
    }

    private static ActionRegistry Registry() => ActionRegistry.CreateDefault(new RecordingLaunchers().Set);

    [Fact]
    public void Work_defaults_to_the_work_OneDrive_file_and_Personal_has_no_calendar()
    {
        var catalog = ProfileCatalog.Load(_paths, Registry());

        Assert.Equal(WorkCalendarFile, catalog.Get(ProfileId.Work).CalendarFilePath);
        Assert.True(catalog.Get(ProfileId.Work).IsActionAllowed(CalendarReadFileAction.ActionId));
        Assert.Null(catalog.Get(ProfileId.Personal).CalendarFilePath);
        Assert.False(catalog.Get(ProfileId.Personal).IsActionAllowed(CalendarReadFileAction.ActionId));
        Assert.True(catalog.Get(ProfileId.Personal).IsActionAllowed(ScheduleShowAction.ActionId));
    }

    [Fact]
    public void ForRoot_resolves_no_OneDrive_so_tests_and_smoke_runs_have_no_default_calendar()
    {
        var paths = AppPaths.ForRoot(_root.Root);

        Assert.Null(paths.OneDriveCommercialRoot);
        Assert.Null(paths.OneDriveConsumerRoot);
        Assert.Null(paths.GetDefaultCalendarFile(ProfileId.Work));
    }

    [Fact]
    public void Relative_or_blank_OneDrive_variables_are_ignored()
    {
        var paths = AppPaths.Resolve(
            name => name switch
            {
                AppPaths.DataDirVariable => _root.Root,
                AppPaths.OneDriveCommercialVariable => @"relative\OneDrive",
                AppPaths.OneDriveConsumerVariable => "   ",
                _ => null,
            },
            localAppDataPath: string.Empty);

        Assert.Null(paths.OneDriveCommercialRoot);
        Assert.Null(paths.OneDriveConsumerRoot);
    }

    [Fact]
    public void The_loader_rejects_a_personal_workflow_that_reads_the_calendar()
    {
        var registry = Registry();
        var catalog = ProfileCatalog.Load(_paths, registry);

        var errors = TestSupport.LoadInvalid(registry, catalog.Get(ProfileId.Personal), PersonalWorkflow("sneaky"));

        Assert.Contains(errors, e => e.Message.Contains(CalendarReadFileAction.ActionId, StringComparison.Ordinal));
    }

    [Fact]
    public void The_engine_refuses_to_start_a_personal_run_that_reads_the_calendar()
    {
        using var host = CoreHost.Open(_paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot);
        // Load against a permissive profile to get past the loader, then hand the engine the real Personal profile.
        var permissive = new Profile(ProfileId.Personal, _paths.GetProfileWorkflowsDirectory(ProfileId.Personal), _paths.GetProfileScriptsDirectory(ProfileId.Personal), host.Actions.Ids);
        var workflow = TestSupport.LoadValid(host.Actions, permissive, PersonalWorkflow("forced"));

        var ex = Assert.Throws<ActionNotAllowedException>(() => host.Engine.Start(workflow, host.Profiles.Get(ProfileId.Personal)));

        Assert.Contains(CalendarReadFileAction.ActionId, ex.Message, StringComparison.Ordinal);
        Assert.Null(host.Calendar.GetLatest(ProfileId.Personal));
    }

    [Fact]
    public async Task Personal_cannot_read_the_work_path_even_when_its_policy_allows_the_action()
    {
        WritePolicy(ProfileId.Personal, $$"""
            { "allowedActions": ["m365.calendar.readFile"], "calendarFile": "{{WorkCalendarFile.Replace(@"\", @"\\", StringComparison.Ordinal)}}" }
            """);
        using var host = CoreHost.Open(_paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot);
        Assert.Empty(host.Profiles.Errors);
        var personal = host.Profiles.Get(ProfileId.Personal);
        var workflow = TestSupport.LoadValid(host.Actions, personal, PersonalWorkflow("explicit"));

        var started = host.Engine.Start(workflow, personal);
        using var cts = new CancellationTokenSource(Timeout);
        var result = await host.Engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        var latest = host.Calendar.GetLatest(ProfileId.Personal)!;
        Assert.Equal(CalendarStatus.Refused, latest.Status);
        Assert.Null(latest.Snapshot);
        Assert.Contains("work OneDrive", latest.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_path_guard_also_covers_a_path_spelled_with_dot_dot_segments()
    {
        var sneaky = Path.Combine(_personalOneDrive, "..", Path.GetFileName(_workOneDrive), AppPaths.DefaultFolderName, AppPaths.CalendarFileName);
        var personal = new Profile(ProfileId.Personal, _paths.GetProfileWorkflowsDirectory(ProfileId.Personal), _paths.GetProfileScriptsDirectory(ProfileId.Personal), [CalendarReadFileAction.ActionId], sneaky);

        var result = await new CalendarService(_paths).ReadAsync(personal);

        Assert.Equal(CalendarStatus.Refused, result.Status);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Work_cannot_read_a_file_inside_the_personal_OneDrive()
    {
        var personalFile = Path.Combine(_personalOneDrive, AppPaths.DefaultFolderName, AppPaths.CalendarFileName);
        File.WriteAllText(personalFile, CalendarFileParserTests.SampleJson);
        var work = new Profile(ProfileId.Work, _paths.GetProfileWorkflowsDirectory(ProfileId.Work), _paths.GetProfileScriptsDirectory(ProfileId.Work), [CalendarReadFileAction.ActionId], personalFile);

        var result = await new CalendarService(_paths).ReadAsync(work);

        Assert.Equal(CalendarStatus.Refused, result.Status);
    }

    [Fact]
    public async Task Personal_reading_through_the_service_without_the_action_is_not_allowed()
    {
        var catalog = ProfileCatalog.Load(_paths, Registry());

        var result = await new CalendarService(_paths).ReadAsync(catalog.Get(ProfileId.Personal));

        Assert.Equal(CalendarStatus.NotAllowed, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void A_broken_policy_file_fails_closed_for_m365_actions()
    {
        WritePolicy(ProfileId.Work, "{ not json");
        WritePolicy(ProfileId.Personal, """{ "allowedActions": ["windows.url.open"], "calendarFile": "relative\\calendar.json" }""");

        var catalog = ProfileCatalog.Load(_paths, Registry());

        Assert.NotEmpty(catalog.Errors);
        Assert.False(catalog.Get(ProfileId.Work).IsActionAllowed(CalendarReadFileAction.ActionId));
        Assert.True(catalog.Get(ProfileId.Work).IsActionAllowed("windows.url.open"));
        Assert.False(catalog.Get(ProfileId.Personal).IsActionAllowed(CalendarReadFileAction.ActionId));
        Assert.Contains(catalog.Errors, e => e.Message.Contains("calendarFile", StringComparison.Ordinal));
    }

    [Fact]
    public void A_policy_calendarFile_overrides_the_default_and_may_use_variables()
    {
        WritePolicy(ProfileId.Work, """{ "calendarFile": "%OneDriveCommercial%\\Somewhere\\cal.json" }""");

        var catalog = ProfileCatalog.Load(_paths, Registry());

        Assert.Empty(catalog.Errors);
        Assert.Equal(@"%OneDriveCommercial%\Somewhere\cal.json", catalog.Get(ProfileId.Work).CalendarFilePath);
        Assert.True(catalog.Get(ProfileId.Work).IsActionAllowed(CalendarReadFileAction.ActionId));
    }

    [Fact]
    public async Task Work_reads_its_calendar_and_shows_the_schedule_through_the_recording_presenter()
    {
        var launchers = new RecordingLaunchers();
        using var host = CoreHost.Open(_paths, launchers.Set, TempDataRoot.ExamplesRoot);
        var work = host.GetWorkflows(ProfileId.Work);
        Assert.Contains(work.Workflows, w => w.Id == "today-schedule");

        var started = host.Run(ProfileId.Work, "today-schedule");
        using var cts = new CancellationTokenSource(Timeout);
        var result = await host.Engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var latest = host.Calendar.GetLatest(ProfileId.Work)!;
        Assert.True(latest.HasEvents, latest.Message);
        Assert.Equal(ProfileId.Work, Assert.Single(launchers.Dashboard.Shown));
        Assert.Equal(1, launchers.TotalCalls);

        // History keeps the status line and counts, never the event subjects.
        var persisted = host.History.GetExecution(result.Id)!;
        Assert.DoesNotContain(persisted.Steps, s => (s.Message ?? string.Empty).Contains("stand-up", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_missing_file_completes_the_example_with_calendar_unavailable()
    {
        File.Delete(WorkCalendarFile);
        var launchers = new RecordingLaunchers();
        using var host = CoreHost.Open(_paths, launchers.Set, TempDataRoot.ExamplesRoot);
        host.Calendar.RetryDelay = TimeSpan.FromMilliseconds(10);

        var started = host.Run(ProfileId.Work, "today-schedule");
        using var cts = new CancellationTokenSource(Timeout);
        var result = await host.Engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(CalendarStatus.Unavailable, host.Calendar.GetLatest(ProfileId.Work)!.Status);
        Assert.Single(launchers.Dashboard.Shown);
        Assert.False(File.Exists(WorkCalendarFile));
    }

    public void Dispose() => _root.Dispose();
}
