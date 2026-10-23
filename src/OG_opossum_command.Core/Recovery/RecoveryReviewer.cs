using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Recovery;

/// <summary>What the review decided for one interrupted run.</summary>
/// <param name="ExecutionId">The interrupted run.</param>
/// <param name="Profile">Its profile.</param>
/// <param name="WorkflowId">Its workflow.</param>
/// <param name="WorkflowName">Display name.</param>
/// <param name="Outcome"><see cref="RecoveryStates.AutoResumed"/> or <see cref="RecoveryStates.NeedsAttention"/>.</param>
/// <param name="Explanation">Why, in plain words; shown to the operator for Needs attention.</param>
/// <param name="ResumedAs">The new run when auto-resumed.</param>
public sealed record RecoveryDecision(Guid ExecutionId, ProfileId Profile, string WorkflowId, string WorkflowName, string Outcome, string Explanation, Guid? ResumedAs);

/// <summary>
/// The spec §13 recovery algorithm (ADR-035). After startup marked runs Interrupted, each is classified:
/// steps that never started are always safe; a step that was running is safe only if its action is idempotent
/// and the workflow file is unchanged. Safe runs resume automatically in a new run that skips the steps already
/// completed; anything else becomes Needs attention with a clear explanation and waits for Resume or Dismiss.
/// </summary>
public static class RecoveryReviewer
{
    /// <summary>
    /// Classifies <paramref name="interrupted"/> (normally <see cref="IHistoryStore.GetPendingRecovery"/>) and acts on
    /// each. Never throws for a single bad run; the run becomes Needs attention instead.
    /// </summary>
    public static IReadOnlyList<RecoveryDecision> Review(
        IReadOnlyList<Guid> interrupted,
        IHistoryStore history,
        WorkflowEngine engine,
        ActionRegistry registry,
        IReadOnlyList<ProfileWorkflows> workflows,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(interrupted);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(workflows);

        var decisions = new List<RecoveryDecision>();
        foreach (var id in interrupted)
        {
            var execution = history.GetExecution(id);
            if (execution is null)
            {
                continue;
            }

            try
            {
                decisions.Add(ReviewOne(execution, history, engine, registry, workflows, now));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var explanation = $"The recovery check for this run failed ({ex.GetType().Name}: {ex.Message}), so it was not resumed. Resume or Dismiss.";
                if (TrySetNeedsAttention(history, execution.Id, explanation, now))
                {
                    decisions.Add(new RecoveryDecision(execution.Id, execution.Profile, execution.WorkflowId, execution.WorkflowName, RecoveryStates.NeedsAttention, explanation, null));
                }
            }
        }

        return decisions;
    }

    private static RecoveryDecision ReviewOne(ExecutionSnapshot execution, IHistoryStore history, WorkflowEngine engine, ActionRegistry registry, IReadOnlyList<ProfileWorkflows> workflows, DateTimeOffset now)
    {
        var loaded = workflows.FirstOrDefault(w => w.Profile.Id == execution.Profile);
        var definition = loaded?.Workflows.FirstOrDefault(w => w.Id == execution.WorkflowId);
        var problem = Classify(execution, definition, registry);
        if (problem is not null)
        {
            history.SetRecovery(execution.Id, ExecutionStatus.NeedsAttention, RecoveryStates.NeedsAttention, problem, now);
            return new RecoveryDecision(execution.Id, execution.Profile, execution.WorkflowId, execution.WorkflowName, RecoveryStates.NeedsAttention, problem, null);
        }

        var completed = CompletedSteps(execution);
        var resumed = engine.Start(definition!, loaded!.Profile, new StartOptions(TriggerSources.Recovery, null, execution.Id, completed));
        var explanation = $"Resumed automatically as run {resumed.Execution.Id}: every unfinished step was safe to repeat ({completed.Count} already-completed step(s) were not repeated).";
        history.SetRecovery(execution.Id, ExecutionStatus.Interrupted, RecoveryStates.AutoResumed, explanation, now);
        return new RecoveryDecision(execution.Id, execution.Profile, execution.WorkflowId, execution.WorkflowName, RecoveryStates.AutoResumed, explanation, resumed.Execution.Id);
    }

    /// <summary>Last resort for a run whose review threw; if even this fails the row stays pending and the next start reviews it again.</summary>
    private static bool TrySetNeedsAttention(IHistoryStore history, Guid executionId, string explanation, DateTimeOffset now)
    {
        try
        {
            history.SetRecovery(executionId, ExecutionStatus.NeedsAttention, RecoveryStates.NeedsAttention, explanation, now);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Operator chose Resume: a new run that skips the steps the interrupted run completed. The current file is used.</summary>
    /// <exception cref="InvalidOperationException">The run is not waiting for attention, its workflow does not load, or another run of the workflow is active (the run stays waiting).</exception>
    public static StartResult Resume(Guid executionId, IHistoryStore history, WorkflowEngine engine, IReadOnlyList<ProfileWorkflows> workflows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(workflows);

        var execution = history.GetExecution(executionId) ?? throw new KeyNotFoundException($"Unknown run {executionId}.");
        if (execution.Status != ExecutionStatus.NeedsAttention)
        {
            throw new InvalidOperationException($"Run {executionId} is {execution.Status}, not waiting for attention.");
        }

        var loaded = workflows.FirstOrDefault(w => w.Profile.Id == execution.Profile);
        var definition = loaded?.Workflows.FirstOrDefault(w => w.Id == execution.WorkflowId)
            ?? throw new InvalidOperationException($"Workflow '{execution.WorkflowId}' is not loaded in the {execution.Profile.DisplayName()} profile, so it cannot be resumed. Fix the file, then try again, or dismiss this run.");

        var completed = CompletedSteps(execution);
        var resumed = engine.Start(definition, loaded!.Profile, new StartOptions(TriggerSources.Recovery, null, execution.Id, completed));
        if (resumed.WasAlreadyRunning)
        {
            // Focus-existing returned another run of the workflow; the interrupted remainder has not run. Keep it waiting.
            throw new InvalidOperationException($"{execution.WorkflowName} is running now; resume this run after it finishes.");
        }

        history.SetRecovery(execution.Id, ExecutionStatus.Interrupted, RecoveryStates.Resumed, $"Resumed by the operator as run {resumed.Execution.Id}; {completed.Count} already-completed step(s) were not repeated.", now);
        return resumed;
    }

    /// <summary>Operator chose Dismiss: nothing is re-run; the run stays in history as Interrupted.</summary>
    public static void Dismiss(Guid executionId, IHistoryStore history, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        var execution = history.GetExecution(executionId) ?? throw new KeyNotFoundException($"Unknown run {executionId}.");
        if (execution.Status != ExecutionStatus.NeedsAttention)
        {
            throw new InvalidOperationException($"Run {executionId} is {execution.Status}, not waiting for attention.");
        }

        history.SetRecovery(execution.Id, ExecutionStatus.Interrupted, RecoveryStates.Dismissed, "Dismissed by the operator; nothing was re-run.", now);
    }

    /// <summary>The reason a run cannot resume on its own, or <c>null</c> when it can.</summary>
    public static string? Classify(ExecutionSnapshot execution, WorkflowDefinition? definition, ActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(registry);

        if (definition is null)
        {
            return $"The workflow file for '{execution.WorkflowId}' no longer loads in the {execution.Profile.DisplayName()} profile, so the interrupted run cannot be checked. Fix the file and press Resume, or Dismiss.";
        }

        if (!string.IsNullOrEmpty(execution.WorkflowHash) && execution.WorkflowHash != definition.ContentHash)
        {
            return $"{execution.WorkflowName} was changed on disk since this run started, so the remaining steps may not be what they were. Resume runs the current file (skipping the {CompletedSteps(execution).Count} step(s) that completed), or Dismiss.";
        }

        // A step that started and did not complete (interrupted, or failed or cancelled part-way, e.g. a timeout with
        // onFailure: continue) may have left its side effect half done; repeating it on its own is only safe if idempotent.
        var unsafeSteps = new List<string>();
        foreach (var step in execution.Steps.Where(s => s.StartedAt is not null && s.Status is StepStatus.Interrupted or StepStatus.Failed or StepStatus.Cancelled))
        {
            var idempotent = registry.TryGet(step.ActionId, out var action) && action.Idempotency == IdempotencyClass.Idempotent;
            if (!idempotent)
            {
                unsafeSteps.Add($"'{step.Name}' ({step.ActionId})");
            }
        }

        if (unsafeSteps.Count > 0)
        {
            return $"{string.Join(", ", unsafeSteps)} was running (or had started and not completed) when the app stopped, and repeating it could start a second copy of a program or script. Resume runs it again (steps that completed are not repeated); Dismiss leaves things as they are.";
        }

        return null;
    }

    private static HashSet<string> CompletedSteps(ExecutionSnapshot execution) =>
        execution.Steps.Where(s => s.Status == StepStatus.Completed).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
}
