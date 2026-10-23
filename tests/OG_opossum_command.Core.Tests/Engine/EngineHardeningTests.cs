using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Engine;

/// <summary>Durability and race fixes in the engine: every transition persisted, no phantom or orphaned runs, late cancels, redaction.</summary>
public sealed class EngineHardeningTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly FlakyHistoryStore _history;
    private readonly Profile _work;
    private WorkflowEngine? _engine;

    public EngineHardeningTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _history = new FlakyHistoryStore(SqliteHistoryStore.Open(_root.Paths.DatabasePath));
        _work = _root.Profile(ProfileId.Work, _registry);
    }

    private WorkflowEngine Engine(int maxConcurrentWorkflows = 2) => _engine ??= new WorkflowEngine(_registry, _history, new EngineOptions(maxConcurrentWorkflows));

    private WorkflowDefinition Workflow(string id, string steps, int maxParallel = 1) => TestSupport.LoadValid(_registry, _work, $$"""
        {
          "schemaVersion": 1, "id": "{{id}}", "name": "{{id}}", "profileId": "work", "trigger": { "type": "manual" },
          "execution": { "maxParallelSteps": {{maxParallel}} },
          "steps": [ {{steps}} ]
        }
        """);

    private static string UrlStep(string id, string dependsOn = "") =>
        $$"""{ "id": "{{id}}", "action": "windows.url.open", "inputs": { "url": "https://{{id}}.example/" }, "dependsOn": [{{dependsOn}}] }""";

    private static string ProcessStep(string id) =>
        $$"""{ "id": "{{id}}", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\PING.EXE", "arguments": ["-n", "1", "127.0.0.1"], "waitForExit": true } }""";

    private async Task<ExecutionSnapshot> WaitAsync(Guid id)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await Engine().WaitForCompletionAsync(id, cts.Token);
    }

    [Fact]
    public async Task A_resumed_run_records_its_already_completed_steps_when_it_is_queued()
    {
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        var engine = Engine(maxConcurrentWorkflows: 1);
        var blocker = engine.Start(Workflow("blocker", ProcessStep("wait")), _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));

        var resumed = engine.Start(Workflow("wf", $"{UrlStep("a")}, {UrlStep("b", "\"a\"")}"), _work, new StartOptions(TriggerSources.Recovery, null, Guid.NewGuid(), new HashSet<string>(["a"], StringComparer.Ordinal)));

        // Still Queued behind the blocker, yet the journal already says "a" happened: a crash now cannot forget it.
        var journal = _history.GetExecution(resumed.Execution.Id)!;
        Assert.Equal(ExecutionStatus.Queued, journal.Status);
        Assert.Equal(StepStatus.Completed, journal.Steps.Single(s => s.Id == "a").Status);
        Assert.Contains("not repeated", journal.Steps.Single(s => s.Id == "a").Message, StringComparison.Ordinal);
        Assert.Equal(StepStatus.Pending, journal.Steps.Single(s => s.Id == "b").Status);

        engine.Cancel(blocker.Execution.Id);
        var result = await WaitAsync(resumed.Execution.Id);
        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(["b"], _launchers.Urls.Opened.Select(u => u.Host.Split('.')[0]));
    }

    [Fact]
    public async Task Cancel_persists_Cancelling_before_announcing_it()
    {
        _launchers.Processes.Behavior = _ => new FakeProcess();
        var engine = Engine();
        ExecutionStatus? journaled = null;
        engine.ExecutionChanged += (_, snapshot) =>
        {
            if (snapshot.Status == ExecutionStatus.Cancelling)
            {
                journaled = _history.GetExecution(snapshot.Id)!.Status;
            }
        };
        var started = engine.Start(Workflow("wf", ProcessStep("wait")), _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));

        Assert.True(engine.Cancel(started.Execution.Id));
        await WaitAsync(started.Execution.Id);

        Assert.Equal(ExecutionStatus.Cancelling, journaled);
    }

    [Fact]
    public async Task Start_is_refused_once_ShutdownAsync_has_begun()
    {
        _launchers.Processes.Behavior = _ => new FakeProcess();
        var engine = Engine();
        engine.Start(Workflow("first", ProcessStep("wait")), _work);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));

        var shutdown = engine.ShutdownAsync(Timeout);

        Assert.Throws<ObjectDisposedException>(() => engine.Start(Workflow("second", UrlStep("a")), _work));
        await shutdown;
        Assert.Empty(engine.ActiveExecutions);
        Assert.Empty(_launchers.Urls.Opened);
        Assert.Single(_history.GetRecentExecutions(null, 10));
    }

    [Fact]
    public async Task A_failed_first_save_leaves_no_phantom_run_that_blocks_the_workflow()
    {
        var engine = Engine();
        var workflow = Workflow("wf", UrlStep("a"));
        _history.FailNextSave(s => s.Status == ExecutionStatus.Queued);

        Assert.Throws<IOException>(() => engine.Start(workflow, _work));
        Assert.Empty(engine.ActiveExecutions);

        var retry = engine.Start(workflow, _work);
        Assert.False(retry.WasAlreadyRunning);
        Assert.Equal(ExecutionStatus.Completed, (await WaitAsync(retry.Execution.Id)).Status);
    }

    [Fact]
    public async Task A_bookkeeping_failure_in_one_step_stops_and_waits_for_its_running_sibling()
    {
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        var engine = Engine();
        _history.FailNextSave(s => s.Steps.Any(step => step.Id == "fast" && step.Status == StepStatus.Completed));

        var started = engine.Start(Workflow("wf", $"{ProcessStep("slow")}, {UrlStep("fast")}", maxParallel: 2), _work);
        var result = await WaitAsync(started.Execution.Id);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("Simulated disk failure", result.Error, StringComparison.Ordinal);
        Assert.True(lingering.WasKilled, "the sibling's process was left running");
        Assert.Equal(StepStatus.Cancelled, result.Steps.Single(s => s.Id == "slow").Status);
        Assert.False(engine.Cancel(started.Execution.Id));
        Assert.All(_history.GetExecution(started.Execution.Id)!.Steps, s => Assert.True(s.IsTerminal, $"{s.Id} is {s.Status}"));
    }

    [Fact]
    public async Task A_cancel_that_lands_after_the_last_step_completed_leaves_the_run_Completed()
    {
        var engine = Engine();
        var cancelled = false;
        var accepted = false;
        engine.ExecutionChanged += (_, snapshot) =>
        {
            if (!cancelled && snapshot.Status == ExecutionStatus.Running && snapshot.Steps.All(s => s.Status == StepStatus.Completed))
            {
                cancelled = true;
                accepted = engine.Cancel(snapshot.Id);
            }
        };

        var started = engine.Start(Workflow("wf", UrlStep("a")), _work);
        var result = await WaitAsync(started.Execution.Id);

        Assert.True(accepted, "the late cancel never landed");
        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(ExecutionStatus.Completed, _history.GetExecution(started.Execution.Id)!.Status);
    }

    [Fact]
    public async Task A_step_message_containing_a_secret_is_redacted_before_it_is_stored_or_published()
    {
        _launchers.Processes.Behavior = _ => throw new InvalidOperationException("login failed for password=hunter2");
        var engine = Engine();

        var started = engine.Start(Workflow("wf", ProcessStep("launch")), _work);
        var result = await WaitAsync(started.Execution.Id);

        var stored = _history.GetExecution(started.Execution.Id)!;
        foreach (var text in new[] { result.Steps[0].Message, result.Error, stored.Steps[0].Message, stored.Error })
        {
            Assert.NotNull(text);
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
            Assert.Contains("password=[redacted]", text, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _history.Dispose();
        _root.Dispose();
    }
}
