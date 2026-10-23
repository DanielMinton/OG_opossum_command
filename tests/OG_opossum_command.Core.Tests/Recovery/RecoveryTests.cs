using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Recovery;

/// <summary>
/// Spec §13 / §17 Phase 2 exit criterion: no silent duplicate side effects after a kill mid-run. The kill is
/// simulated by closing the history store underneath a running engine (nothing more can be journaled, exactly
/// as when the process dies) and then opening a fresh host on the same database.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TempDataRoot _root = new();

    private const string UrlThenProcess = """
        {
          "schemaVersion": 2, "id": "mixed", "name": "Mixed", "profileId": "personal", "trigger": { "type": "manual" },
          "steps": [
            { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } },
            { "id": "launch", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\notepad.exe", "waitForExit": true }, "dependsOn": ["open"] },
            { "id": "after", "action": "windows.url.open", "inputs": { "url": "https://example.com/after" }, "dependsOn": ["launch"] }
          ]
        }
        """;

    private const string UrlOnly = """
        {
          "schemaVersion": 2, "id": "urls", "name": "Urls", "profileId": "personal", "trigger": { "type": "manual" },
          "steps": [
            { "id": "first", "action": "windows.url.open", "inputs": { "url": "https://example.com/1" } },
            { "id": "second", "action": "windows.url.open", "inputs": { "url": "https://example.com/2" }, "dependsOn": ["first"] },
            { "id": "third", "action": "windows.url.open", "inputs": { "url": "https://example.com/3" }, "dependsOn": ["second"] }
          ]
        }
        """;

    private string WriteWorkflow(string fileName, string json)
    {
        var folder = _root.Paths.GetProfileWorkflowsDirectory(ProfileId.Personal);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Runs <paramref name="json"/> until step <paramref name="runningStep"/> is persisted as Running, then kills the journal and the engine.</summary>
    private async Task<Guid> RunUntilStepRunningThenKillAsync(string json, string runningStep, RecordingLaunchers launchers)
    {
        var registry = ActionRegistry.CreateDefault(launchers.Set);
        var profile = _root.Profile(ProfileId.Personal, registry);
        var workflow = TestSupport.LoadValid(registry, profile, json);
        var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        var engine = new WorkflowEngine(registry, history);
        var started = engine.Start(workflow, profile);
        Assert.True(
            await TestSupport.WaitUntilAsync(() => history.GetExecution(started.Execution.Id)?.Steps.Single(s => s.Id == runningStep).Status == StepStatus.Running, Timeout),
            $"step {runningStep} never reached Running in the journal");

        // The "kill": the journal closes first, so nothing after this instant is recorded, then the engine is torn down.
        history.Dispose();
        engine.Dispose();
        return started.Execution.Id;
    }

    [Fact]
    public async Task A_non_idempotent_step_killed_mid_run_becomes_NeedsAttention_and_nothing_is_repeated_on_its_own()
    {
        WriteWorkflow("mixed.json", UrlThenProcess);
        var before = new RecordingLaunchers();
        before.Processes.Behavior = _ => new FakeProcess(); // notepad "keeps running" until the kill
        var id = await RunUntilStepRunningThenKillAsync(UrlThenProcess, "launch", before);
        Assert.Single(before.Urls.Opened);
        Assert.Single(before.Processes.Requests);

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);

        Assert.Equal(1, host.InterruptedOnStartup);
        var decision = Assert.Single(host.RecoveryDecisions);
        Assert.Equal(id, decision.ExecutionId);
        Assert.Equal(RecoveryStates.NeedsAttention, decision.Outcome);
        Assert.Contains("'Launch program' (windows.process.launch)", decision.Explanation, StringComparison.Ordinal);
        Assert.Contains("second copy", decision.Explanation, StringComparison.Ordinal);

        var run = host.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.NeedsAttention, run.Status);
        Assert.Equal(RecoveryStates.NeedsAttention, run.Recovery);
        Assert.Equal(decision.Explanation, run.Error);
        Assert.Equal([StepStatus.Completed, StepStatus.Interrupted, StepStatus.Interrupted], run.Steps.Select(s => s.Status));
        Assert.Equal(id, Assert.Single(host.NeedsAttention).Id);

        // No side effect happened on its own.
        Assert.Equal(0, after.TotalCalls);
        Assert.Empty(host.Engine.ActiveExecutions);
    }

    [Fact]
    public async Task Resume_runs_only_the_unfinished_steps_and_links_the_new_run_to_the_old_one()
    {
        WriteWorkflow("mixed.json", UrlThenProcess);
        var before = new RecordingLaunchers();
        before.Processes.Behavior = _ => new FakeProcess();
        var id = await RunUntilStepRunningThenKillAsync(UrlThenProcess, "launch", before);

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);
        var resumed = host.Resume(id);
        using var cts = new CancellationTokenSource(Timeout);
        var result = await host.Engine.WaitForCompletionAsync(resumed.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(TriggerSources.Recovery, result.TriggerSource);
        Assert.Equal(id, result.ResumedFrom);
        Assert.Equal(StepStatus.Completed, result.Steps[0].Status);
        Assert.Contains("not repeated", result.Steps[0].Message, StringComparison.Ordinal);
        // The completed URL step was NOT opened again; the interrupted process step and the step after it ran once each.
        Assert.Single(after.Processes.Requests);
        Assert.Equal(["https://example.com/after"], after.Urls.Opened.Select(u => u.AbsoluteUri));

        var original = host.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.Interrupted, original.Status);
        Assert.Equal(RecoveryStates.Resumed, original.Recovery);
        Assert.Contains(resumed.Execution.Id.ToString("D"), original.Error, StringComparison.Ordinal);
        Assert.Empty(host.NeedsAttention);
        Assert.Contains(host.History.GetEvents(id), e => e.EventType == "recovery.resumed");
        Assert.Throws<InvalidOperationException>(() => host.Resume(id));
    }

    [Fact]
    public async Task Dismiss_leaves_the_run_Interrupted_and_runs_nothing()
    {
        WriteWorkflow("mixed.json", UrlThenProcess);
        var before = new RecordingLaunchers();
        before.Processes.Behavior = _ => new FakeProcess();
        var id = await RunUntilStepRunningThenKillAsync(UrlThenProcess, "launch", before);

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);
        host.Dismiss(id);

        var run = host.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.Interrupted, run.Status);
        Assert.Equal(RecoveryStates.Dismissed, run.Recovery);
        Assert.Contains("Dismissed", run.Error, StringComparison.Ordinal);
        Assert.Empty(host.NeedsAttention);
        Assert.Equal(0, after.TotalCalls);
        Assert.Throws<InvalidOperationException>(() => host.Dismiss(id));
    }

    [Fact]
    public async Task Only_idempotent_unfinished_steps_resume_automatically_without_repeating_completed_ones()
    {
        var path = WriteWorkflow("urls.json", UrlOnly);
        var registry = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);
        var definition = TestSupport.LoadValid(registry, _root.Profile(ProfileId.Personal, registry), File.ReadAllText(path));
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Journal exactly what a crash leaves behind: "first" completed, "second" running, "third" never started.
        using (var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
            history.SaveExecution(new ExecutionSnapshot(id, ProfileId.Personal, "urls", "Urls", ExecutionStatus.Running, now, now, null, null,
                [
                    new StepSnapshot("first", "first", "windows.url.open", 0, StepStatus.Completed, now, now, null, "Opened", null),
                    new StepSnapshot("second", "second", "windows.url.open", 1, StepStatus.Running, now, null, null, null, null),
                    new StepSnapshot("third", "third", "windows.url.open", 2, StepStatus.Pending, null, null, null, null, null),
                ],
                definition.ContentHash));
        }

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);

        var decision = Assert.Single(host.RecoveryDecisions);
        Assert.Equal(RecoveryStates.AutoResumed, decision.Outcome);
        Assert.NotNull(decision.ResumedAs);
        using var cts = new CancellationTokenSource(Timeout);
        var result = await host.Engine.WaitForCompletionAsync(decision.ResumedAs!.Value, cts.Token);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(id, result.ResumedFrom);
        Assert.Equal(["https://example.com/2", "https://example.com/3"], after.Urls.Opened.Select(u => u.AbsoluteUri));
        var original = host.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.Interrupted, original.Status);
        Assert.Equal(RecoveryStates.AutoResumed, original.Recovery);
        Assert.Empty(host.NeedsAttention);
    }

    [Fact]
    public async Task A_workflow_file_changed_since_the_run_started_needs_attention_even_when_every_step_is_idempotent()
    {
        var path = WriteWorkflow("urls.json", UrlOnly);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
            history.SaveExecution(new ExecutionSnapshot(id, ProfileId.Personal, "urls", "Urls", ExecutionStatus.Running, now, now, null, null,
                [
                    new StepSnapshot("first", "first", "windows.url.open", 0, StepStatus.Completed, now, now, null, null, null),
                    new StepSnapshot("second", "second", "windows.url.open", 1, StepStatus.Running, now, null, null, null, null),
                ],
                "0000-hash-of-the-old-file"));
        }

        File.WriteAllText(path, UrlOnly.Replace("https://example.com/3", "https://example.com/changed", StringComparison.Ordinal));
        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);

        var decision = Assert.Single(host.RecoveryDecisions);
        Assert.Equal(RecoveryStates.NeedsAttention, decision.Outcome);
        Assert.Contains("changed on disk", decision.Explanation, StringComparison.Ordinal);
        Assert.Empty(after.Urls.Opened);

        // Resume uses the current file and skips what completed.
        var resumed = host.Resume(id);
        using var cts = new CancellationTokenSource(Timeout);
        await host.Engine.WaitForCompletionAsync(resumed.Execution.Id, cts.Token);
        Assert.Equal(["https://example.com/2", "https://example.com/changed"], after.Urls.Opened.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task The_engine_survives_the_journal_closing_underneath_it_without_unobserved_exceptions()
    {
        WriteWorkflow("mixed.json", UrlThenProcess);
        var launchers = new RecordingLaunchers();
        launchers.Processes.Behavior = _ => new FakeProcess();
        var unobserved = new List<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            lock (unobserved)
            {
                unobserved.Add(e.Exception.ToString());
            }
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await RunUntilStepRunningThenKillAsync(UrlThenProcess, "launch", launchers);
            await Task.Delay(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved)
            {
                // Other test classes run in parallel in this process; only exceptions from the engine count here.
                var fromEngine = unobserved.Where(x => x.Contains("WorkflowEngine", StringComparison.Ordinal)).ToList();
                Assert.True(fromEngine.Count == 0, string.Join(Environment.NewLine + "----" + Environment.NewLine, fromEngine));
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    public void Dispose() => _root.Dispose();
}
