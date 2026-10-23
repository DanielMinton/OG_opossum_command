# Changelog

All notable changes to OG Opossum Command Central. Versions follow [SemVer](https://semver.org/).

## 0.2.1 — 2026-10-23

### Added

- **App icon** (ADR-056): your opossum artwork, the same at every size, on the program, taskbar,
  window and tray, plus PNGs for the Hub in `assets/branding/`.

## 0.2.0 — 2026-10-23

Phases 8-9: exports and alerts through your work OneDrive. Still no network calls, sign-in, or
listeners. Every integration is a file that OneDrive syncs and your own Power Automate flow reads or
writes (`docs/POWER_AUTOMATE_FLOW.md`).

### Added

- **status.json** (ADR-050): the Work profile writes a small, scrubbed summary to
  `%OneDriveCommercial%\OG_opossum_command\status.json`. It covers runs that need attention,
  failures in the last 24 h, completions today, what is running, and what is still scheduled
  today. It is written at start, every 15 minutes, within a few seconds of a run ending (at most
  once every 30 s), and on Quit, always atomically; a write that OneDrive or antivirus blocks is
  retried 30 s later. It is the input for the Morning Briefing flow.
- **Alert outbox** (ADR-052, ADR-053): when a Work run fails, needs attention, or overruns its step
  timeout by 5 minutes, one JSON file is dropped into `OG_opossum_command\outbox\`, for a flow that
  posts it to Teams. At most one per workflow and event per 30 minutes. If the folder is missing or
  locked at that moment, the alert is retried every 30 s for up to 30 minutes. The outbox is pruned to 200
  files and 7 days, touching only the app's own files. Quiet mode doesn't hold alerts back; the
  flow handles quiet hours. This replaces the planned HTTP `webhook.post`, which the no-network
  rule forbids.
- **Carryovers from yesterday** (ADR-051): the Today panel lists up to 10 items from
  `tomorrow.json`, which your 4:30 pm flow writes. Items show only on the file's `forDate`. The
  file is parsed strictly as untrusted input and nothing in it is ever opened.
- **profile.json `exports`** (`folder`, `status`, `alerts`, `carryovers`): Work is on by default,
  Personal is off. Folders inside the other context's OneDrive are refused in both directions, and
  a broken `profile.json` turns exports off (fail closed).
- **Settings → Exports**: read-only state per profile (status.json last written, folder missing,
  off by policy; last alert; carryovers file). State changes are logged once to `logs\exports.log`.
- `--smoke-ui` prints `today panel carryovers bound (N)` and the exports row count.

### Changed

- The calendar's default path now comes from the shared exports folder (same location as before).
  The cross-OneDrive guard is shared code (`OneDriveBoundary`).
- Version 0.2.0 (`Directory.Build.props`, `app.manifest`).
- **New look** (ADR-055): the built-in Windows 11 theme (Mica, rounded controls, your accent colour,
  light, dark and high contrast follow Windows), a navigation bar on the left instead of the footer
  buttons, and the dashboard laid out as cards. Same access keys, tile positions and Cancel button.

### Fixed

- Scrolling the mouse wheel over the profile picker no longer switches between Work and Personal,
  and scrolling over Recent runs scrolls the dashboard instead of getting stuck in the list (ADR-055).

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
