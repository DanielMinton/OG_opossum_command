# Changelog

All notable changes to OG Opossum Command Central. Versions follow [SemVer](https://semver.org/).

## 0.1.0 — 2026-10-23

First usable release, for one person on one Windows PC, as a standard user.

### Added

- **Tray app** (`OG_opossum_command.exe`, WPF on .NET 10). Closing the window hides it to the
  tray; Quit is in the tray menu. A second launch brings the running window forward.
- **Dashboard** with six fixed tiles per profile, live step progress, Cancel, a Needs-attention
  panel (Resume / Dismiss), recent runs, a Today panel, and separate History and Settings views.
- **Command palette** on Ctrl+Alt+Space (configurable): type a workflow name or alias, Enter runs it.
- **Work and Personal profiles** with per-profile action allowlists (`profile.json`); `m365.*`
  actions are Work-only and fail closed when the policy file is broken.
- **Workflows as JSON files** (schema v1 and v2, see `docs/WORKFLOW-SCHEMA.md`), validated strictly
  at load time with readable errors.
- **Actions:** open an http/https page, open a folder, launch a program from a full `.exe` path with
  an argument array, run a trusted script from the profile's own `scripts` folder, read the
  calendar file, show today's schedule.
- **Engine:** execution IDs, dependencies, bounded parallelism, engine-wide resource locks,
  timeouts, and cancellation that kills launched process trees.
- **Scheduler:** explicit time zones, DST-safe occurrences, missed-run policies, duplicate
  suppression, pause, startup reconciliation.
- **Crash recovery:** interrupted runs are reconciled at startup; only provably safe steps resume on
  their own, anything else asks first.
- **History** in SQLite (`data.db`) with redacted, size-bounded step output; schema migrations keep
  older databases working.
- **Calendar** from a local JSON file written by the user's own Power Automate flow
  (`docs/POWER_AUTOMATE_FLOW.md`); no sign-in, no app registration, no network.
- **Notifications** through the tray, with a quiet mode that only lets errors through.
- **Smoke modes** `--smoke-test` and `--smoke-ui` that never touch real data or open anything.

### Hardening (phase 6)

Four adversarial reviews (security, stress correctness, accessibility, simplicity), each finding
checked by a second reviewer before anything was fixed (ADR-041 to ADR-049).

- **Security:** `cmd` scripts refuse shell metacharacters in arguments and run from the scripts
  folder (ADR-042). `windows.process.launch` refuses shells and script hosts (ADR-043). Duplicate
  JSON keys and NUL characters are load errors (ADR-044). Redaction is central and covers more
  secret shapes (ADR-045). A test proves the app has no network or listener code (ADR-048).
- **Reliability:** every run transition is saved before it is shown. A grandchild process holding
  the output pipe can no longer hang a step. Cancelled runs are never auto-resumed, and failed
  non-idempotent steps also block auto-resume. Recovery picks up runs an earlier start never
  reviewed. The scheduler survives store errors. Quit stops the scheduler before cancelling runs.
  Opening a meeting link with no browser registered no longer crashes the app (ADR-046).
- **Accessibility:** screen readers announce status changes and read real names for list rows,
  steps, and buttons. Font sizes are resources that scale with the system message font (whether that
  follows the Windows Text size slider is checked by hand, docs/ACCEPTANCE.md M5.4). The dashboard scrolls and keeps Cancel
  under the tiles. Access keys are unique, list rows show a thick focus ring, and the tray icon
  answers Enter (ADR-047).
- **Simplicity:** dead members and a test-only presenter removed (ADR-049).
- **Acceptance:** `docs/ACCEPTANCE.md` maps every row of spec §18 to PASS, MANUAL, or DEFERRED.

### Changed

- Scripts for the `cmd` interpreter may no longer have spaces in their file names.
- A run that was being cancelled when the app stopped now ends **Cancelled** instead of
  **Interrupted** (ADR-046 revises ADR-035).

### Not included

- Microsoft Graph sign-in, HTTP/WebSocket adapters, AI features, UI Automation adapters, a visual
  workflow editor, and organisational deployment. See `docs/ACCEPTANCE.md` for what is deferred.
