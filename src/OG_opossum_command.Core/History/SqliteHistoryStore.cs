using System.Globalization;
using Microsoft.Data.Sqlite;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.History;

/// <summary>
/// SQLite-backed <see cref="IHistoryStore"/> at <c>&lt;data root&gt;\data.db</c>. One connection, guarded by a lock,
/// WAL journal, foreign keys on, migrations applied on open. Pooling is off so the file is released on dispose.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    private const string ExecutionColumns = "id, profile_id, workflow_id, workflow_name, status, queued_at, started_at, finished_at, error, workflow_hash, trigger_source, scheduled_for, resumed_from, recovery";
    private const string TriggerColumns = "id, profile_id, workflow_id, type, enabled, config_json, config_hash, next_due_at, last_fired_for, last_fired_at, last_outcome, updated_at";

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private bool _disposed;

    private SqliteHistoryStore(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    /// <summary>Where the database file lives.</summary>
    public string Path { get; }

    /// <summary>Schema version after migration.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>Opens (creating if needed) and migrates the database at <paramref name="databasePath"/>.</summary>
    public static SqliteHistoryStore Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!System.IO.Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("Database path must be absolute.", nameof(databasePath));
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "PRAGMA busy_timeout = 5000;");

            var store = new SqliteHistoryStore(connection, databasePath);
            store.SchemaVersion = Migrate(connection);
            return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void UpsertProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO profiles (id, display_name, allowed_actions, updated_at)
                VALUES ($id, $name, $allowed, $now)
                ON CONFLICT (id) DO UPDATE SET display_name = excluded.display_name, allowed_actions = excluded.allowed_actions, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", profile.Id.ToJsonValue());
            command.Parameters.AddWithValue("$name", profile.DisplayName);
            command.Parameters.AddWithValue("$allowed", string.Join(",", profile.AllowedActionIds.Order(StringComparer.Ordinal)));
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public void UpsertWorkflow(WorkflowDefinition workflow, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO workflows (profile_id, id, name, schema_version, content_hash, source_path, last_seen_at)
                VALUES ($profile, $id, $name, $schema, $hash, $source, $seen)
                ON CONFLICT (profile_id, id) DO UPDATE SET
                    name = excluded.name, schema_version = excluded.schema_version, content_hash = excluded.content_hash,
                    source_path = excluded.source_path, last_seen_at = excluded.last_seen_at;
                """;
            command.Parameters.AddWithValue("$profile", workflow.ProfileId.ToJsonValue());
            command.Parameters.AddWithValue("$id", workflow.Id);
            command.Parameters.AddWithValue("$name", workflow.Name);
            command.Parameters.AddWithValue("$schema", workflow.SchemaVersion);
            command.Parameters.AddWithValue("$hash", workflow.ContentHash);
            command.Parameters.AddWithValue("$source", workflow.SourcePath);
            command.Parameters.AddWithValue("$seen", Format(seenAt));
            command.ExecuteNonQuery();
        }
    }

    public void SaveExecution(ExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO executions (id, profile_id, workflow_id, workflow_name, status, queued_at, started_at, finished_at, error, workflow_hash, trigger_source, scheduled_for, resumed_from, recovery)
                    VALUES ($id, $profile, $workflow, $name, $status, $queued, $started, $finished, $error, $hash, $source, $scheduled, $resumed, $recovery)
                    ON CONFLICT (id) DO UPDATE SET
                        status = excluded.status, started_at = excluded.started_at, finished_at = excluded.finished_at, error = excluded.error,
                        recovery = excluded.recovery;
                    """;
                command.Parameters.AddWithValue("$id", execution.Id.ToString("D"));
                command.Parameters.AddWithValue("$profile", execution.Profile.ToJsonValue());
                command.Parameters.AddWithValue("$workflow", execution.WorkflowId);
                command.Parameters.AddWithValue("$name", execution.WorkflowName);
                command.Parameters.AddWithValue("$status", execution.Status.ToString());
                command.Parameters.AddWithValue("$queued", Format(execution.QueuedAt));
                command.Parameters.AddWithValue("$started", (object?)Format(execution.StartedAt) ?? DBNull.Value);
                command.Parameters.AddWithValue("$finished", (object?)Format(execution.FinishedAt) ?? DBNull.Value);
                command.Parameters.AddWithValue("$error", (object?)execution.Error ?? DBNull.Value);
                command.Parameters.AddWithValue("$hash", execution.WorkflowHash);
                command.Parameters.AddWithValue("$source", execution.TriggerSource);
                command.Parameters.AddWithValue("$scheduled", (object?)Format(execution.ScheduledFor) ?? DBNull.Value);
                command.Parameters.AddWithValue("$resumed", (object?)execution.ResumedFrom?.ToString("D") ?? DBNull.Value);
                command.Parameters.AddWithValue("$recovery", (object?)execution.Recovery ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            foreach (var step in execution.Steps)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO step_executions (execution_id, step_id, ordinal, step_name, action_id, attempt, status, started_at, finished_at, error_code, message, output)
                    VALUES ($execution, $step, $ordinal, $name, $action, 1, $status, $started, $finished, $code, $message, $output)
                    ON CONFLICT (execution_id, step_id) DO UPDATE SET
                        status = excluded.status, started_at = excluded.started_at, finished_at = excluded.finished_at,
                        error_code = excluded.error_code, message = excluded.message, output = excluded.output;
                    """;
                command.Parameters.AddWithValue("$execution", execution.Id.ToString("D"));
                command.Parameters.AddWithValue("$step", step.Id);
                command.Parameters.AddWithValue("$ordinal", step.Ordinal);
                command.Parameters.AddWithValue("$name", step.Name);
                command.Parameters.AddWithValue("$action", step.ActionId);
                command.Parameters.AddWithValue("$status", step.Status.ToString());
                command.Parameters.AddWithValue("$started", (object?)Format(step.StartedAt) ?? DBNull.Value);
                command.Parameters.AddWithValue("$finished", (object?)Format(step.FinishedAt) ?? DBNull.Value);
                command.Parameters.AddWithValue("$code", (object?)step.ErrorCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$message", (object?)step.Message ?? DBNull.Value);
                command.Parameters.AddWithValue("$output", (object?)step.Output ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void AppendEvent(ExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO execution_events (execution_id, workflow_id, step_id, at_utc, severity, event_type, message, duration_ms)
                VALUES ($execution, $workflow, $step, $at, $severity, $type, $message, $duration);
                """;
            command.Parameters.AddWithValue("$execution", executionEvent.ExecutionId.ToString("D"));
            command.Parameters.AddWithValue("$workflow", executionEvent.WorkflowId);
            command.Parameters.AddWithValue("$step", (object?)executionEvent.StepId ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", Format(executionEvent.AtUtc));
            command.Parameters.AddWithValue("$severity", executionEvent.Severity.ToString());
            command.Parameters.AddWithValue("$type", executionEvent.EventType);
            command.Parameters.AddWithValue("$message", executionEvent.Message);
            command.Parameters.AddWithValue("$duration", (object?)executionEvent.DurationMs ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Guid> MarkInterrupted(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var stamp = Format(now);

            var ids = new List<(string Id, string WorkflowId)>();
            using (var select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id, workflow_id FROM executions WHERE status IN ('Queued', 'Running', 'Cancelling') ORDER BY queued_at, rowid;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (id, workflowId) in ids)
            {
                using (var steps = _connection.CreateCommand())
                {
                    steps.Transaction = transaction;
                    steps.CommandText = "UPDATE step_executions SET status = 'Interrupted', finished_at = $now WHERE execution_id = $id AND status IN ('Pending', 'Running');";
                    steps.Parameters.AddWithValue("$now", stamp);
                    steps.Parameters.AddWithValue("$id", id);
                    steps.ExecuteNonQuery();
                }

                using (var execution = _connection.CreateCommand())
                {
                    execution.Transaction = transaction;
                    execution.CommandText = "UPDATE executions SET status = 'Interrupted', finished_at = $now, recovery = $recovery, error = COALESCE(error, 'The app stopped while this run was active.') WHERE id = $id;";
                    execution.Parameters.AddWithValue("$now", stamp);
                    execution.Parameters.AddWithValue("$id", id);
                    execution.Parameters.AddWithValue("$recovery", RecoveryStates.Pending);
                    execution.ExecuteNonQuery();
                }

                using (var evt = _connection.CreateCommand())
                {
                    evt.Transaction = transaction;
                    evt.CommandText = """
                        INSERT INTO execution_events (execution_id, workflow_id, step_id, at_utc, severity, event_type, message, duration_ms)
                        VALUES ($id, $workflow, NULL, $now, 'Warning', 'execution.interrupted', 'Marked Interrupted at startup: the app stopped while this run was active.', NULL);
                        """;
                    evt.Parameters.AddWithValue("$id", id);
                    evt.Parameters.AddWithValue("$workflow", workflowId);
                    evt.Parameters.AddWithValue("$now", stamp);
                    evt.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return ids.Select(i => Guid.Parse(i.Id)).ToList();
        }
    }

    public void SetRecovery(Guid executionId, ExecutionStatus status, string recovery, string explanation, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recovery);
        ArgumentNullException.ThrowIfNull(explanation);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            string workflowId;
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE executions SET status = $status, recovery = $recovery, error = $error WHERE id = $id RETURNING workflow_id;";
                update.Parameters.AddWithValue("$status", status.ToString());
                update.Parameters.AddWithValue("$recovery", recovery);
                update.Parameters.AddWithValue("$error", explanation);
                update.Parameters.AddWithValue("$id", executionId.ToString("D"));
                workflowId = update.ExecuteScalar() as string ?? throw new KeyNotFoundException($"Unknown execution {executionId}.");
            }

            using (var evt = _connection.CreateCommand())
            {
                evt.Transaction = transaction;
                evt.CommandText = """
                    INSERT INTO execution_events (execution_id, workflow_id, step_id, at_utc, severity, event_type, message, duration_ms)
                    VALUES ($id, $workflow, NULL, $now, $severity, $type, $message, NULL);
                    """;
                evt.Parameters.AddWithValue("$id", executionId.ToString("D"));
                evt.Parameters.AddWithValue("$workflow", workflowId);
                evt.Parameters.AddWithValue("$now", Format(now));
                evt.Parameters.AddWithValue("$severity", status == ExecutionStatus.NeedsAttention ? EventSeverity.Error.ToString() : EventSeverity.Warning.ToString());
                evt.Parameters.AddWithValue("$type", $"recovery.{recovery}");
                evt.Parameters.AddWithValue("$message", OutputRedactor.Redact(explanation) ?? string.Empty);
                evt.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<ExecutionSnapshot> GetRecentExecutions(ProfileId? profile, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            var executions = new List<ExecutionSnapshot>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = profile is null
                    ? $"SELECT {ExecutionColumns} FROM executions ORDER BY queued_at DESC, rowid DESC LIMIT $limit;"
                    : $"SELECT {ExecutionColumns} FROM executions WHERE profile_id = $profile ORDER BY queued_at DESC, rowid DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit);
                if (profile is not null)
                {
                    command.Parameters.AddWithValue("$profile", profile.Value.ToJsonValue());
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    executions.Add(ReadExecution(reader, []));
                }
            }

            return executions.Select(e => e with { Steps = ReadSteps(e.Id) }).ToList();
        }
    }

    public IReadOnlyList<ExecutionSnapshot> GetExecutionsByStatus(ExecutionStatus status, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            var executions = new List<ExecutionSnapshot>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = $"SELECT {ExecutionColumns} FROM executions WHERE status = $status ORDER BY queued_at DESC, rowid DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$status", status.ToString());
                command.Parameters.AddWithValue("$limit", limit);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    executions.Add(ReadExecution(reader, []));
                }
            }

            return executions.Select(e => e with { Steps = ReadSteps(e.Id) }).ToList();
        }
    }

    public ExecutionSnapshot? GetExecution(Guid executionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT {ExecutionColumns} FROM executions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", executionId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var execution = ReadExecution(reader, []);
            reader.Close();
            return execution with { Steps = ReadSteps(executionId) };
        }
    }

    public IReadOnlyList<ExecutionEvent> GetEvents(Guid executionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, execution_id, workflow_id, step_id, at_utc, severity, event_type, message, duration_ms FROM execution_events WHERE execution_id = $id ORDER BY id;";
            command.Parameters.AddWithValue("$id", executionId.ToString("D"));
            using var reader = command.ExecuteReader();
            var events = new List<ExecutionEvent>();
            while (reader.Read())
            {
                events.Add(new ExecutionEvent(
                    reader.GetInt64(0),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    Parse(reader.GetString(4)),
                    Enum.Parse<EventSeverity>(reader.GetString(5)),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8)));
            }

            return events;
        }
    }

    public IReadOnlyList<TriggerRecord> GetTriggers()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT {TriggerColumns} FROM triggers ORDER BY id;";
            using var reader = command.ExecuteReader();
            var triggers = new List<TriggerRecord>();
            while (reader.Read())
            {
                triggers.Add(ReadTrigger(reader));
            }

            return triggers;
        }
    }

    public TriggerRecord? GetTrigger(string triggerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT {TriggerColumns} FROM triggers WHERE id = $id;";
            command.Parameters.AddWithValue("$id", triggerId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTrigger(reader) : null;
        }
    }

    public void UpsertTrigger(TriggerRecord trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO triggers (id, profile_id, workflow_id, type, enabled, config_json, config_hash, next_due_at, last_fired_for, last_fired_at, last_outcome, updated_at)
                VALUES ($id, $profile, $workflow, $type, $enabled, $config, $hash, $next, $firedFor, $firedAt, $outcome, $updated)
                ON CONFLICT (id) DO UPDATE SET
                    profile_id = excluded.profile_id, workflow_id = excluded.workflow_id, type = excluded.type, enabled = excluded.enabled,
                    config_json = excluded.config_json, config_hash = excluded.config_hash, next_due_at = excluded.next_due_at,
                    last_fired_for = excluded.last_fired_for, last_fired_at = excluded.last_fired_at, last_outcome = excluded.last_outcome,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", trigger.Id);
            command.Parameters.AddWithValue("$profile", trigger.Profile.ToJsonValue());
            command.Parameters.AddWithValue("$workflow", trigger.WorkflowId);
            command.Parameters.AddWithValue("$type", trigger.Type);
            command.Parameters.AddWithValue("$enabled", trigger.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$config", trigger.ConfigJson);
            command.Parameters.AddWithValue("$hash", trigger.ConfigHash);
            command.Parameters.AddWithValue("$next", (object?)Format(trigger.NextDueAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("$firedFor", (object?)Format(trigger.LastFiredFor) ?? DBNull.Value);
            command.Parameters.AddWithValue("$firedAt", (object?)Format(trigger.LastFiredAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("$outcome", (object?)trigger.LastOutcome ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", Format(trigger.UpdatedAt));
            command.ExecuteNonQuery();
        }
    }

    public void DeleteTrigger(string triggerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM triggers WHERE id = $id;";
            command.Parameters.AddWithValue("$id", triggerId);
            command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $now)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Applied migration versions, for diagnostics and tests.</summary>
    public IReadOnlyList<int> GetAppliedMigrations()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            using var reader = command.ExecuteReader();
            var versions = new List<int>();
            while (reader.Read())
            {
                versions.Add(reader.GetInt32(0));
            }

            return versions;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static int Migrate(SqliteConnection connection)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);");

        var applied = new HashSet<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        var unknown = applied.Where(v => v > Migrations.Latest).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"The database was created by a newer version of the app (schema {unknown.Max()}, this build understands {Migrations.Latest}). Refusing to open it.");
        }

        foreach (var migration in Migrations.All)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $now);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Migrations.Latest;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static ExecutionSnapshot ReadExecution(SqliteDataReader reader, IReadOnlyList<StepSnapshot> steps) =>
        new(
            Guid.Parse(reader.GetString(0)),
            ProfileIdExtensions.TryParseJsonValue(reader.GetString(1), out var profile) ? profile : ProfileId.Personal,
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<ExecutionStatus>(reader.GetString(4)),
            Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            steps,
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    private static TriggerRecord ReadTrigger(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            ProfileIdExtensions.TryParseJsonValue(reader.GetString(1), out var profile) ? profile : ProfileId.Personal,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) != 0,
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            Parse(reader.GetString(11)));

    private List<StepSnapshot> ReadSteps(Guid executionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT step_id, step_name, action_id, ordinal, status, started_at, finished_at, error_code, message, output FROM step_executions WHERE execution_id = $id ORDER BY ordinal;";
        command.Parameters.AddWithValue("$id", executionId.ToString("D"));
        using var reader = command.ExecuteReader();
        var steps = new List<StepSnapshot>();
        while (reader.Read())
        {
            steps.Add(new StepSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                Enum.Parse<StepStatus>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return steps;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Format(DateTimeOffset? value) => value is null ? null : Format(value.Value);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
