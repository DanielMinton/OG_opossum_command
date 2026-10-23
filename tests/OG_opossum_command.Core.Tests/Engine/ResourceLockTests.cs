using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Engine;

public sealed class ResourceLockTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly SqliteHistoryStore _history;
    private readonly WorkflowEngine _engine;
    private readonly Profile _work;

    public ResourceLockTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        _engine = new WorkflowEngine(_registry, _history);
        _work = _root.Profile(ProfileId.Work, _registry);
    }

    private static string ProcessStep(string id, string locks) =>
        $$"""{ "id": "{{id}}", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\PING.EXE", "arguments": ["-n", "1", "127.0.0.1"], "waitForExit": true }, "onFailure": "continue", "locks": [{{locks}}] }""";

    private static string Workflow(string id, int maxParallel, params string[] steps) => $$"""
        {
          "schemaVersion": 2, "id": "{{id}}", "name": "{{id}}", "profileId": "work", "trigger": { "type": "manual" },
          "execution": { "maxParallelSteps": {{maxParallel}} },
          "steps": [ {{string.Join(", ", steps)}} ]
        }
        """;

    private List<FakeProcess> LingeringProcesses()
    {
        var fakes = new List<FakeProcess>();
        _launchers.Processes.Behavior = _ =>
        {
            var fake = new FakeProcess();
            lock (fakes)
            {
                fakes.Add(fake);
            }

            return fake;
        };
        return fakes;
    }

    [Theory]
    [InlineData("window:Outlook", "window:Outlook")]
    [InlineData("WINDOW: outlook ", "window:outlook")]
    [InlineData("custom:my-thing", "custom:my-thing")]
    public void Normalizes_window_and_custom_locks_verbatim_with_a_lower_case_kind(string input, string expected)
    {
        Assert.Null(ResourceLocks.Normalize(input, out var key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Normalizes_file_locks_to_one_canonical_full_path()
    {
        Assert.Null(ResourceLocks.Normalize(@"file:C:\Notes\..\Notes\today.txt", out var a));
        Assert.Null(ResourceLocks.Normalize(@"file:c:\notes\TODAY.TXT", out var b));
        Assert.Null(ResourceLocks.Normalize(@"file:%SystemRoot%\x.txt", out var expanded));

        Assert.Equal(a, b);
        Assert.StartsWith("file:", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("%", expanded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("outlook")]
    [InlineData("window:")]
    [InlineData("registry:HKLM")]
    [InlineData(@"file:relative\path.txt")]
    public void Rejects_malformed_locks_with_a_readable_message(string input)
    {
        var problem = ResourceLocks.Normalize(input, out _);

        Assert.NotNull(problem);
        Assert.Contains("ock", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Steps_sharing_a_lock_run_one_at_a_time_even_with_parallel_slots_free()
    {
        var fakes = LingeringProcesses();
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow("wf", 3, ProcessStep("a", "\"window:outlook\""), ProcessStep("b", "\"window:outlook\""), ProcessStep("c", "\"file:C:\\\\other.txt\"")));
        Assert.All(workflow.Steps.Take(2), s => Assert.Equal(["window:outlook"], s.Locks));

        var started = _engine.Start(workflow, _work);

        // a and c start (different locks); b waits for a.
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 2, Timeout));
        await Task.Delay(300);
        Assert.Equal(2, _launchers.Processes.Requests.Count);
        Assert.True(_engine.Locks.IsHeld("window:outlook"));
        var waiting = _engine.GetExecution(started.Execution.Id)!.Steps.Single(s => s.Id == "b");
        Assert.Equal(StepStatus.Running, waiting.Status);
        Assert.Contains("Waiting for window:outlook", waiting.Message, StringComparison.Ordinal);

        fakes[0].KillTree();
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 3, Timeout));
        fakes[1].KillTree();
        fakes[2].KillTree();
        using var cts = new CancellationTokenSource(Timeout);
        var result = await _engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.True(result.Steps[1].FinishedAt >= result.Steps[0].FinishedAt);
        Assert.False(_engine.Locks.IsHeld("window:outlook"));
        Assert.Contains(_history.GetEvents(result.Id), e => e.EventType == "step.locked" && e.StepId == "b");
    }

    [Fact]
    public async Task Locks_serialize_across_different_workflows_and_are_released_on_cancel()
    {
        var fakes = LingeringProcesses();
        var first = TestSupport.LoadValid(_registry, _work, Workflow("first", 1, ProcessStep("hold", "\"file:C:\\\\Notes\\\\today.txt\"")));
        var second = TestSupport.LoadValid(_registry, _work, Workflow("second", 1, ProcessStep("want", "\"FILE:c:\\\\notes\\\\TODAY.txt\"")));

        var holder = _engine.Start(first, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));
        var waiter = _engine.Start(second, _work);
        await Task.Delay(300);
        Assert.Single(_launchers.Processes.Requests);

        // Cancelling the holder releases the lock: the waiter starts.
        Assert.True(_engine.Cancel(holder.Execution.Id));
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 2, Timeout));
        Assert.True(fakes[0].WasKilled);

        fakes[1].KillTree();
        using var cts = new CancellationTokenSource(Timeout);
        await _engine.WaitForCompletionAsync(holder.Execution.Id, cts.Token);
        var result = await _engine.WaitForCompletionAsync(waiter.Execution.Id, cts.Token);
        Assert.False(result.IsActive);
        Assert.False(_engine.Locks.IsHeld(second.Steps[0].Locks[0]));
    }

    [Fact]
    public async Task Cancelling_a_step_that_is_waiting_for_a_lock_releases_nothing_and_leaves_the_holder_alone()
    {
        var fakes = LingeringProcesses();
        var first = TestSupport.LoadValid(_registry, _work, Workflow("first", 1, ProcessStep("hold", "\"window:x\"")));
        var second = TestSupport.LoadValid(_registry, _work, Workflow("second", 1, ProcessStep("want", "\"window:x\"")));

        var holder = _engine.Start(first, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));
        var waiter = _engine.Start(second, _work);
        await Task.Delay(200);

        Assert.True(_engine.Cancel(waiter.Execution.Id));
        using var cts = new CancellationTokenSource(Timeout);
        var cancelled = await _engine.WaitForCompletionAsync(waiter.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.Single(_launchers.Processes.Requests);
        Assert.True(_engine.Locks.IsHeld("window:x"), "the holder must still own the lock");
        Assert.False(fakes[0].WasKilled);

        fakes[0].KillTree();
        await _engine.WaitForCompletionAsync(holder.Execution.Id, cts.Token);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _history.Dispose();
        _root.Dispose();
    }
}
