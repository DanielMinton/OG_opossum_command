using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.History;

/// <summary>Durable store for runs, steps, events, triggers, and settings (spec §13). The engine is the only writer of run state.</summary>
public interface IHistoryStore : IDisposable
{
    /// <summary>Records or refreshes a profile row.</summary>
    void UpsertProfile(Profile profile);

    /// <summary>Records or refreshes the workflow definition the engine will run (versioned by content hash).</summary>
    void UpsertWorkflow(WorkflowDefinition workflow, DateTimeOffset seenAt);

    /// <summary>
    /// Writes the run and all of its steps in one transaction; safe to call on every transition. The run error and
    /// the step messages and output are redacted on the way in (spec §13).
    /// </summary>
    void SaveExecution(ExecutionSnapshot execution);

    /// <summary>Appends one ordered diagnostic event.</summary>
    void AppendEvent(ExecutionEvent executionEvent);

    /// <summary>
    /// Startup reconciliation: every run still Queued or Running becomes Interrupted with recovery <c>pending</c>;
    /// a run that was Cancelling becomes Cancelled (the operator stopped it). Returns the Interrupted IDs.
    /// </summary>
    IReadOnlyList<Guid> MarkInterrupted(DateTimeOffset now);

    /// <summary>Interrupted runs whose recovery is still <c>pending</c> (this boot's and any an earlier boot failed to review), oldest first.</summary>
    IReadOnlyList<Guid> GetPendingRecovery();

    /// <summary>Records the recovery outcome of an interrupted run (status, <see cref="RecoveryStates"/> value, explanation) and logs an event.</summary>
    void SetRecovery(Guid executionId, ExecutionStatus status, string recovery, string explanation, DateTimeOffset now);

    /// <summary>Most recent runs, newest first, optionally scoped to one profile.</summary>
    IReadOnlyList<ExecutionSnapshot> GetRecentExecutions(ProfileId? profile, int limit);

    /// <summary>Runs in one status, newest first (used for Needs attention).</summary>
    IReadOnlyList<ExecutionSnapshot> GetExecutionsByStatus(ExecutionStatus status, int limit);

    /// <summary>One run with its steps, or <c>null</c>.</summary>
    ExecutionSnapshot? GetExecution(Guid executionId);

    /// <summary>Events of one run in order.</summary>
    IReadOnlyList<ExecutionEvent> GetEvents(Guid executionId);

    /// <summary>All trigger rows.</summary>
    IReadOnlyList<TriggerRecord> GetTriggers();

    /// <summary>One trigger row, or <c>null</c>.</summary>
    TriggerRecord? GetTrigger(string triggerId);

    /// <summary>Inserts or replaces a trigger row.</summary>
    void UpsertTrigger(TriggerRecord trigger);

    /// <summary>
    /// Updates only <c>last_fired_at</c> (when not null), <c>last_outcome</c>, and <c>updated_at</c>, so a dispatcher
    /// can never roll back <c>last_fired_for</c> or the schedule state from a stale read.
    /// </summary>
    void UpdateTriggerOutcome(string triggerId, DateTimeOffset? lastFiredAt, string outcome, DateTimeOffset updatedAt);

    /// <summary>Removes a trigger row (its workflow is gone or no longer scheduled).</summary>
    void DeleteTrigger(string triggerId);

    string? GetSetting(string key);

    void SetSetting(string key, string value);
}

/// <summary>Keys of the <c>settings</c> table that Core and the UI share.</summary>
public static class SettingKeys
{
    /// <summary><c>work</c> / <c>personal</c>: the profile the dashboard shows.</summary>
    public const string ActiveProfile = "ui.activeProfile";

    /// <summary><c>true</c> suppresses every notification below Error (quiet mode, spec §14).</summary>
    public const string QuietMode = "ui.quietMode";

    /// <summary>The command-palette hotkey, e.g. <c>Ctrl+Alt+Space</c>.</summary>
    public const string Hotkey = "ui.hotkey";

    /// <summary><c>true</c> while the operator has paused all schedules.</summary>
    public const string SchedulerPaused = "scheduler.paused";

    /// <summary>The last unexpected scheduler-loop failure (UTC time, type, redacted message); the loop keeps running.</summary>
    public const string SchedulerLastError = "scheduler.lastError";
}
