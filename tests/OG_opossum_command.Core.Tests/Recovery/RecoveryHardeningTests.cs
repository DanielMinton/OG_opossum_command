using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Recovery;

/// <summary>Recovery edge cases: operator cancels, half-done failed steps, runs a previous start never reviewed, Resume while running.</summary>
public sealed class RecoveryHardeningTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TempDataRoot _root = new();

    private const string UrlOnly = """
        {
          "schemaVersion": 2, "id": "urls", "name": "Urls", "profileId": "personal", "trigger": { "type": "manual" },
          "steps": [
            { "id": "first", "action": "windows.url.open", "inputs": { "url": "https://example.com/1" } },
            { "id": "second", "action": "windows.url.open", "inputs": { "url": "https://example.com/2" }, "dependsOn": ["first"] }
          ]
        }
        """;

    private const string ScriptThenUrl = """
        {
          "schemaVersion": 2, "id": "mixed", "name": "Mixed", "profileId": "personal", "trigger": { "type": "manual" },
          "execution": { "maxParallelSteps": 2 },
          "steps": [
            { "id": "launch", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\PING.EXE", "arguments": ["-n", "1", "127.0.0.1"], "waitForExit": true }, "onFailure": "continue" },
            { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } }
          ]
        }
        """;

    /// <summary>Writes the workflow file and returns the hash the loader gives it.</summary>
    private string WriteWorkflow(string fileName, string json)
    {
        var folder = _root.Paths.GetProfileWorkflowsDirectory(ProfileId.Personal);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), json);
        var registry = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);
        return TestSupport.LoadValid(registry, _root.Profile(ProfileId.Personal, registry), json).ContentHash;
    }

    private void Seed(Guid id, string workflowId, ExecutionStatus status, string hash, params StepSnapshot[] steps)
    {
        var now = DateTimeOffset.UtcNow;
        using var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        history.SaveExecution(new ExecutionSnapshot(id, ProfileId.Personal, workflowId, workflowId, status, now, now, null, null, steps, hash));
    }

    private static StepSnapshot Step(string id, string action, int ordinal, StepStatus status, bool started = true) =>
        new(id, id, action, ordinal, status, started ? DateTimeOffset.UtcNow : null, null, null, null, null);

    [Fact]
    public void A_run_that_was_being_cancelled_when_the_app_stopped_ends_Cancelled_and_is_not_resumed()
    {
        var hash = WriteWorkflow("urls.json", UrlOnly);
        var id = Guid.NewGuid();
        Seed(id, "urls", ExecutionStatus.Cancelling, hash, Step("first", "windows.url.open", 0, StepStatus.Completed), Step("second", "windows.url.open", 1, StepStatus.Running));

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);

        Assert.Equal(0, host.InterruptedOnStartup);
        Assert.Empty(host.RecoveryDecisions);
        var run = host.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.Cancelled, run.Status);
        Assert.Null(run.Recovery);
        Assert.Equal(SqliteHistoryStore.CancelledBeforeStopError, run.Error);
        Assert.Equal([StepStatus.Completed, StepStatus.Cancelled], run.Steps.Select(s => s.Status));
        Assert.Equal(0, after.TotalCalls);
        Assert.Empty(host.Engine.ActiveExecutions);
    }

    [Fact]
    public void A_non_idempotent_step_that_started_and_failed_blocks_automatic_resume()
    {
        var hash = WriteWorkflow("mixed.json", ScriptThenUrl);
        var id = Guid.NewGuid();
        // "launch" timed out part-way (onFailure: continue), then the app died while the idempotent "open" ran.
        Seed(id, "mixed", ExecutionStatus.Running, hash, Step("launch", "windows.process.launch", 0, StepStatus.Failed), Step("open", "windows.url.open", 1, StepStatus.Running));

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);

        var decision = Assert.Single(host.RecoveryDecisions);
        Assert.Equal(RecoveryStates.NeedsAttention, decision.Outcome);
        Assert.Contains("(windows.process.launch)", decision.Explanation, StringComparison.Ordinal);
        Assert.Equal(ExecutionStatus.NeedsAttention, host.History.GetExecution(id)!.Status);
        Assert.Equal(0, after.TotalCalls);
    }

    [Fact]
    public async Task An_interrupted_run_an_earlier_start_never_reviewed_is_reviewed_on_the_next_start()
    {
        var hash = WriteWorkflow("urls.json", UrlOnly);
        var id = Guid.NewGuid();
        Seed(id, "urls", ExecutionStatus.Running, hash, Step("first", "windows.url.open", 0, StepStatus.Completed), Step("second", "windows.url.open", 1, StepStatus.Running));

        // The earlier start marked it Interrupted and then died before reviewing it.
        using (var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
            Assert.Equal([id], history.MarkInterrupted(DateTimeOffset.UtcNow));
        }

        var after = new RecordingLaunchers();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);

        Assert.Equal(0, host.InterruptedOnStartup);
        var decision = Assert.Single(host.RecoveryDecisions);
        Assert.Equal(id, decision.ExecutionId);
        Assert.Equal(RecoveryStates.AutoResumed, decision.Outcome);
        using var cts = new CancellationTokenSource(Timeout);
        await host.Engine.WaitForCompletionAsync(decision.ResumedAs!.Value, cts.Token);
        Assert.Equal(["https://example.com/2"], after.Urls.Opened.Select(u => u.AbsoluteUri));
        Assert.Equal(RecoveryStates.AutoResumed, host.History.GetExecution(id)!.Recovery);
    }

    [Fact]
    public async Task Resume_while_the_workflow_is_already_running_throws_and_keeps_the_run_waiting()
    {
        var hash = WriteWorkflow("mixed.json", ScriptThenUrl);
        var id = Guid.NewGuid();
        Seed(id, "mixed", ExecutionStatus.Running, hash, Step("launch", "windows.process.launch", 0, StepStatus.Running), Step("open", "windows.url.open", 1, StepStatus.Pending, started: false));

        var after = new RecordingLaunchers();
        after.Processes.Behavior = _ => new FakeProcess();
        using var host = CoreHost.Open(_root.Paths, after.Set, _root.EmptyExamplesRoot);
        Assert.Equal(RecoveryStates.NeedsAttention, Assert.Single(host.RecoveryDecisions).Outcome);
        var manual = host.Run(ProfileId.Personal, "mixed");
        Assert.True(await TestSupport.WaitUntilAsync(() => after.Processes.Requests.Count == 1, Timeout));

        var ex = Assert.Throws<InvalidOperationException>(() => host.Resume(id));

        Assert.Contains("running now", ex.Message, StringComparison.Ordinal);
        var run = host.History.GetExecution(id)!;
        Assert.Equal(ExecutionStatus.NeedsAttention, run.Status);
        Assert.Equal(RecoveryStates.NeedsAttention, run.Recovery);
        Assert.Contains(host.NeedsAttention, r => r.Id == id);

        host.Engine.Cancel(manual.Execution.Id);
        using var cts = new CancellationTokenSource(Timeout);
        await host.Engine.WaitForCompletionAsync(manual.Execution.Id, cts.Token);
    }

    [Fact]
    public void SaveExecution_redacts_the_run_error_and_step_message_and_output()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);

        history.SaveExecution(new ExecutionSnapshot(id, ProfileId.Personal, "wf", "WF", ExecutionStatus.Failed, now, now, now, "failed: password=secret",
            [new StepSnapshot("s", "s", "windows.url.open", 0, StepStatus.Failed, now, now, "x", "message password=secret", "output password=secret")]));

        var stored = history.GetExecution(id)!;
        foreach (var text in new[] { stored.Error, stored.Steps[0].Message, stored.Steps[0].Output })
        {
            Assert.NotNull(text);
            Assert.DoesNotContain("=secret", text, StringComparison.Ordinal);
            Assert.Contains("password=[redacted]", text, StringComparison.Ordinal);
        }
    }

    public void Dispose() => _root.Dispose();
}
