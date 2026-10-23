namespace OG_opossum_command.Core.History;

/// <summary>One schema step. Versions are applied in order inside a transaction and recorded in <c>schema_migrations</c>.</summary>
/// <param name="Version">1-based, strictly increasing.</param>
/// <param name="Name">Short label stored next to the version.</param>
/// <param name="Sql">Statements to run; may contain several separated by semicolons.</param>
public sealed record Migration(int Version, string Name, string Sql);

/// <summary>The database schema, as an ordered list of migrations. Never edit a shipped migration; add a new one.</summary>
public static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new Migration(1, "initial", """
            CREATE TABLE profiles (
                id              TEXT PRIMARY KEY,
                display_name    TEXT NOT NULL,
                allowed_actions TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE workflows (
                profile_id      TEXT NOT NULL,
                id              TEXT NOT NULL,
                name            TEXT NOT NULL,
                schema_version  INTEGER NOT NULL,
                content_hash    TEXT NOT NULL,
                source_path     TEXT NOT NULL,
                last_seen_at    TEXT NOT NULL,
                PRIMARY KEY (profile_id, id)
            );

            CREATE TABLE triggers (
                id              TEXT PRIMARY KEY,
                profile_id      TEXT NOT NULL,
                workflow_id     TEXT NOT NULL,
                type            TEXT NOT NULL,
                enabled         INTEGER NOT NULL DEFAULT 1,
                config_json     TEXT NOT NULL DEFAULT '{}',
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE executions (
                id              TEXT PRIMARY KEY,
                profile_id      TEXT NOT NULL,
                workflow_id     TEXT NOT NULL,
                workflow_name   TEXT NOT NULL,
                status          TEXT NOT NULL,
                queued_at       TEXT NOT NULL,
                started_at      TEXT,
                finished_at     TEXT,
                error           TEXT
            );
            CREATE INDEX ix_executions_queued_at ON executions (queued_at DESC);
            CREATE INDEX ix_executions_profile ON executions (profile_id, queued_at DESC);

            CREATE TABLE step_executions (
                execution_id    TEXT NOT NULL REFERENCES executions (id) ON DELETE CASCADE,
                step_id         TEXT NOT NULL,
                ordinal         INTEGER NOT NULL,
                step_name       TEXT NOT NULL,
                action_id       TEXT NOT NULL,
                attempt         INTEGER NOT NULL DEFAULT 1,
                status          TEXT NOT NULL,
                started_at      TEXT,
                finished_at     TEXT,
                error_code      TEXT,
                message         TEXT,
                output          TEXT,
                PRIMARY KEY (execution_id, step_id)
            );

            CREATE TABLE execution_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                execution_id    TEXT NOT NULL,
                workflow_id     TEXT NOT NULL,
                step_id         TEXT,
                at_utc          TEXT NOT NULL,
                severity        TEXT NOT NULL,
                event_type      TEXT NOT NULL,
                message         TEXT NOT NULL,
                duration_ms     INTEGER
            );
            CREATE INDEX ix_execution_events_execution ON execution_events (execution_id, id);

            CREATE TABLE settings (
                key             TEXT PRIMARY KEY,
                value           TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            """),

        // Phase 4 (ADR-034, ADR-035): scheduler state on triggers; provenance and recovery bookkeeping on executions.
        // Additive only (ALTER TABLE ... ADD COLUMN with defaults), so a v1 database and its rows survive untouched.
        new Migration(2, "scheduler-and-recovery", """
            ALTER TABLE executions ADD COLUMN workflow_hash  TEXT NOT NULL DEFAULT '';
            ALTER TABLE executions ADD COLUMN trigger_source TEXT NOT NULL DEFAULT 'manual';
            ALTER TABLE executions ADD COLUMN scheduled_for  TEXT;
            ALTER TABLE executions ADD COLUMN resumed_from   TEXT;
            ALTER TABLE executions ADD COLUMN recovery       TEXT;
            CREATE INDEX ix_executions_status ON executions (status);

            ALTER TABLE triggers ADD COLUMN config_hash    TEXT NOT NULL DEFAULT '';
            ALTER TABLE triggers ADD COLUMN next_due_at    TEXT;
            ALTER TABLE triggers ADD COLUMN last_fired_for TEXT;
            ALTER TABLE triggers ADD COLUMN last_fired_at  TEXT;
            ALTER TABLE triggers ADD COLUMN last_outcome   TEXT;
            """),
    ];

    /// <summary>The version a freshly migrated database ends at.</summary>
    public static int Latest => All[^1].Version;
}
