using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.History;

/// <summary>Durable store for runs, steps, events, and settings (spec §13). The engine is the only writer of run state.</summary>
public interface IHistoryStore : IDisposable
{
    /// <summary>Records or refreshes a profile row.</summary>
    void UpsertProfile(Profile profile);

    /// <summary>Records or refreshes the workflow definition the engine will run (versioned by content hash).</summary>
    void UpsertWorkflow(WorkflowDefinition workflow, DateTimeOffset seenAt);

    /// <summary>Writes the run and all of its steps in one transaction; safe to call on every transition.</summary>
    void SaveExecution(ExecutionSnapshot execution);

    /// <summary>Appends one ordered diagnostic event.</summary>
    void AppendEvent(ExecutionEvent executionEvent);

    /// <summary>Startup reconciliation: every run still Queued, Running, or Cancelling becomes Interrupted. Returns how many.</summary>
    int MarkInterrupted(DateTimeOffset now);

    /// <summary>Most recent runs, newest first, optionally scoped to one profile.</summary>
    IReadOnlyList<ExecutionSnapshot> GetRecentExecutions(ProfileId? profile, int limit);

    /// <summary>One run with its steps, or <c>null</c>.</summary>
    ExecutionSnapshot? GetExecution(Guid executionId);

    /// <summary>Events of one run in order.</summary>
    IReadOnlyList<ExecutionEvent> GetEvents(Guid executionId);

    string? GetSetting(string key);

    void SetSetting(string key, string value);
}
