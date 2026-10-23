using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Hosting;

public sealed class CoreHostTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TempDataRoot _root = new();

    [Fact]
    public async Task History_persists_across_a_restart_and_examples_are_seeded_once()
    {
        Guid runId;
        var launchers = new RecordingLaunchers();
        using (var host = CoreHost.Open(_root.Paths, launchers.Set, TempDataRoot.ExamplesRoot))
        {
            Assert.Equal(0, host.InterruptedOnStartup);
            Assert.All(host.Seeding, s => Assert.NotEmpty(s.CopiedFiles));
            Assert.Equal(3, host.GetWorkflows(ProfileId.Work).Workflows.Count);
            Assert.Equal(2, host.GetWorkflows(ProfileId.Personal).Workflows.Count);
            Assert.Empty(host.Workflows.SelectMany(w => w.Errors));
            Assert.True(File.Exists(Path.Combine(host.Profiles.Get(ProfileId.Personal).ScriptsDirectory, "hello.cmd")), "example script was not seeded");

            var started = host.Run(ProfileId.Work, "start-workday");
            using var cts = new CancellationTokenSource(Timeout);
            var result = await host.Engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);
            runId = result.Id;
            // Whether the folder step succeeds depends on %USERPROFILE%\Documents existing on this PC;
            // what matters here is that the run settled and was persisted, with the two URL opens recorded by the fake.
            Assert.False(result.IsActive);
            Assert.Equal(2, launchers.Urls.Opened.Count);
        }

        using (var reopened = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, TempDataRoot.ExamplesRoot))
        {
            Assert.Equal(0, reopened.InterruptedOnStartup);
            Assert.All(reopened.Seeding, s => Assert.Empty(s.CopiedFiles));
            var recent = reopened.History.GetRecentExecutions(ProfileId.Work, 10);
            var run = Assert.Single(recent);
            Assert.Equal(runId, run.Id);
            Assert.Equal(3, run.Steps.Count);
            Assert.Empty(reopened.History.GetRecentExecutions(ProfileId.Personal, 10));
        }
    }

    [Fact]
    public void A_run_left_active_by_a_crash_is_marked_Interrupted_on_the_next_start()
    {
        var id = Guid.NewGuid();
        using (var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot))
        {
            var now = DateTimeOffset.UtcNow;
            host.History.SaveExecution(new ExecutionSnapshot(id, ProfileId.Personal, "wf", "Crashy", ExecutionStatus.Running, now, now, null, null,
                [new StepSnapshot("s0", "Step", "windows.url.open", 0, StepStatus.Running, now, null, null, null, null)]));
        }

        using var reopened = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot, new CoreHostOptions(ReviewInterruptedRuns: false));

        Assert.Equal(1, reopened.InterruptedOnStartup);
        var run = reopened.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.Interrupted, run.Status);
        Assert.Equal(StepStatus.Interrupted, run.Steps[0].Status);
    }

    [Fact]
    public void An_interrupted_run_whose_workflow_file_is_missing_becomes_NeedsAttention_instead_of_failing_startup()
    {
        var id = Guid.NewGuid();
        using (var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot))
        {
            var now = DateTimeOffset.UtcNow;
            host.History.SaveExecution(new ExecutionSnapshot(id, ProfileId.Personal, "wf", "Crashy", ExecutionStatus.Running, now, now, null, null,
                [new StepSnapshot("s0", "Step", "windows.url.open", 0, StepStatus.Running, now, null, null, null, null)]));
        }

        using var reopened = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot);

        var decision = Assert.Single(reopened.RecoveryDecisions);
        Assert.Equal(RecoveryStates.NeedsAttention, decision.Outcome);
        Assert.Contains("no longer loads", decision.Explanation, StringComparison.Ordinal);
        var run = Assert.Single(reopened.NeedsAttention);
        Assert.Equal(id, run.Id);
        Assert.Equal(ExecutionStatus.NeedsAttention, run.Status);
    }

    [Fact]
    public void Search_matches_names_aliases_and_ids_across_both_profiles()
    {
        using var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, TempDataRoot.ExamplesRoot);

        Assert.Equal(["hello-script"], host.Search("hello").Select(m => m.Workflow.Id));
        Assert.Equal(["hello-script"], host.Search("script demo").Select(m => m.Workflow.Id));
        Assert.Equal(["start-workday"], host.Search("start").Select(m => m.Workflow.Id));
        Assert.Equal(ProfileId.Work, host.Search("focus").Single().Profile.Id);
        Assert.Equal(5, host.Search(string.Empty).Count);
        Assert.Empty(host.Search("zzz-nothing"));
    }

    [Fact]
    public void A_profile_policy_file_narrows_the_allowlist_and_the_engine_enforces_it()
    {
        var workDir = _root.Paths.GetProfileDirectory(ProfileId.Work);
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, ProfileCatalog.PolicyFileName), """{ "allowedActions": ["windows.url.open", "windows.folder.open"] }""");

        using var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, TempDataRoot.ExamplesRoot);

        var work = host.Profiles.Get(ProfileId.Work);
        Assert.Empty(host.Profiles.Errors);
        Assert.False(work.IsActionAllowed(ProcessLaunchAction.ActionId));
        Assert.True(host.Profiles.Get(ProfileId.Personal).IsActionAllowed(ProcessLaunchAction.ActionId));

        // focus-mode launches Notepad and today-schedule reads the calendar, so both fail to load under the narrowed Work profile; start-workday still loads.
        var loaded = host.GetWorkflows(ProfileId.Work);
        Assert.Equal(["start-workday"], loaded.Workflows.Select(w => w.Id));
        Assert.Equal(["focus-mode.json", "today-schedule.json"], loaded.Errors.Select(e => Path.GetFileName(e.Source)).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        var error = loaded.Errors.Single(e => e.Source.EndsWith("focus-mode.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("not allowed in the Work profile", error.Message, StringComparison.Ordinal);

        // Defence in depth: even a definition that slipped past the loader is refused by the engine.
        var personalFocus = host.GetWorkflows(ProfileId.Personal).Workflows.Single(w => w.Id == "plan-tomorrow");
        var narrowedPersonal = _root.Profile(ProfileId.Personal, UrlOpenAction.ActionId);
        Assert.Throws<ActionNotAllowedException>(() => host.Engine.Start(personalFocus, narrowedPersonal));
        Assert.Throws<KeyNotFoundException>(() => host.Run(ProfileId.Work, "focus-mode"));
    }

    [Fact]
    public void A_broken_policy_file_is_reported_and_falls_back_to_every_local_action_but_no_m365_action()
    {
        var personalDir = _root.Paths.GetProfileDirectory(ProfileId.Personal);
        Directory.CreateDirectory(personalDir);
        File.WriteAllText(Path.Combine(personalDir, ProfileCatalog.PolicyFileName), """{ "allowedActions": ["windows.registry.write"] }""");

        using var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot);

        var error = Assert.Single(host.Profiles.Errors);
        Assert.Contains("Unknown action 'windows.registry.write'", error.Message, StringComparison.Ordinal);
        Assert.True(host.Profiles.Get(ProfileId.Personal).IsActionAllowed(ProcessLaunchAction.ActionId));
        Assert.False(host.Profiles.Get(ProfileId.Personal).IsActionAllowed(Core.Actions.M365.CalendarReadFileAction.ActionId));
    }

    [Fact]
    public void ReloadWorkflows_picks_up_edits_without_a_restart()
    {
        using var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot);
        Assert.Empty(host.GetWorkflows(ProfileId.Personal).Workflows);

        var folder = host.Profiles.Get(ProfileId.Personal).WorkflowsDirectory;
        File.WriteAllText(Path.Combine(folder, "new.json"), """
            { "schemaVersion": 1, "id": "new", "name": "New", "profileId": "personal", "trigger": { "type": "manual" },
              "steps": [ { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } } ] }
            """);

        var reloaded = host.ReloadWorkflows(ProfileId.Personal);

        Assert.Equal(["new"], reloaded.Workflows.Select(w => w.Id));
        Assert.Equal(["new"], host.GetWorkflows(ProfileId.Personal).Workflows.Select(w => w.Id));
    }

    public void Dispose() => _root.Dispose();
}
