using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Engine;

/// <summary>Top-level state of a run (spec §06 state machine).</summary>
public enum ExecutionStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelling,
    Cancelled,

    /// <summary>The app stopped while the run was active. Set during startup reconciliation; terminal once reviewed.</summary>
    Interrupted,

    /// <summary>Recovery could not tell whether a side effect happened; the operator decides (Resume / Dismiss).</summary>
    NeedsAttention,
}

/// <summary>State of one step inside a run.</summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,

    /// <summary>Not started because a step it depends on did not complete, or the run stopped after a failure.</summary>
    Skipped,
    Cancelled,
    Interrupted,
}

/// <summary>Immutable view of one step, safe to hand to any thread.</summary>
public sealed record StepSnapshot(
    string Id,
    string Name,
    string ActionId,
    int Ordinal,
    StepStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? Message,
    string? Output)
{
    /// <summary>Whether the step can no longer change.</summary>
    public bool IsTerminal => Status is not (StepStatus.Pending or StepStatus.Running);
}

/// <summary>Immutable view of one run, safe to hand to any thread.</summary>
/// <param name="WorkflowHash">Content hash of the definition this run used (so recovery can tell whether the file changed).</param>
/// <param name="TriggerSource">One of <see cref="TriggerSources"/>.</param>
/// <param name="ScheduledFor">The schedule occurrence that started this run, when <paramref name="TriggerSource"/> is <c>schedule</c>.</param>
/// <param name="ResumedFrom">The interrupted run this one continues, when <paramref name="TriggerSource"/> is <c>recovery</c>.</param>
/// <param name="Recovery">One of <see cref="RecoveryStates"/>, or <c>null</c> when recovery never touched this run.</param>
public sealed record ExecutionSnapshot(
    Guid Id,
    ProfileId Profile,
    string WorkflowId,
    string WorkflowName,
    ExecutionStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    IReadOnlyList<StepSnapshot> Steps,
    string WorkflowHash = "",
    string TriggerSource = TriggerSources.Manual,
    DateTimeOffset? ScheduledFor = null,
    Guid? ResumedFrom = null,
    string? Recovery = null)
{
    /// <summary>Queued, Running, or Cancelling.</summary>
    public bool IsActive => Status is ExecutionStatus.Queued or ExecutionStatus.Running or ExecutionStatus.Cancelling;

    /// <summary>Steps that completed successfully.</summary>
    public int CompletedStepCount => Steps.Count(s => s.Status == StepStatus.Completed);

    /// <summary>Steps that reached any terminal state.</summary>
    public int SettledStepCount => Steps.Count(s => s.IsTerminal);
}

/// <summary>Values of <see cref="ExecutionSnapshot.TriggerSource"/>.</summary>
public static class TriggerSources
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
    public const string Recovery = "recovery";
}

/// <summary>Values of <see cref="ExecutionSnapshot.Recovery"/> (spec §13 recovery algorithm, ADR-035).</summary>
public static class RecoveryStates
{
    /// <summary>Marked Interrupted at startup; not yet classified.</summary>
    public const string Pending = "pending";

    /// <summary>Every unfinished step was safe to repeat; a new run was started automatically.</summary>
    public const string AutoResumed = "auto-resumed";

    /// <summary>A side effect may already have happened; waiting for the operator.</summary>
    public const string NeedsAttention = "needs-attention";

    /// <summary>The operator chose Resume; a new run was started.</summary>
    public const string Resumed = "resumed";

    /// <summary>The operator chose Dismiss; nothing was re-run.</summary>
    public const string Dismissed = "dismissed";
}

/// <summary>How a run is being started.</summary>
/// <param name="TriggerSource">One of <see cref="TriggerSources"/>.</param>
/// <param name="ScheduledFor">The schedule occurrence being honoured.</param>
/// <param name="ResumedFrom">The interrupted run this one continues.</param>
/// <param name="AlreadyCompletedSteps">Step IDs to mark Completed at once instead of running them again (their side effects happened in <paramref name="ResumedFrom"/>).</param>
public sealed record StartOptions(
    string TriggerSource = TriggerSources.Manual,
    DateTimeOffset? ScheduledFor = null,
    Guid? ResumedFrom = null,
    IReadOnlySet<string>? AlreadyCompletedSteps = null)
{
    /// <summary>A plain manual start.</summary>
    public static StartOptions Manual { get; } = new();
}

/// <summary>One ordered, redacted diagnostic event (spec §13 logging contract).</summary>
public sealed record ExecutionEvent(
    long Id,
    Guid ExecutionId,
    string WorkflowId,
    string? StepId,
    DateTimeOffset AtUtc,
    EventSeverity Severity,
    string EventType,
    string Message,
    long? DurationMs);

/// <summary>Severity of an <see cref="ExecutionEvent"/>.</summary>
public enum EventSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>What <see cref="WorkflowEngine.Start"/> returns: the run that is now active for the workflow.</summary>
/// <param name="Execution">The new run, or the existing one under the focus-existing policy.</param>
/// <param name="WasAlreadyRunning">True when no new run was created because one was still active.</param>
public sealed record StartResult(ExecutionSnapshot Execution, bool WasAlreadyRunning);

/// <summary>Engine limits.</summary>
/// <param name="MaxConcurrentWorkflows">Runs that may be Running at once; the rest wait in Queued.</param>
public sealed record EngineOptions(int MaxConcurrentWorkflows = 2);

/// <summary>Thrown by the engine when a workflow uses an action its profile does not allow (ADR-012).</summary>
public sealed class ActionNotAllowedException : InvalidOperationException
{
    public ActionNotAllowedException()
    {
    }

    public ActionNotAllowedException(string message)
        : base(message)
    {
    }

    public ActionNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ActionNotAllowedException(ProfileId profile, string workflowId, string stepId, string actionId)
        : base($"Workflow '{workflowId}' step '{stepId}' uses action '{actionId}', which the {profile.DisplayName()} profile does not allow.")
    {
        Profile = profile;
        WorkflowId = workflowId;
        StepId = stepId;
        ActionId = actionId;
    }

    public ProfileId Profile { get; }

    public string? WorkflowId { get; }

    public string? StepId { get; }

    public string? ActionId { get; }
}
