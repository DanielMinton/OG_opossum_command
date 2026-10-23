using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Scheduling;

/// <summary>Drives the scheduler with an explicit clock and explicit ticks; the background loop is never started here.</summary>
public sealed class SchedulerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // Thu 22 Oct 2026 08:00 PDT = 15:00 UTC.
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly SqliteHistoryStore _history;
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly WorkflowEngine _engine;
    private readonly Profile _work;
    private readonly List<WorkflowDefinition> _workflows = [];
    private readonly List<SchedulerEvent> _events = [];
    private readonly Scheduler _scheduler;

    public SchedulerTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        _engine = new WorkflowEngine(_registry, _history, null, _clock);
        _work = _root.Profile(ProfileId.Work, _registry);
        _scheduler = new Scheduler(_history, _engine, () => [new ProfileWorkflows(_work, _workflows.ToList(), [])], _clock);
        _scheduler.Fired += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    private static string Workflow(string id, string trigger, string step) => $$"""
        {
          "schemaVersion": 2, "id": "{{id}}", "name": "{{id}}", "profileId": "work",
          "trigger": {{trigger}},
          "steps": [ {{step}} ]
        }
        """;

    private const string UrlStep = """{ "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } }""";
    private const string ProcessStep = """{ "id": "wait", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\cmd.exe", "arguments": ["/c", "echo"], "waitForExit": true } }""";

    private WorkflowDefinition Add(string id, string trigger, string step = UrlStep)
    {
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow(id, trigger, step));
        _workflows.Add(workflow);
        return workflow;
    }

    private List<ExecutionSnapshot> Runs(string workflowId) =>
        _history.GetRecentExecutions(ProfileId.Work, 50).Where(e => e.WorkflowId == workflowId).OrderBy(e => e.QueuedAt).ToList();

    private List<string> Kinds()
    {
        lock (_events)
        {
            return _events.Select(e => e.Kind).ToList();
        }
    }

    private async Task TickAndSettleAsync(DateTimeOffset now)
    {
        _clock.Set(now);
        _scheduler.Tick(now);
        await _scheduler.WaitForIdleAsync(Timeout);
        foreach (var run in _engine.ActiveExecutions)
        {
            using var cts = new CancellationTokenSource(Timeout);
            await _engine.WaitForCompletionAsync(run.Id, cts.Token);
        }
    }

    [Fact]
    public async Task Fires_an_occurrence_that_is_due_and_records_the_run_as_scheduled()
    {
        Add("daily", """{ "type": "schedule", "at": ["08:30"], "timeZone": "Pacific Standard Time" }""");
        _scheduler.Reconcile(Start);
        Assert.Empty(Runs("daily"));

        await TickAndSettleAsync(Start.AddMinutes(29)); // 08:29: nothing yet
        Assert.Empty(Runs("daily"));

        await TickAndSettleAsync(Start.AddMinutes(30).AddSeconds(2)); // 08:30:02

        var run = Assert.Single(Runs("daily"));
        Assert.Equal(TriggerSources.Schedule, run.TriggerSource);
        Assert.Equal(Start.AddMinutes(30), run.ScheduledFor);
        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Single(_launchers.Urls.Opened);
        var trigger = _history.GetTrigger(TriggerRecord.IdFor(ProfileId.Work, "daily"))!;
        Assert.Equal(Start.AddMinutes(30), trigger.LastFiredFor);
        Assert.Equal(Start.AddHours(24).AddMinutes(30), trigger.NextDueAt);
        Assert.Equal([SchedulerEventKinds.Started], Kinds());
    }

    [Fact]
    public async Task The_same_occurrence_never_fires_twice_even_across_a_restart()
    {
        Add("daily", """{ "type": "schedule", "at": ["08:30"], "timeZone": "Pacific Standard Time" }""");
        _scheduler.Reconcile(Start);
        var due = Start.AddMinutes(30);

        await TickAndSettleAsync(due.AddSeconds(1));
        await TickAndSettleAsync(due.AddSeconds(1)); // same instant again
        await TickAndSettleAsync(due.AddMinutes(2)); // a little later, still inside the expiry window
        Assert.Single(Runs("daily"));

        // Restart: a fresh scheduler over the same database and clock must not fire the occurrence again.
        using var restarted = new Scheduler(_history, _engine, () => [new ProfileWorkflows(_work, _workflows.ToList(), [])], _clock);
        restarted.Reconcile(due.AddMinutes(3));
        await restarted.WaitForIdleAsync(Timeout);
        restarted.Tick(due.AddMinutes(4));
        await restarted.WaitForIdleAsync(Timeout);

        Assert.Single(Runs("daily"));
        Assert.Single(_launchers.Urls.Opened);
    }

    [Fact]
    public async Task Skip_policy_drops_occurrences_missed_while_the_app_was_off()
    {
        Add("daily", """{ "type": "schedule", "at": ["08:30"], "timeZone": "Pacific Standard Time", "missedRuns": "skip" }""");
        _scheduler.Reconcile(Start);

        await TickAndSettleAsync(Start.AddHours(3)); // 11:00, three hours past 08:30

        Assert.Empty(Runs("daily"));
        Assert.Equal([SchedulerEventKinds.Skipped], Kinds());
        var trigger = _history.GetTrigger(TriggerRecord.IdFor(ProfileId.Work, "daily"))!;
        Assert.Equal(Start.AddMinutes(30), trigger.LastFiredFor);
        Assert.Contains("Skipped", trigger.LastOutcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_policy_runs_a_single_catch_up_however_many_occurrences_were_missed()
    {
        Add("hourly", """{ "type": "schedule", "at": ["09:00", "10:00", "11:00"], "timeZone": "Pacific Standard Time", "missedRuns": "run-once" }""");
        _scheduler.Reconcile(Start);

        await TickAndSettleAsync(Start.AddHours(5)); // 13:00: three occurrences missed

        var run = Assert.Single(Runs("hourly"));
        Assert.Equal(Start.AddHours(3), run.ScheduledFor); // the most recent one (11:00)
        Assert.Equal([SchedulerEventKinds.Started], Kinds());
    }

    [Fact]
    public async Task CatchUpLimited_runs_the_most_recent_missed_occurrences_one_after_another_up_to_the_limit()
    {
        Add("hourly", """{ "type": "schedule", "at": ["09:00", "10:00", "11:00", "12:00"], "timeZone": "Pacific Standard Time", "missedRuns": "catch-up-limited", "catchUpLimit": 2 }""");
        _scheduler.Reconcile(Start);

        await TickAndSettleAsync(Start.AddHours(6)); // 14:00: four missed, limit two

        var runs = Runs("hourly").OrderBy(r => r.ScheduledFor).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal([Start.AddHours(3), Start.AddHours(4)], runs.Select(r => r.ScheduledFor));
        Assert.All(runs, r => Assert.Equal(ExecutionStatus.Completed, r.Status));
        // Sequential, not collapsed by focus-existing: the second run was queued only after the first had completed (event ids are the journal order).
        var firstCompleted = _history.GetEvents(runs[0].Id).Single(e => e.EventType == "execution.completed").Id;
        var secondQueued = _history.GetEvents(runs[1].Id).Single(e => e.EventType == "execution.queued").Id;
        Assert.True(secondQueued > firstCompleted, "catch-up runs must run one after another");
        Assert.Equal([SchedulerEventKinds.Skipped, SchedulerEventKinds.Started, SchedulerEventKinds.Started], Kinds());
    }

    [Fact]
    public async Task An_occurrence_due_while_the_workflow_is_still_running_is_suppressed_not_queued()
    {
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        Add("long", """{ "type": "schedule", "at": ["08:30", "08:35"], "timeZone": "Pacific Standard Time" }""", ProcessStep);
        _scheduler.Reconcile(Start);

        _clock.Set(Start.AddMinutes(30).AddSeconds(1));
        _scheduler.Tick(_clock.GetUtcNow());
        await _scheduler.WaitForIdleAsync(Timeout);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));

        _clock.Set(Start.AddMinutes(35).AddSeconds(1));
        _scheduler.Tick(_clock.GetUtcNow());
        await _scheduler.WaitForIdleAsync(Timeout);

        Assert.Equal([SchedulerEventKinds.Started, SchedulerEventKinds.Suppressed], Kinds());
        Assert.Single(Runs("long"));
        Assert.Single(_launchers.Processes.Requests);
        lingering.KillTree();
        foreach (var run in _engine.ActiveExecutions)
        {
            using var cts = new CancellationTokenSource(Timeout);
            await _engine.WaitForCompletionAsync(run.Id, cts.Token);
        }
    }

    [Fact]
    public async Task While_paused_due_occurrences_are_skipped_and_do_not_fire_after_unpausing()
    {
        Add("daily", """{ "type": "schedule", "at": ["08:30"], "timeZone": "Pacific Standard Time", "missedRuns": "run-once" }""");
        _scheduler.Reconcile(Start);
        _scheduler.IsPaused = true;

        await TickAndSettleAsync(Start.AddMinutes(31));
        Assert.Empty(Runs("daily"));
        Assert.Equal([SchedulerEventKinds.Paused], Kinds());

        _scheduler.IsPaused = false;
        await TickAndSettleAsync(Start.AddMinutes(32));
        Assert.Empty(Runs("daily"));
    }

    [Fact]
    public async Task Reconcile_registers_new_schedules_from_now_resets_changed_ones_and_removes_stale_rows()
    {
        var original = Add("daily", """{ "type": "schedule", "at": ["08:30"], "timeZone": "Pacific Standard Time" }""");
        Add("manual", """{ "type": "manual" }""");
        _scheduler.Reconcile(Start);

        var id = TriggerRecord.IdFor(ProfileId.Work, "daily");
        var registered = Assert.Single(_scheduler.Triggers);
        Assert.Equal(id, registered.Id);
        Assert.Equal(Start, registered.LastFiredFor);
        Assert.Equal(Start.AddMinutes(30), registered.NextDueAt);
        Assert.Contains("08:30 every day (Pacific Standard Time)", registered.ConfigJson, StringComparison.Ordinal);

        await TickAndSettleAsync(Start.AddMinutes(31));
        Assert.Single(Runs("daily"));

        // Same schedule again: state kept.
        _scheduler.Reconcile(Start.AddMinutes(32));
        Assert.Equal(Start.AddMinutes(30), _history.GetTrigger(id)!.LastFiredFor);

        // Changed schedule: state reset to now, so the 08:30 occurrence is not reconsidered under the new config.
        _workflows.Remove(original);
        Add("daily", """{ "type": "schedule", "at": ["08:30", "17:00"], "timeZone": "Pacific Standard Time" }""");
        _scheduler.Reconcile(Start.AddMinutes(33));
        var reset = _history.GetTrigger(id)!;
        Assert.Equal(Start.AddMinutes(33), reset.LastFiredFor);
        Assert.Equal(Start.AddHours(9), reset.NextDueAt);

        // Schedule removed from the file: row deleted.
        _workflows.Clear();
        _scheduler.Reconcile(Start.AddMinutes(34));
        Assert.Empty(_scheduler.Triggers);
    }

    [Fact]
    public async Task Occurrences_older_than_the_look_back_window_are_ignored()
    {
        Add("daily", """{ "type": "schedule", "at": ["08:30"], "timeZone": "Pacific Standard Time", "missedRuns": "catch-up-limited", "catchUpLimit": 10 }""");
        _scheduler.Reconcile(Start);

        await TickAndSettleAsync(Start.AddDays(30)); // a month off: only the last 7 days are considered, not 30

        Assert.Equal(7, Runs("daily").Count);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _engine.Dispose();
        _history.Dispose();
        _root.Dispose();
    }
}
