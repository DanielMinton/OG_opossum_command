using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Actions;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Engine;

/// <remarks>Shares the "real processes" collection with the launcher tests so ping-process bookkeeping is never interleaved (ADR-030).</remarks>
[Collection(RealProcesses.Name)]
public sealed class WorkflowEngineTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly SqliteHistoryStore _history;
    private readonly WorkflowEngine _engine;
    private readonly Profile _work;
    private readonly List<ExecutionSnapshot> _changes = [];
    private readonly Lock _gate = new();

    public WorkflowEngineTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        _engine = new WorkflowEngine(_registry, _history);
        _engine.ExecutionChanged += (_, snapshot) =>
        {
            lock (_gate)
            {
                _changes.Add(snapshot);
            }
        };
        _work = _root.Profile(ProfileId.Work, _registry);
    }

    private static string Workflow(string steps, int maxParallel = 1) => $$"""
        {
          "schemaVersion": 1, "id": "wf", "name": "WF", "profileId": "work", "trigger": { "type": "manual" },
          "execution": { "maxParallelSteps": {{maxParallel}} },
          "steps": [ {{steps}} ]
        }
        """;

    private static string UrlStep(string id, string dependsOn = "", string onFailure = "stop") =>
        $$"""{ "id": "{{id}}", "action": "windows.url.open", "inputs": { "url": "https://{{id}}.example/" }, "dependsOn": [{{dependsOn}}], "onFailure": "{{onFailure}}" }""";

    private static string ProcessStep(string id, string dependsOn = "", string onFailure = "stop", int? timeoutSeconds = null) =>
        $$"""{ "id": "{{id}}", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\PING.EXE", "arguments": ["-n", "1", "{{id}}"], "waitForExit": true }, "dependsOn": [{{dependsOn}}], "onFailure": "{{onFailure}}"{{(timeoutSeconds is null ? "" : $", \"timeoutSeconds\": {timeoutSeconds}")}} }""";

    private List<ExecutionStatus> StatusesOf(Guid id)
    {
        lock (_gate)
        {
            return _changes.Where(c => c.Id == id).Select(c => c.Status).Distinct().ToList();
        }
    }

    private async Task<ExecutionSnapshot> RunToEndAsync(string json)
    {
        var workflow = TestSupport.LoadValid(_registry, _work, json);
        var started = _engine.Start(workflow, _work);
        Assert.False(started.WasAlreadyRunning);
        Assert.Equal(ExecutionStatus.Queued, started.Execution.Status);
        using var cts = new CancellationTokenSource(Timeout);
        return await _engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);
    }

    [Fact]
    public async Task Completes_in_dependency_order_and_moves_Queued_Running_Completed()
    {
        var result = await RunToEndAsync(Workflow($"{UrlStep("a")}, {UrlStep("b", "\"a\"")}, {UrlStep("c", "\"a\", \"b\"")}", maxParallel: 3));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal([ExecutionStatus.Queued, ExecutionStatus.Running, ExecutionStatus.Completed], StatusesOf(result.Id));
        Assert.All(result.Steps, s => Assert.Equal(StepStatus.Completed, s.Status));
        Assert.Equal(["a", "b", "c"], _launchers.Urls.Opened.Select(u => u.Host.Split('.')[0]).ToArray());
        Assert.True(result.Steps[1].StartedAt >= result.Steps[0].FinishedAt);
        Assert.True(result.Steps[2].StartedAt >= result.Steps[1].FinishedAt);
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.FinishedAt);

        var persisted = _history.GetExecution(result.Id);
        Assert.Equal(ExecutionStatus.Completed, persisted!.Status);
        Assert.Equal(3, persisted.Steps.Count);
        Assert.Contains(_history.GetEvents(result.Id), e => e.EventType == "execution.completed");
    }

    [Fact]
    public async Task A_failed_step_with_stop_skips_the_rest_and_fails_the_run()
    {
        _launchers.Processes.Behavior = _ => new FakeProcess(exitCode: 3, standardError: "boom");

        var result = await RunToEndAsync(Workflow($"{ProcessStep("first")}, {UrlStep("second", "\"first\"")}, {UrlStep("independent")}"));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, result.Steps[0].Status);
        Assert.Equal("exit-code", result.Steps[0].ErrorCode);
        Assert.Contains("boom", result.Steps[0].Output, StringComparison.Ordinal);
        Assert.Equal(StepStatus.Skipped, result.Steps[1].Status);
        Assert.Equal(StepStatus.Skipped, result.Steps[2].Status);
        Assert.Empty(_launchers.Urls.Opened);
        Assert.Contains("exited with code 3", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_step_with_continue_lets_independent_steps_run_but_the_run_is_still_Failed()
    {
        _launchers.Processes.Behavior = _ => new FakeProcess(exitCode: 1);

        var result = await RunToEndAsync(Workflow($"{ProcessStep("first", onFailure: "continue")}, {UrlStep("dependent", "\"first\"")}, {UrlStep("independent")}"));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, result.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, result.Steps[1].Status);
        Assert.Equal("dependency-failed", result.Steps[1].ErrorCode);
        Assert.Equal(StepStatus.Completed, result.Steps[2].Status);
        Assert.Single(_launchers.Urls.Opened);
    }

    [Fact]
    public async Task Cancel_moves_Running_to_Cancelling_to_Cancelled_and_kills_the_fake_process_tree()
    {
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow($"{ProcessStep("wait")}, {UrlStep("after", "\"wait\"")}"));

        var started = _engine.Start(workflow, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout), "the process step never started");
        Assert.True(_engine.Cancel(started.Execution.Id));

        using var cts = new CancellationTokenSource(Timeout);
        var result = await _engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.True(lingering.WasKilled, "KillTree was not called on the launched process");
        Assert.Equal([ExecutionStatus.Queued, ExecutionStatus.Running, ExecutionStatus.Cancelling, ExecutionStatus.Cancelled], StatusesOf(result.Id));
        Assert.Equal(StepStatus.Cancelled, result.Steps[0].Status);
        Assert.Equal(StepStatus.Cancelled, result.Steps[1].Status);
        Assert.Empty(_launchers.Urls.Opened);
        Assert.False(_engine.Cancel(started.Execution.Id), "cancelling a finished run must return false");
        Assert.Equal(ExecutionStatus.Cancelled, _history.GetExecution(result.Id)!.Status);
    }

    [Fact]
    public async Task Cancel_kills_a_real_child_process_tree()
    {
        // A .cmd running ping -n 30 127.0.0.1: cmd is the launched process, ping is its child (rule 7 allows these throwaways).
        // cmd.exe itself may not be started with windows.process.launch, so the script action provides the tree.
        _root.WriteScript(ProfileId.Work, "ping.cmd", "@ping -n 30 127.0.0.1\r\n");
        var real = new LauncherSet(new WindowsProcessLauncher(), _launchers.Urls, _launchers.Folders, _launchers.Dashboard);
        var registry = ActionRegistry.CreateDefault(real);
        using var engine = new WorkflowEngine(registry, _history);
        var profile = _root.Profile(ProfileId.Work, registry);
        var workflow = TestSupport.LoadValid(registry, profile, Workflow(
            """{ "id": "ping", "action": "windows.script.run", "inputs": { "interpreter": "cmd", "script": "ping.cmd" } }"""));

        var pingsBefore = TestSupport.PingPids();
        var started = engine.Start(workflow, profile);
        var pingPid = await TestSupport.WaitForNewPingAsync(pingsBefore, Timeout);

        Assert.True(engine.Cancel(started.Execution.Id));
        using var cts = new CancellationTokenSource(Timeout);
        var result = await engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.True(await TestSupport.WaitForPingGoneAsync(pingPid, TimeSpan.FromSeconds(5)), "the ping child process survived cancellation");
    }

    [Fact]
    public async Task A_step_that_exceeds_its_timeout_fails_with_timeout_and_the_process_is_killed()
    {
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;

        var result = await RunToEndAsync(Workflow(ProcessStep("slow", timeoutSeconds: 1)));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, result.Steps[0].Status);
        Assert.Equal("timeout", result.Steps[0].ErrorCode);
        Assert.True(lingering.WasKilled);
    }

    [Fact]
    public async Task Starting_a_workflow_that_is_already_active_returns_the_existing_run()
    {
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow(ProcessStep("wait")));

        var first = _engine.Start(workflow, _work);
        var second = _engine.Start(workflow, _work);

        Assert.True(second.WasAlreadyRunning);
        Assert.Equal(first.Execution.Id, second.Execution.Id);
        Assert.Single(_engine.ActiveExecutions);

        _engine.Cancel(first.Execution.Id);
        using var cts = new CancellationTokenSource(Timeout);
        await _engine.WaitForCompletionAsync(first.Execution.Id, cts.Token);

        var third = _engine.Start(workflow, _work);
        Assert.False(third.WasAlreadyRunning);
        Assert.NotEqual(first.Execution.Id, third.Execution.Id);
        _engine.Cancel(third.Execution.Id);
        await _engine.WaitForCompletionAsync(third.Execution.Id, cts.Token);
    }

    [Fact]
    public async Task maxParallelSteps_bounds_how_many_steps_run_at_once()
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

        // Serial: with maxParallelSteps 1 the second independent step must wait for the first.
        var serial = TestSupport.LoadValid(_registry, _work, Workflow($"{ProcessStep("one", onFailure: "continue")}, {ProcessStep("two", onFailure: "continue")}", maxParallel: 1));
        var started = _engine.Start(serial, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));
        await Task.Delay(300);
        Assert.Single(_launchers.Processes.Requests);

        fakes[0].KillTree(); // exit -1 → step one fails; "continue" lets step two start
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 2, Timeout));
        fakes[1].KillTree();
        using var cts = new CancellationTokenSource(Timeout);
        var result = await _engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);
        Assert.True(result.Steps[1].StartedAt >= result.Steps[0].FinishedAt);

        // Parallel: with maxParallelSteps 2 both start before either finishes.
        var parallel = TestSupport.LoadValid(_registry, _work, Workflow($"{ProcessStep("one", onFailure: "continue")}, {ProcessStep("two", onFailure: "continue")}", maxParallel: 2));
        var started2 = _engine.Start(parallel, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 4, Timeout));
        Assert.All(fakes.Skip(2), f => Assert.False(f.HasExited));
        _engine.Cancel(started2.Execution.Id);
        await _engine.WaitForCompletionAsync(started2.Execution.Id, cts.Token);
    }

    [Fact]
    public async Task MaxConcurrentWorkflows_queues_the_extra_run_until_a_slot_frees()
    {
        using var engine = new WorkflowEngine(_registry, _history, new EngineOptions(MaxConcurrentWorkflows: 1));
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        var a = TestSupport.LoadValid(_registry, _work, Workflow(ProcessStep("wait")).Replace("\"id\": \"wf\"", "\"id\": \"wf-a\"", StringComparison.Ordinal));
        var b = TestSupport.LoadValid(_registry, _work, Workflow(UrlStep("open")).Replace("\"id\": \"wf\"", "\"id\": \"wf-b\"", StringComparison.Ordinal));

        var runA = engine.Start(a, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => engine.GetExecution(runA.Execution.Id)!.Status == ExecutionStatus.Running, Timeout));
        var runB = engine.Start(b, _work);
        await Task.Delay(300);
        Assert.Equal(ExecutionStatus.Queued, engine.GetExecution(runB.Execution.Id)!.Status);
        Assert.Empty(_launchers.Urls.Opened);

        engine.Cancel(runA.Execution.Id);
        using var cts = new CancellationTokenSource(Timeout);
        var resultB = await engine.WaitForCompletionAsync(runB.Execution.Id, cts.Token);
        Assert.Equal(ExecutionStatus.Completed, resultB.Status);
        Assert.Single(_launchers.Urls.Opened);
    }

    [Fact]
    public void Refuses_a_workflow_whose_action_is_outside_the_profile_allowlist()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow($"{UrlStep("a")}, {ProcessStep("b")}"));
        var restricted = _root.Profile(ProfileId.Work, UrlOpenAction.ActionId, FolderOpenAction.ActionId);

        var ex = Assert.Throws<ActionNotAllowedException>(() => _engine.Start(workflow, restricted));

        Assert.Equal("b", ex.StepId);
        Assert.Equal(ProcessLaunchAction.ActionId, ex.ActionId);
        Assert.Equal(ProfileId.Work, ex.Profile);
        Assert.Contains("Work profile does not allow", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_engine.ActiveExecutions);
        Assert.Empty(_launchers.Processes.Requests);
        Assert.Empty(_history.GetRecentExecutions(null, 10));
    }

    [Fact]
    public void Refuses_to_run_a_work_workflow_under_the_personal_profile()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow(UrlStep("a")));
        var personal = _root.Profile(ProfileId.Personal, _registry);

        var ex = Assert.Throws<ActionNotAllowedException>(() => _engine.Start(workflow, personal));

        Assert.Contains("belongs to the Work profile", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_launchers.Urls.Opened);
    }

    [Fact]
    public async Task ShutdownAsync_cancels_every_active_run()
    {
        _launchers.Processes.Behavior = _ => new FakeProcess();
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow(ProcessStep("wait")));
        var started = _engine.Start(workflow, _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));

        await _engine.ShutdownAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(_engine.ActiveExecutions);
        Assert.Equal(ExecutionStatus.Cancelled, _engine.GetExecution(started.Execution.Id)!.Status);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _history.Dispose();
        _root.Dispose();
    }
}
