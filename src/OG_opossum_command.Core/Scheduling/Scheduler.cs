using System.Globalization;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Scheduling;

/// <summary>What the scheduler did about one trigger during a tick (for the UI and tests).</summary>
/// <param name="TriggerId">The trigger row.</param>
/// <param name="Profile">Owning profile.</param>
/// <param name="WorkflowId">The workflow.</param>
/// <param name="WorkflowName">Display name.</param>
/// <param name="Kind">One of <see cref="SchedulerEventKinds"/>.</param>
/// <param name="Message">One readable line.</param>
/// <param name="OccurredAt">The schedule occurrence concerned, when there is one.</param>
public sealed record SchedulerEvent(string TriggerId, ProfileId Profile, string WorkflowId, string WorkflowName, string Kind, string Message, DateTimeOffset? OccurredAt);

/// <summary>Values of <see cref="SchedulerEvent.Kind"/>.</summary>
public static class SchedulerEventKinds
{
    public const string Started = "started";
    public const string Suppressed = "suppressed";
    public const string Skipped = "skipped";
    public const string Paused = "paused";
    public const string Refused = "refused";
}

/// <summary>
/// Fires schedule triggers (spec §08, ADR-034). State lives in the <c>triggers</c> table; <see cref="Tick"/> is pure
/// bookkeeping over <see cref="OccurrenceCalculator"/> plus a per-trigger dispatch queue, so tests drive it with an
/// explicit clock. The background loop only calls <see cref="Tick"/> at the right moments.
/// </summary>
public sealed class Scheduler : IDisposable
{
    /// <summary>Occurrences older than this are never considered, however long the app was off.</summary>
    public static readonly TimeSpan MaxLookBack = TimeSpan.FromDays(7);

    /// <summary>Longest the loop sleeps between ticks even when nothing is due (catches clock jumps and wake-from-sleep).</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);

    private readonly IHistoryStore _history;
    private readonly WorkflowEngine _engine;
    private readonly Func<IReadOnlyList<ProfileWorkflows>> _workflows;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Lock _tickGate = new();
    private readonly Dictionary<string, Queue<PendingRun>> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _dispatchers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private bool _disposed;

    public Scheduler(IHistoryStore history, WorkflowEngine engine, Func<IReadOnlyList<ProfileWorkflows>> workflows, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(workflows);

        _history = history;
        _engine = engine;
        _workflows = workflows;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised for every decision (started, suppressed, skipped, paused). Fires on worker threads.</summary>
    public event EventHandler<SchedulerEvent>? Fired;

    /// <summary>Whether the operator paused every schedule (persisted setting). While paused, due occurrences are skipped, not deferred.</summary>
    public bool IsPaused
    {
        get => string.Equals(_history.GetSetting(SettingKeys.SchedulerPaused), "true", StringComparison.OrdinalIgnoreCase);
        set => _history.SetSetting(SettingKeys.SchedulerPaused, value ? "true" : "false");
    }

    /// <summary>Current trigger rows, for the UI.</summary>
    public IReadOnlyList<TriggerRecord> Triggers => _history.GetTriggers();

    /// <summary>
    /// Brings the <c>triggers</c> table in line with the loaded workflows: new or changed schedules start from
    /// <paramref name="now"/> (nothing before they existed counts as missed), unchanged ones keep their state, and
    /// rows whose workflow is gone or no longer scheduled are removed. Then ticks once.
    /// </summary>
    public void Reconcile(DateTimeOffset now)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (workflow, _) in ScheduledWorkflows())
        {
            var schedule = workflow.Schedule!;
            var id = TriggerRecord.IdFor(workflow.ProfileId, workflow.Id);
            live.Add(id);
            var existing = _history.GetTrigger(id);
            if (existing is not null && existing.ConfigHash == schedule.ConfigHash)
            {
                continue;
            }

            _history.UpsertTrigger(new TriggerRecord(
                id,
                workflow.ProfileId,
                workflow.Id,
                TriggerRecord.ScheduleType,
                existing?.Enabled ?? true,
                schedule.Describe(),
                schedule.ConfigHash,
                OccurrenceCalculator.NextAfter(schedule, now),
                now,
                existing?.LastFiredAt,
                existing is null ? "Schedule registered" : "Schedule changed; state reset",
                now));
        }

        foreach (var stale in _history.GetTriggers().Where(t => !live.Contains(t.Id)))
        {
            _history.DeleteTrigger(stale.Id);
        }

        Tick(now);
    }

    /// <summary>
    /// Evaluates every enabled trigger at <paramref name="now"/>: occurrences since <c>last_fired_for</c> that are
    /// still inside their expiry window are started; older ones follow the missed-run policy. <c>last_fired_for</c>
    /// is persisted <b>before</b> any run starts, so a crash loses at most one run and never repeats one.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_tickGate)
        {
            TickCore(now);
        }
    }

    /// <summary>Only ever runs under <see cref="_tickGate"/>, so the loop and a reload can never account for the same occurrence twice.</summary>
    private void TickCore(DateTimeOffset now)
    {
        var paused = IsPaused;
        foreach (var (workflow, profile) in ScheduledWorkflows())
        {
            var schedule = workflow.Schedule!;
            var trigger = _history.GetTrigger(TriggerRecord.IdFor(workflow.ProfileId, workflow.Id));
            if (trigger is null || !trigger.Enabled)
            {
                continue;
            }

            var floor = trigger.LastFiredFor ?? now;
            if (floor < now - MaxLookBack)
            {
                floor = now - MaxLookBack;
            }

            var due = OccurrenceCalculator.OccurrencesBetween(schedule, floor, now);
            var next = OccurrenceCalculator.NextAfter(schedule, now);
            if (due.Count == 0)
            {
                if (trigger.NextDueAt != next)
                {
                    _history.UpsertTrigger(trigger with { NextDueAt = next, UpdatedAt = now });
                }

                continue;
            }

            var onTime = due.Where(d => now - d <= schedule.ExpiresAfter).ToList();
            var missed = due.Where(d => now - d > schedule.ExpiresAfter).ToList();
            var toRun = new List<PendingRun>();
            string outcome;

            if (paused)
            {
                outcome = $"Paused: skipped {due.Count.ToString(CultureInfo.InvariantCulture)} occurrence(s)";
                Raise(trigger, workflow, SchedulerEventKinds.Paused, $"{workflow.Name}: scheduler paused, skipped {Describe(due)}", due[^1]);
            }
            else
            {
                if (missed.Count > 0)
                {
                    switch (schedule.MissedRuns)
                    {
                        case MissedRunPolicy.Skip:
                            Raise(trigger, workflow, SchedulerEventKinds.Skipped, $"{workflow.Name}: skipped {Describe(missed)} missed while the app was not running", missed[^1]);
                            break;
                        case MissedRunPolicy.RunOnce:
                            toRun.Add(new PendingRun(missed[^1], IsCatchUp: true));
                            break;
                        case MissedRunPolicy.CatchUpLimited:
                            var catchUp = missed.TakeLast(schedule.CatchUpLimit).ToList();
                            if (catchUp.Count < missed.Count)
                            {
                                Raise(trigger, workflow, SchedulerEventKinds.Skipped, $"{workflow.Name}: catching up the last {catchUp.Count.ToString(CultureInfo.InvariantCulture)} of {missed.Count.ToString(CultureInfo.InvariantCulture)} missed occurrences", missed[^1]);
                            }

                            toRun.AddRange(catchUp.Select(c => new PendingRun(c, IsCatchUp: true)));
                            break;
                    }
                }

                toRun.AddRange(onTime.Select(o => new PendingRun(o, IsCatchUp: false)));
                outcome = toRun.Count == 0
                    ? $"Skipped {missed.Count.ToString(CultureInfo.InvariantCulture)} missed occurrence(s) ({schedule.MissedRuns})"
                    : $"Queued {toRun.Count.ToString(CultureInfo.InvariantCulture)} run(s) for {Describe(toRun.Select(r => r.Occurrence).ToList())}";
            }

            // Account for every occurrence before anything starts: a crash from here on loses a run, never repeats one.
            _history.UpsertTrigger(trigger with { LastFiredFor = due[^1], NextDueAt = next, LastOutcome = outcome, UpdatedAt = now });

            if (toRun.Count > 0)
            {
                Enqueue(trigger, workflow, profile, toRun);
            }
        }
    }

    /// <summary>Starts the background loop: tick now, then sleep until the next occurrence (at most <see cref="MaxSleep"/>).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _loop ??= Task.Run(LoopAsync);
        }
    }

    /// <summary>Completes once every queued run has been dispatched (tests).</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task[] dispatchers;
            lock (_gate)
            {
                dispatchers = _dispatchers.Values.ToArray();
            }

            if (dispatchers.Length == 0)
            {
                return;
            }

            await Task.WhenAll(dispatchers).WaitAsync(cts.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task LoopAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = _time.GetUtcNow();
                Tick(now);
                var next = _history.GetTriggers().Where(t => t.Enabled && t.NextDueAt is not null).Select(t => t.NextDueAt!.Value).DefaultIfEmpty(now + MaxSleep).Min();
                var delay = next - _time.GetUtcNow();
                if (delay > MaxSleep)
                {
                    delay = MaxSleep;
                }

                if (delay < TimeSpan.FromSeconds(1))
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                await Task.Delay(delay, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Store closed under us (shutdown); stop quietly.
                return;
            }
        }
    }

    private void Enqueue(TriggerRecord trigger, WorkflowDefinition workflow, Profile profile, List<PendingRun> runs)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(trigger.Id, out var queue))
            {
                queue = new Queue<PendingRun>();
                _queues[trigger.Id] = queue;
            }

            foreach (var run in runs)
            {
                queue.Enqueue(run);
            }

            if (!_dispatchers.ContainsKey(trigger.Id))
            {
                _dispatchers[trigger.Id] = Task.Run(() => DispatchAsync(trigger.Id, workflow, profile));
            }
        }
    }

    private async Task DispatchAsync(string triggerId, WorkflowDefinition workflow, Profile profile)
    {
        try
        {
            while (true)
            {
                PendingRun pending;
                lock (_gate)
                {
                    if (!_queues.TryGetValue(triggerId, out var queue) || queue.Count == 0)
                    {
                        _dispatchers.Remove(triggerId);
                        return;
                    }

                    pending = queue.Dequeue();
                }

                var trigger = _history.GetTrigger(triggerId);
                if (trigger is null)
                {
                    continue;
                }

                StartResult started;
                try
                {
                    started = _engine.Start(workflow, profile, new StartOptions(TriggerSources.Schedule, pending.Occurrence));
                }
                catch (ActionNotAllowedException ex)
                {
                    Raise(trigger, workflow, SchedulerEventKinds.Refused, ex.Message, pending.Occurrence);
                    _history.UpsertTrigger(trigger with { LastOutcome = $"Refused: {ex.Message}", UpdatedAt = _time.GetUtcNow() });
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var now = _time.GetUtcNow();
                if (started.WasAlreadyRunning)
                {
                    // Duplicate suppression: the workflow is still active, so the occurrence is dropped, not queued (focus-existing).
                    Raise(trigger, workflow, SchedulerEventKinds.Suppressed, $"{workflow.Name}: still running, so the {pending.Occurrence:HH:mm} occurrence was not started again", pending.Occurrence);
                    _history.UpsertTrigger(trigger with { LastOutcome = $"Suppressed {pending.Occurrence:u}: already running", UpdatedAt = now });
                    continue;
                }

                Raise(trigger, workflow, SchedulerEventKinds.Started, pending.IsCatchUp ? $"{workflow.Name}: catching up the {pending.Occurrence:HH:mm} run" : $"{workflow.Name}: scheduled run started", pending.Occurrence);
                _history.UpsertTrigger(trigger with { LastFiredAt = now, LastOutcome = $"Started {pending.Occurrence:u} as run {started.Execution.Id}", UpdatedAt = now });

                if (pending.IsCatchUp)
                {
                    // Catch-up runs go one after another; otherwise focus-existing would collapse them into one.
                    try
                    {
                        await _engine.WaitForCompletionAsync(started.Execution.Id, _stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Shutting down.
            lock (_gate)
            {
                _dispatchers.Remove(triggerId);
            }
        }
    }

    private IEnumerable<(WorkflowDefinition Workflow, Profile Profile)> ScheduledWorkflows()
    {
        foreach (var loaded in _workflows())
        {
            foreach (var workflow in loaded.Workflows.Where(w => w.Schedule is not null))
            {
                yield return (workflow, loaded.Profile);
            }
        }
    }

    private void Raise(TriggerRecord trigger, WorkflowDefinition workflow, string kind, string message, DateTimeOffset? occurrence) =>
        Fired?.Invoke(this, new SchedulerEvent(trigger.Id, trigger.Profile, workflow.Id, workflow.Name, kind, message, occurrence));

    private static string Describe(IReadOnlyList<DateTimeOffset> occurrences) =>
        occurrences.Count == 1
            ? occurrences[0].ToString("u", CultureInfo.InvariantCulture)
            : $"{occurrences.Count.ToString(CultureInfo.InvariantCulture)} occurrences ({occurrences[0]:u} … {occurrences[^1]:u})";

    private readonly record struct PendingRun(DateTimeOffset Occurrence, bool IsCatchUp);
}
