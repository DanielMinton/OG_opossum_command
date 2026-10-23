using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Engine;

/// <summary>
/// Runs workflows: execution IDs, the spec §06 state machine, dependsOn ordering, bounded parallelism, resource
/// locks, timeouts, cancellation that reaches launched process trees, the focus-existing duplicate policy, and
/// durable, redacted history. Everything runs on the thread pool; <see cref="ExecutionChanged"/> fires on
/// worker threads with immutable snapshots.
/// </summary>
public sealed class WorkflowEngine : IDisposable
{
    private readonly ActionRegistry _registry;
    private readonly IHistoryStore _history;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _workflowSlots;
    private readonly Dictionary<Guid, Run> _runs = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    public WorkflowEngine(ActionRegistry registry, IHistoryStore history, EngineOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(history);

        Options = options ?? new EngineOptions();
        if (Options.MaxConcurrentWorkflows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentWorkflows must be at least 1.");
        }

        _registry = registry;
        _history = history;
        _time = time ?? TimeProvider.System;
        _workflowSlots = new SemaphoreSlim(Options.MaxConcurrentWorkflows, Options.MaxConcurrentWorkflows);
    }

    /// <summary>Raised after every state change with a fresh snapshot. Fires on worker threads.</summary>
    public event EventHandler<ExecutionSnapshot>? ExecutionChanged;

    public EngineOptions Options { get; }

    /// <summary>Engine-wide resource locks shared by every run (ADR-033).</summary>
    public ResourceLocks Locks { get; } = new();

    /// <summary>Runs that are Queued, Running, or Cancelling, oldest first.</summary>
    public IReadOnlyList<ExecutionSnapshot> ActiveExecutions
    {
        get
        {
            lock (_gate)
            {
                return _runs.Values.Select(r => r.Snapshot()).Where(s => s.IsActive).OrderBy(s => s.QueuedAt).ToList();
            }
        }
    }

    /// <summary>The current snapshot of a run this engine instance knows about (active or finished since start).</summary>
    public ExecutionSnapshot? GetExecution(Guid executionId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(executionId, out var run) ? run.Snapshot() : null;
        }
    }

    /// <summary>
    /// Queues a run. Refuses (throws) when any step uses an action outside the profile's allowlist; returns the
    /// existing run when one for the same workflow is still active (focus-existing).
    /// </summary>
    /// <exception cref="ActionNotAllowedException">A step's action is not allowed in <paramref name="profile"/>.</exception>
    public StartResult Start(WorkflowDefinition workflow, Profile profile, StartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= StartOptions.Manual;

        if (workflow.ProfileId != profile.Id)
        {
            throw new ActionNotAllowedException($"Workflow '{workflow.Id}' belongs to the {workflow.ProfileId.DisplayName()} profile and cannot run under {profile.DisplayName}.");
        }

        foreach (var step in workflow.Steps)
        {
            if (!profile.IsActionAllowed(step.ActionId))
            {
                throw new ActionNotAllowedException(profile.Id, workflow.Id, step.Id, step.ActionId);
            }

            if (!_registry.Contains(step.ActionId))
            {
                throw new ActionNotAllowedException($"Workflow '{workflow.Id}' step '{step.Id}' uses unknown action '{step.ActionId}'.");
            }
        }

        Run run;
        lock (_gate)
        {
            var existing = _runs.Values.FirstOrDefault(r => r.Workflow.Id == workflow.Id && r.Profile.Id == profile.Id && r.Snapshot().IsActive);
            if (existing is not null)
            {
                return new StartResult(existing.Snapshot(), WasAlreadyRunning: true);
            }

            run = new Run(Guid.NewGuid(), workflow, profile, options, _time.GetUtcNow());
            _runs[run.Id] = run;
        }

        var snapshot = run.Snapshot();
        _history.SaveExecution(snapshot);
        AppendEvent(run, null, EventSeverity.Info, "execution.queued", options.TriggerSource switch
        {
            TriggerSources.Schedule => $"Queued {workflow.Name} (scheduled for {options.ScheduledFor:u})",
            TriggerSources.Recovery => $"Queued {workflow.Name} (resuming run {options.ResumedFrom}; {options.AlreadyCompletedSteps?.Count ?? 0} step(s) already done)",
            _ => $"Queued {workflow.Name}",
        }, null);
        ExecutionChanged?.Invoke(this, snapshot);

        _ = Task.Run(() => RunAsync(run));
        return new StartResult(snapshot, WasAlreadyRunning: false);
    }

    /// <summary>Requests cancellation. Returns false when the run is unknown or already finished.</summary>
    public bool Cancel(Guid executionId)
    {
        Run? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(executionId, out run) || !run.Snapshot().IsActive)
            {
                return false;
            }
        }

        var changed = run.Update(state =>
        {
            if (state.Status is ExecutionStatus.Queued or ExecutionStatus.Running)
            {
                state.Status = ExecutionStatus.Cancelling;
                return true;
            }

            return false;
        });

        if (changed)
        {
            Publish(run);
            AppendEvent(run, null, EventSeverity.Warning, "execution.cancelling", "Cancel requested", null);
        }

        run.Cancellation.Cancel();
        return true;
    }

    /// <summary>Completes when the run reaches a terminal state.</summary>
    public async Task<ExecutionSnapshot> WaitForCompletionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        Run run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(executionId, out run!))
            {
                throw new KeyNotFoundException($"Unknown execution {executionId}.");
            }
        }

        await run.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return run.Snapshot();
    }

    /// <summary>Cancels every active run and waits up to <paramref name="grace"/> for them to settle.</summary>
    public async Task ShutdownAsync(TimeSpan grace)
    {
        List<Run> active;
        lock (_gate)
        {
            active = _runs.Values.Where(r => r.Snapshot().IsActive).ToList();
        }

        foreach (var run in active)
        {
            Cancel(run.Id);
        }

        using var timeout = new CancellationTokenSource(grace);
        try
        {
            await Task.WhenAll(active.Select(r => r.Completion.Task)).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Grace period elapsed; whatever is left is reconciled as Interrupted on the next start.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Run> runs;
        lock (_gate)
        {
            runs = _runs.Values.ToList();
        }

        foreach (var run in runs)
        {
            run.Cancellation.Cancel();
            run.Cancellation.Dispose();
        }

        _workflowSlots.Dispose();
    }

    private async Task RunAsync(Run run)
    {
        var token = run.Cancellation.Token;
        var slotAcquired = false;
        try
        {
            await _workflowSlots.WaitAsync(token).ConfigureAwait(false);
            slotAcquired = true;

            var now = _time.GetUtcNow();
            run.Update(state =>
            {
                state.Status = ExecutionStatus.Running;
                state.StartedAt = now;
                foreach (var step in state.Steps.Values.Where(s => run.Options.AlreadyCompletedSteps?.Contains(s.Definition.Id) == true))
                {
                    // Its side effect happened in the run being resumed; never repeat it (spec §13).
                    step.Status = StepStatus.Completed;
                    step.StartedAt = now;
                    step.FinishedAt = now;
                    step.Message = "Completed in the interrupted run; not repeated";
                }

                return true;
            });
            Publish(run);
            AppendEvent(run, null, EventSeverity.Info, "execution.started", $"Started {run.Workflow.Name}", null);

            await RunStepsAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancelled while queued or between steps; finalised below.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Engine or history failure: record it on the run rather than losing the run silently.
            run.Update(state =>
            {
                state.Error = $"{ex.GetType().Name}: {ex.Message}";
                return true;
            });
        }
        finally
        {
            if (slotAcquired && !_disposed)
            {
                _workflowSlots.Release();
            }

            Finalize(run);
        }
    }

    private async Task RunStepsAsync(Run run)
    {
        var token = run.Cancellation.Token;
        var pending = run.Workflow.Steps.Where(s => run.StepStatusOf(s.Id) == StepStatus.Pending).Select(s => s.Id).ToList();
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        var stopRequested = false;

        while (pending.Count > 0 || running.Count > 0)
        {
            if (token.IsCancellationRequested)
            {
                foreach (var id in pending)
                {
                    run.SetStep(id, StepStatus.Cancelled, _time.GetUtcNow(), null, "Run cancelled before this step started", null);
                }

                pending.Clear();
            }
            else if (stopRequested)
            {
                foreach (var id in pending)
                {
                    run.SetStep(id, StepStatus.Skipped, _time.GetUtcNow(), "stopped", "Skipped because an earlier step failed", null);
                }

                pending.Clear();
            }
            else
            {
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var id in pending.ToList())
                    {
                        var step = run.Workflow.FindStep(id)!;
                        var dependencies = step.DependsOn.Select(run.StepStatusOf).ToList();
                        if (dependencies.Any(d => d is StepStatus.Failed or StepStatus.Skipped or StepStatus.Cancelled))
                        {
                            run.SetStep(id, StepStatus.Skipped, _time.GetUtcNow(), "dependency-failed", "Skipped because a step it depends on did not complete", null);
                            pending.Remove(id);
                            changed = true;
                        }
                        else if (dependencies.All(d => d == StepStatus.Completed) && running.Count < run.Workflow.MaxParallelSteps)
                        {
                            running[id] = RunStepAsync(run, step);
                            pending.Remove(id);
                            changed = true;
                        }
                    }
                }

                if (pending.Count > 0 || running.Count > 0)
                {
                    Publish(run);
                }
            }

            if (running.Count == 0)
            {
                if (pending.Count > 0)
                {
                    // Dependencies validated acyclic at load time, so this cannot happen; guard against a hang anyway.
                    foreach (var id in pending)
                    {
                        run.SetStep(id, StepStatus.Skipped, _time.GetUtcNow(), "unreachable", "Skipped: its dependencies can never complete", null);
                    }

                    pending.Clear();
                }

                break;
            }

            var finished = await Task.WhenAny(running.Values).ConfigureAwait(false);
            var finishedId = running.First(kv => kv.Value == finished).Key;
            running.Remove(finishedId);
            await finished.ConfigureAwait(false);

            var finishedStep = run.Workflow.FindStep(finishedId)!;
            if (run.StepStatusOf(finishedId) == StepStatus.Failed && finishedStep.OnFailure == OnFailurePolicy.Stop)
            {
                stopRequested = true;
            }
        }
    }

    private async Task RunStepAsync(Run run, StepDefinition step)
    {
        var action = _registry.TryGet(step.ActionId, out var found) ? found : throw new InvalidOperationException($"Action '{step.ActionId}' vanished from the registry.");
        var timeout = step.Timeout ?? action.DefaultTimeout;
        var startedAt = _time.GetUtcNow();

        run.SetStep(step.Id, StepStatus.Running, null, null, step.Locks.Count > 0 ? $"Waiting for {string.Join(", ", step.Locks)}" : null, null, startedAt);
        _history.SaveExecution(run.Snapshot());
        AppendEvent(run, step.Id, EventSeverity.Info, "step.started", $"{step.Name} ({step.ActionId})", null);
        Publish(run);

        using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token);
        stepCancellation.CancelAfter(timeout);

        StepStatus status;
        string? errorCode = null;
        string? message;
        string? output = null;
        var severity = EventSeverity.Info;
        try
        {
            // Locks are taken under the step's own token, so Cancel while waiting releases nothing it did not take (ADR-033).
            using var held = await Locks.AcquireAsync(step.Locks, stepCancellation.Token).ConfigureAwait(false);
            if (step.Locks.Count > 0)
            {
                run.SetStep(step.Id, StepStatus.Running, null, null, null, null);
                AppendEvent(run, step.Id, EventSeverity.Info, "step.locked", $"Holding {string.Join(", ", step.Locks)}", null);
                Publish(run);
            }

            var context = new ActionContext(run.Id, run.Workflow.Id, step.Id, run.Profile);
            var result = await action.ExecuteAsync(step.Inputs, context, stepCancellation.Token).ConfigureAwait(false);
            status = result.Succeeded ? StepStatus.Completed : StepStatus.Failed;
            errorCode = result.ErrorCode;
            message = result.Message;
            output = OutputRedactor.Redact(result.Output);
            severity = result.Succeeded ? EventSeverity.Info : EventSeverity.Error;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            status = StepStatus.Cancelled;
            message = "Cancelled";
            severity = EventSeverity.Warning;
        }
        catch (OperationCanceledException)
        {
            status = StepStatus.Failed;
            errorCode = "timeout";
            message = $"Timed out after {timeout.TotalSeconds:0} s";
            severity = EventSeverity.Error;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = StepStatus.Failed;
            errorCode = "action-error";
            message = OutputRedactor.Redact($"{ex.GetType().Name}: {ex.Message}") ?? ex.GetType().Name;
            severity = EventSeverity.Error;
        }

        var finishedAt = _time.GetUtcNow();
        run.SetStep(step.Id, status, finishedAt, errorCode, message, output);
        _history.SaveExecution(run.Snapshot());
        AppendEvent(run, step.Id, severity, $"step.{status.ToString().ToLowerInvariant()}", message ?? status.ToString(), (long)(finishedAt - startedAt).TotalMilliseconds);
        Publish(run);
    }

    private void Finalize(Run run)
    {
        var now = _time.GetUtcNow();
        run.Update(state =>
        {
            foreach (var step in state.Steps.Values.Where(s => s.Status is StepStatus.Pending or StepStatus.Running))
            {
                step.Status = run.Cancellation.IsCancellationRequested ? StepStatus.Cancelled : StepStatus.Skipped;
                step.FinishedAt = now;
            }

            if (run.Cancellation.IsCancellationRequested)
            {
                state.Status = ExecutionStatus.Cancelled;
            }
            else if (state.Error is not null || state.Steps.Values.Any(s => s.Status is StepStatus.Failed or StepStatus.Skipped))
            {
                state.Status = ExecutionStatus.Failed;
                state.Error ??= state.Steps.Values.FirstOrDefault(s => s.Status == StepStatus.Failed)?.Message;
            }
            else
            {
                state.Status = ExecutionStatus.Completed;
            }

            state.FinishedAt = now;
            return true;
        });

        var snapshot = run.Snapshot();
        try
        {
            _history.SaveExecution(snapshot);
            AppendEvent(
                run,
                null,
                snapshot.Status == ExecutionStatus.Completed ? EventSeverity.Info : EventSeverity.Warning,
                $"execution.{snapshot.Status.ToString().ToLowerInvariant()}",
                snapshot.Error ?? $"{snapshot.Status}: {snapshot.CompletedStepCount} of {snapshot.Steps.Count} steps completed",
                snapshot.StartedAt is null ? null : (long)(now - snapshot.StartedAt.Value).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The store is gone (shutdown or a simulated crash): the next start reconciles this run as Interrupted (spec §13).
        }
        finally
        {
            run.Completion.TrySetResult(snapshot);
            ExecutionChanged?.Invoke(this, snapshot);
        }
    }

    private void Publish(Run run) => ExecutionChanged?.Invoke(this, run.Snapshot());

    private void AppendEvent(Run run, string? stepId, EventSeverity severity, string eventType, string message, long? durationMs) =>
        _history.AppendEvent(new ExecutionEvent(0, run.Id, run.Workflow.Id, stepId, _time.GetUtcNow(), severity, eventType, OutputRedactor.Redact(message) ?? string.Empty, durationMs));

    /// <summary>Mutable state of one run, guarded by its own lock.</summary>
    private sealed class Run
    {
        private readonly Lock _lock = new();
        private readonly State _state;

        public Run(Guid id, WorkflowDefinition workflow, Profile profile, StartOptions options, DateTimeOffset queuedAt)
        {
            Id = id;
            Workflow = workflow;
            Profile = profile;
            Options = options;
            _state = new State(queuedAt);
            var ordinal = 0;
            foreach (var step in workflow.Steps)
            {
                _state.Steps[step.Id] = new StepState(step, ordinal++);
            }
        }

        public Guid Id { get; }

        public WorkflowDefinition Workflow { get; }

        public Profile Profile { get; }

        public StartOptions Options { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<ExecutionSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Update(Func<State, bool> mutate)
        {
            lock (_lock)
            {
                return mutate(_state);
            }
        }

        public StepStatus StepStatusOf(string stepId)
        {
            lock (_lock)
            {
                return _state.Steps[stepId].Status;
            }
        }

        public void SetStep(string stepId, StepStatus status, DateTimeOffset? finishedAt, string? errorCode, string? message, string? output, DateTimeOffset? startedAt = null)
        {
            lock (_lock)
            {
                var step = _state.Steps[stepId];
                step.Status = status;
                step.StartedAt ??= startedAt;
                step.FinishedAt = finishedAt ?? step.FinishedAt;
                step.ErrorCode = errorCode;
                step.Message = message;
                step.Output = output;
            }
        }

        public ExecutionSnapshot Snapshot()
        {
            lock (_lock)
            {
                var steps = _state.Steps.Values
                    .OrderBy(s => s.Ordinal)
                    .Select(s => new StepSnapshot(s.Definition.Id, s.Definition.Name, s.Definition.ActionId, s.Ordinal, s.Status, s.StartedAt, s.FinishedAt, s.ErrorCode, s.Message, s.Output))
                    .ToList();
                return new ExecutionSnapshot(
                    Id,
                    Profile.Id,
                    Workflow.Id,
                    Workflow.Name,
                    _state.Status,
                    _state.QueuedAt,
                    _state.StartedAt,
                    _state.FinishedAt,
                    _state.Error,
                    steps,
                    Workflow.ContentHash,
                    Options.TriggerSource,
                    Options.ScheduledFor,
                    Options.ResumedFrom);
            }
        }

        public sealed class State
        {
            public State(DateTimeOffset queuedAt)
            {
                QueuedAt = queuedAt;
            }

            public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;

            public DateTimeOffset QueuedAt { get; }

            public DateTimeOffset? StartedAt { get; set; }

            public DateTimeOffset? FinishedAt { get; set; }

            public string? Error { get; set; }

            public Dictionary<string, StepState> Steps { get; } = new(StringComparer.Ordinal);
        }

        public sealed class StepState
        {
            public StepState(StepDefinition definition, int ordinal)
            {
                Definition = definition;
                Ordinal = ordinal;
            }

            public StepDefinition Definition { get; }

            public int Ordinal { get; }

            public StepStatus Status { get; set; } = StepStatus.Pending;

            public DateTimeOffset? StartedAt { get; set; }

            public DateTimeOffset? FinishedAt { get; set; }

            public string? ErrorCode { get; set; }

            public string? Message { get; set; }

            public string? Output { get; set; }
        }
    }
}
