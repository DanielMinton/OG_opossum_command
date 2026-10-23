using Microsoft.Data.Sqlite;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.History;

public sealed class SqliteHistoryStoreTests : IDisposable
{
    private readonly TempDataRoot _root = new();

    private static ExecutionSnapshot Snapshot(Guid id, ExecutionStatus status, ProfileId profile = ProfileId.Work, DateTimeOffset? queuedAt = null, params StepStatus[] steps)
    {
        var queued = queuedAt ?? new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        var stepList = steps.Select((s, i) => new StepSnapshot($"s{i}", $"Step {i}", "windows.url.open", i, s, queued, s is StepStatus.Pending or StepStatus.Running ? null : queued.AddSeconds(1), null, s == StepStatus.Failed ? "went wrong" : null, null)).ToList();
        return new ExecutionSnapshot(id, profile, "wf", "Workflow", status, queued, queued, status is ExecutionStatus.Queued or ExecutionStatus.Running or ExecutionStatus.Cancelling ? null : queued.AddSeconds(2), null, stepList);
    }

    [Fact]
    public void CountFinishedSince_counts_only_finished_runs_of_the_profile_since_the_cutoff()
    {
        using var store = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        var cutoff = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.FromHours(-7));
        var today = cutoff.AddHours(9);
        store.SaveExecution(Snapshot(Guid.NewGuid(), ExecutionStatus.Completed, queuedAt: today));
        store.SaveExecution(Snapshot(Guid.NewGuid(), ExecutionStatus.Completed, queuedAt: today.AddMinutes(5)));
        store.SaveExecution(Snapshot(Guid.NewGuid(), ExecutionStatus.Failed, queuedAt: today.AddMinutes(10)));
        store.SaveExecution(Snapshot(Guid.NewGuid(), ExecutionStatus.Running, queuedAt: today.AddMinutes(15)));
        store.SaveExecution(Snapshot(Guid.NewGuid(), ExecutionStatus.Completed, queuedAt: cutoff.AddMinutes(-1)));
        store.SaveExecution(Snapshot(Guid.NewGuid(), ExecutionStatus.Completed, ProfileId.Personal, today));

        Assert.Equal(new RunCounts(3, 2), store.CountFinishedSince(ProfileId.Work, cutoff));
        Assert.Equal(new RunCounts(0, 0), store.CountFinishedSince(ProfileId.Work, today.AddDays(1)));
    }

    [Fact]
    public void Open_creates_the_file_runs_migrations_and_uses_WAL()
    {
        using var store = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        store.SetSetting("probe", "1");

        Assert.True(File.Exists(_root.Paths.DatabasePath));
        Assert.EndsWith("data.db", store.Path, StringComparison.Ordinal);
        Assert.Equal(Migrations.Latest, store.SchemaVersion);
        Assert.Equal(Migrations.All.Select(m => m.Version), store.GetAppliedMigrations());
        Assert.True(File.Exists(_root.Paths.DatabasePath + "-wal"), "WAL journal file missing: journal_mode is not WAL");
    }

    [Fact]
    public void Every_spec_13_table_exists()
    {
        using (SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _root.Paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        foreach (var expected in new[] { "profiles", "workflows", "triggers", "executions", "step_executions", "execution_events", "settings", "schema_migrations" })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void Executions_steps_events_and_settings_survive_reopening()
    {
        var id = Guid.NewGuid();
        using (var store = SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
            store.SaveExecution(Snapshot(id, ExecutionStatus.Running, steps: [StepStatus.Completed, StepStatus.Running]));
            store.SaveExecution(Snapshot(id, ExecutionStatus.Failed, steps: [StepStatus.Completed, StepStatus.Failed]));
            store.AppendEvent(new ExecutionEvent(0, id, "wf", "s1", DateTimeOffset.UtcNow, EventSeverity.Error, "step.failed", "went wrong", 12));
            store.SetSetting("ui.activeProfile", "personal");
        }

        using var reopened = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        var execution = reopened.GetExecution(id);
        Assert.NotNull(execution);
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal(2, execution.Steps.Count);
        Assert.Equal(StepStatus.Failed, execution.Steps[1].Status);
        Assert.Equal("went wrong", execution.Steps[1].Message);
        var evt = Assert.Single(reopened.GetEvents(id));
        Assert.Equal("step.failed", evt.EventType);
        Assert.Equal(12, evt.DurationMs);
        Assert.Equal("personal", reopened.GetSetting("ui.activeProfile"));
        Assert.Null(reopened.GetSetting("missing"));
    }

    [Fact]
    public void MarkInterrupted_on_reopen_flags_runs_that_were_still_active()
    {
        var active = Guid.NewGuid();
        var done = Guid.NewGuid();
        using (var store = SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
            store.SaveExecution(Snapshot(active, ExecutionStatus.Running, steps: [StepStatus.Completed, StepStatus.Running, StepStatus.Pending]));
            store.SaveExecution(Snapshot(done, ExecutionStatus.Completed, steps: [StepStatus.Completed]));
        }

        using var reopened = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        var ids = reopened.MarkInterrupted(DateTimeOffset.UtcNow);

        Assert.Equal([active], ids);
        var interrupted = reopened.GetExecution(active)!;
        Assert.Equal(ExecutionStatus.Interrupted, interrupted.Status);
        Assert.Equal(RecoveryStates.Pending, interrupted.Recovery);
        Assert.NotNull(interrupted.FinishedAt);
        Assert.Contains("stopped while this run was active", interrupted.Error, StringComparison.Ordinal);
        Assert.Equal([StepStatus.Completed, StepStatus.Interrupted, StepStatus.Interrupted], interrupted.Steps.Select(s => s.Status));
        Assert.Contains(reopened.GetEvents(active), e => e.EventType == "execution.interrupted");
        Assert.Equal(ExecutionStatus.Completed, reopened.GetExecution(done)!.Status);
        Assert.Empty(reopened.MarkInterrupted(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GetRecentExecutions_filters_by_profile_and_orders_newest_first()
    {
        using var store = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        var t0 = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var oldWork = Guid.NewGuid();
        var newWork = Guid.NewGuid();
        var personal = Guid.NewGuid();
        store.SaveExecution(Snapshot(oldWork, ExecutionStatus.Completed, ProfileId.Work, t0, StepStatus.Completed));
        store.SaveExecution(Snapshot(personal, ExecutionStatus.Completed, ProfileId.Personal, t0.AddMinutes(1), StepStatus.Completed));
        store.SaveExecution(Snapshot(newWork, ExecutionStatus.Cancelled, ProfileId.Work, t0.AddMinutes(2), StepStatus.Cancelled));

        Assert.Equal([newWork, oldWork], store.GetRecentExecutions(ProfileId.Work, 10).Select(e => e.Id));
        Assert.Equal([personal], store.GetRecentExecutions(ProfileId.Personal, 10).Select(e => e.Id));
        Assert.Equal([newWork, personal, oldWork], store.GetRecentExecutions(null, 10).Select(e => e.Id));
        Assert.Single(store.GetRecentExecutions(null, 1));
        Assert.All(store.GetRecentExecutions(null, 10), e => Assert.Single(e.Steps));
    }

    [Fact]
    public void Refuses_a_database_written_by_a_newer_schema()
    {
        using (SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
        }

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _root.Paths.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES (999, 'future', '2030-01-01T00:00:00Z');";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => SqliteHistoryStore.Open(_root.Paths.DatabasePath));
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_relative_database_paths()
    {
        Assert.Throws<ArgumentException>(() => SqliteHistoryStore.Open(@"relative\data.db"));
    }

    public void Dispose() => _root.Dispose();
}

public sealed class OutputRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.DEF-123_xyz", "abc.DEF-123_xyz", "Authorization")]
    [InlineData("password=hunter2 user=dan", "hunter2", "user=dan")]
    [InlineData("\"client_secret\": \"s3cr3t-value\"", "s3cr3t-value", "client_secret")]
    [InlineData("https://x.example/cb?code=abcdef&state=1", "abcdef", "&state=1")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c end", "eyJhbGciOiJIUzI1NiJ9", "token [redacted] end")]
    public void Scrubs_common_secret_shapes(string input, string secret, string keptFragment)
    {
        var redacted = OutputRedactor.Redact(input)!;

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains(keptFragment, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Bounds_stored_output_and_says_how_much_was_dropped()
    {
        var huge = new string('x', OutputRedactor.MaxStoredChars + 500);

        var redacted = OutputRedactor.Redact(huge)!;

        Assert.StartsWith(new string('x', OutputRedactor.MaxStoredChars), redacted, StringComparison.Ordinal);
        Assert.Contains("[truncated 500 characters]", redacted, StringComparison.Ordinal);
        Assert.True(redacted.Length < huge.Length);
    }

    [Fact]
    public void Leaves_harmless_text_alone_and_passes_null_through()
    {
        Assert.Equal("Opened outlook.office.com", OutputRedactor.Redact("Opened outlook.office.com"));
        Assert.Null(OutputRedactor.Redact(null));
        Assert.Equal(string.Empty, OutputRedactor.Redact(string.Empty));
    }
}
