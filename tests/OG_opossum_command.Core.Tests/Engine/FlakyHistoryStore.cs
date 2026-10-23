using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Engine;

/// <summary>A real store that throws once from <see cref="SaveExecution"/> when told to, to simulate a transient I/O failure.</summary>
internal sealed class FlakyHistoryStore : IHistoryStore
{
    private readonly SqliteHistoryStore _inner;
    private readonly Lock _gate = new();
    private Func<ExecutionSnapshot, bool>? _failSave;

    public FlakyHistoryStore(SqliteHistoryStore inner)
    {
        _inner = inner;
    }

    public SqliteHistoryStore Inner => _inner;

    /// <summary>The next save whose snapshot matches <paramref name="when"/> throws <see cref="IOException"/>; later saves succeed.</summary>
    public void FailNextSave(Func<ExecutionSnapshot, bool> when)
    {
        lock (_gate)
        {
            _failSave = when;
        }
    }

    public void SaveExecution(ExecutionSnapshot execution)
    {
        lock (_gate)
        {
            if (_failSave is not null && _failSave(execution))
            {
                _failSave = null;
                throw new IOException("Simulated disk failure (test).");
            }
        }

        _inner.SaveExecution(execution);
    }

    public IReadOnlyList<TriggerRecord> GetTriggers() => _inner.GetTriggers();

    public void UpsertProfile(Profile profile) => _inner.UpsertProfile(profile);

    public void UpsertWorkflow(WorkflowDefinition workflow, DateTimeOffset seenAt) => _inner.UpsertWorkflow(workflow, seenAt);

    public void AppendEvent(ExecutionEvent executionEvent) => _inner.AppendEvent(executionEvent);

    public IReadOnlyList<Guid> MarkInterrupted(DateTimeOffset now) => _inner.MarkInterrupted(now);

    public IReadOnlyList<Guid> GetPendingRecovery() => _inner.GetPendingRecovery();

    public void SetRecovery(Guid executionId, ExecutionStatus status, string recovery, string explanation, DateTimeOffset now) => _inner.SetRecovery(executionId, status, recovery, explanation, now);

    public IReadOnlyList<ExecutionSnapshot> GetRecentExecutions(ProfileId? profile, int limit) => _inner.GetRecentExecutions(profile, limit);

    public RunCounts CountFinishedSince(ProfileId profile, DateTimeOffset since) => _inner.CountFinishedSince(profile, since);

    public IReadOnlyList<ExecutionSnapshot> GetExecutionsByStatus(ExecutionStatus status, ProfileId? profile, int limit) => _inner.GetExecutionsByStatus(status, profile, limit);

    public ExecutionSnapshot? GetExecution(Guid executionId) => _inner.GetExecution(executionId);

    public IReadOnlyList<ExecutionEvent> GetEvents(Guid executionId) => _inner.GetEvents(executionId);

    public TriggerRecord? GetTrigger(string triggerId) => _inner.GetTrigger(triggerId);

    public void UpsertTrigger(TriggerRecord trigger) => _inner.UpsertTrigger(trigger);

    public void UpdateTriggerOutcome(string triggerId, DateTimeOffset? lastFiredAt, string outcome, DateTimeOffset updatedAt) => _inner.UpdateTriggerOutcome(triggerId, lastFiredAt, outcome, updatedAt);

    public void DeleteTrigger(string triggerId) => _inner.DeleteTrigger(triggerId);

    public string? GetSetting(string key) => _inner.GetSetting(key);

    public void SetSetting(string key, string value) => _inner.SetSetting(key, value);

    public void Dispose() => _inner.Dispose();
}
