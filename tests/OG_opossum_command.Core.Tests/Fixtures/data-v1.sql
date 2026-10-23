-- A database exactly as Phase 3 (schema version 1) left it, with one row in every table.
-- Checked in by hand, NOT generated from Migrations.All[0], so that editing a shipped migration cannot
-- silently keep this test green. Loaded by MigrationTests before SqliteHistoryStore.Open migrates it.

CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
INSERT INTO schema_migrations (version, name, applied_at) VALUES (1, 'initial', '2026-10-22T20:00:00.0000000+00:00');

CREATE TABLE profiles (
    id              TEXT PRIMARY KEY,
    display_name    TEXT NOT NULL,
    allowed_actions TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);
INSERT INTO profiles VALUES ('work', 'Work', 'windows.folder.open,windows.process.launch,windows.url.open', '2026-10-22T20:00:00.0000000+00:00');
INSERT INTO profiles VALUES ('personal', 'Personal', 'windows.folder.open,windows.process.launch,windows.url.open', '2026-10-22T20:00:00.0000000+00:00');

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
INSERT INTO workflows VALUES ('work', 'start-workday', 'Start Workday', 1, 'abc123', 'C:\data\profiles\Work\workflows\start-workday.json', '2026-10-22T20:00:00.0000000+00:00');

CREATE TABLE triggers (
    id              TEXT PRIMARY KEY,
    profile_id      TEXT NOT NULL,
    workflow_id     TEXT NOT NULL,
    type            TEXT NOT NULL,
    enabled         INTEGER NOT NULL DEFAULT 1,
    config_json     TEXT NOT NULL DEFAULT '{}',
    updated_at      TEXT NOT NULL
);
INSERT INTO triggers (id, profile_id, workflow_id, type, enabled, config_json, updated_at) VALUES ('work/start-workday/manual', 'work', 'start-workday', 'manual', 1, '{}', '2026-10-22T20:00:00.0000000+00:00');

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
INSERT INTO executions VALUES ('11111111-1111-1111-1111-111111111111', 'work', 'start-workday', 'Start Workday', 'Completed', '2026-10-22T20:01:00.0000000+00:00', '2026-10-22T20:01:00.0000000+00:00', '2026-10-22T20:01:02.0000000+00:00', NULL);
INSERT INTO executions VALUES ('22222222-2222-2222-2222-222222222222', 'work', 'start-workday', 'Start Workday', 'Running', '2026-10-22T20:05:00.0000000+00:00', '2026-10-22T20:05:00.0000000+00:00', NULL, NULL);

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
INSERT INTO step_executions VALUES ('11111111-1111-1111-1111-111111111111', 'outlook', 0, 'Open Outlook (web)', 'windows.url.open', 1, 'Completed', '2026-10-22T20:01:00.0000000+00:00', '2026-10-22T20:01:01.0000000+00:00', NULL, 'Opened outlook.office.com', NULL);
INSERT INTO step_executions VALUES ('22222222-2222-2222-2222-222222222222', 'outlook', 0, 'Open Outlook (web)', 'windows.url.open', 1, 'Completed', '2026-10-22T20:05:00.0000000+00:00', '2026-10-22T20:05:01.0000000+00:00', NULL, 'Opened outlook.office.com', NULL);
INSERT INTO step_executions VALUES ('22222222-2222-2222-2222-222222222222', 'teams', 1, 'Open Teams (web)', 'windows.url.open', 1, 'Running', '2026-10-22T20:05:01.0000000+00:00', NULL, NULL, NULL, NULL);

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
INSERT INTO execution_events (execution_id, workflow_id, step_id, at_utc, severity, event_type, message, duration_ms) VALUES ('11111111-1111-1111-1111-111111111111', 'start-workday', NULL, '2026-10-22T20:01:02.0000000+00:00', 'Info', 'execution.completed', 'Completed: 1 of 1 steps completed', 2000);

CREATE TABLE settings (
    key             TEXT PRIMARY KEY,
    value           TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);
INSERT INTO settings VALUES ('ui.activeProfile', 'personal', '2026-10-22T20:00:00.0000000+00:00');
