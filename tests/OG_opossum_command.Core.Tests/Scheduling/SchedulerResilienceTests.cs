using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Scheduling;

/// <summary>The scheduler keeps working when a subscriber throws, and dispatch outcomes never roll back trigger state.</summary>
public sealed class SchedulerResilienceTests : IDisposable
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
    private readonly Scheduler _scheduler;

    public SchedulerResilienceTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        _engine = new WorkflowEngine(_registry, _history, null, _clock);
        _work = _root.Profile(ProfileId.Work, _registry);
        _scheduler = new Scheduler(_history, _engine, () => [new ProfileWorkflows(_work, _workflows.ToList(), [])], _clock);
    }

    private void Add(string id, string trigger) => _workflows.Add(TestSupport.LoadValid(_registry, _work, $$"""
        {
          "schemaVersion": 2, "id": "{{id}}", "name": "{{id}}", "profileId": "work", "trigger": {{trigger}},
          "steps": [ { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } } ]
        }
        """));

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
    public async Task A_throwing_Fired_handler_neither_aborts_the_tick_nor_silences_other_subscribers_or_later_occurrences()
    {
        var kinds = new List<string>();
        var thrown = 0;
        _scheduler.Fired += (_, _) =>
        {
            Interlocked.Increment(ref thrown);
            throw new IOException("notification store failed (test)");
        };
        _scheduler.Fired += (_, e) =>
        {
            lock (kinds)
            {
                kinds.Add(e.Kind);
            }
        };
        Add("daily", """{ "type": "schedule", "at": ["08:30", "12:00"], "timeZone": "Pacific Standard Time", "missedRuns": "skip" }""");
        _scheduler.Reconcile(Start);

        // 11:00: the 08:30 occurrence expired, so the tick raises Skipped before it persists last_fired_for.
        await TickAndSettleAsync(Start.AddHours(3));
        var id = TriggerRecord.IdFor(ProfileId.Work, "daily");
        Assert.Equal(Start.AddMinutes(30), _history.GetTrigger(id)!.LastFiredFor);

        await TickAndSettleAsync(Start.AddHours(4).AddSeconds(2)); // 12:00:02

        Assert.Single(_launchers.Urls.Opened);
        Assert.Equal(Start.AddHours(4), _history.GetTrigger(id)!.LastFiredFor);
        lock (kinds)
        {
            Assert.Equal([SchedulerEventKinds.Skipped, SchedulerEventKinds.Started], kinds);
        }

        Assert.Equal(2, Volatile.Read(ref thrown));
    }

    [Fact]
    public void UpdateTriggerOutcome_changes_only_the_outcome_columns()
    {
        var firedFor = Start.AddMinutes(30);
        var trigger = new TriggerRecord(TriggerRecord.IdFor(ProfileId.Work, "daily"), ProfileId.Work, "daily", TriggerRecord.ScheduleType, true, "{}", "hash", Start.AddDays(1), firedFor, null, "Queued", Start);
        _history.UpsertTrigger(trigger);

        _history.UpdateTriggerOutcome(trigger.Id, Start.AddMinutes(31), "Started", Start.AddMinutes(31));
        _history.UpdateTriggerOutcome(trigger.Id, null, "Suppressed", Start.AddMinutes(32));

        var stored = _history.GetTrigger(trigger.Id)!;
        Assert.Equal(firedFor, stored.LastFiredFor);
        Assert.Equal(Start.AddDays(1), stored.NextDueAt);
        Assert.Equal("hash", stored.ConfigHash);
        Assert.Equal(Start.AddMinutes(31), stored.LastFiredAt);
        Assert.Equal("Suppressed", stored.LastOutcome);
        Assert.Equal(Start.AddMinutes(32), stored.UpdatedAt);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _engine.Dispose();
        _history.Dispose();
        _root.Dispose();
    }
}
