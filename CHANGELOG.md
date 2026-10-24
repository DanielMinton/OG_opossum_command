# Changelog

What changed in OG Opossum Command Central, release by release. Versions follow
[SemVer](https://semver.org/). The ADR numbers point at `docs/DECISIONS.md` if you want the why.

## Unreleased

### Added

- **Opossum Maid** (ADR-058 to ADR-069): a tidy-up for the work download inbox and Downloads. It
  reads what each file actually is and what it's about (PDF, Word, Excel, PowerPoint, text, HTML and
  photo metadata, all on this PC), proposes a category folder, a short plain name, tags, and keep or
  junk, with reasons. Folders stay few (about seven a side, two levels at most); tags carry the
  detail. Nothing moves until I approve the plan, every run can be undone exactly, and nothing is
  ever deleted: junk waits 30 days in `_Trash Review`. Work and personal files never cross on their
  own, credential-looking files are never opened, and filed work folders are left alone.
- **`OpossumMaid` command line** (`tools/OpossumMaid`): `scan`, `plan`, `report`, `approve`, `apply`,
  `undo`, `learn`, `bundle`, `merge`, `purge`.
- **It learns** from corrections (`learn`, or edits to the plan file): "files mentioning X are Y".
- **Reasoning hand-off for personal files**: `bundle` writes a scrubbed file Claude Code reads with
  `/maid-reason`, `merge` takes its decisions back. The app itself still makes no AI or network call.
  Spec: [docs/MAID-HANDOFF.md](docs/MAID-HANDOFF.md).
- **Settings › Tidy up**: makes a plan and opens the report. Applying stays on the command line.
- **PdfPig** (Apache-2.0) for PDF text, the fourth runtime dependency (ADR-059).

## 0.3.0 — 2026-10-23

### Added

- **Motion** (ADR-057): the dashboard's cards and tiles slide in one after another, tiles lift and
  glow on hover and spring back when clicked, a ring spins on a tile while it runs and a check or
  cross shows when it finishes, numbers count up, and the status dot breathes. Windows "Animation
  effects" off, or a contrast theme, turns all of it off.
- **Sounds** (ADR-057): a soft tick on clicks, two rising notes when a workflow starts, a chime when
  it completes and two low notes when it fails. Made by the app itself; nothing is downloaded.
  Off in Settings › Look and sound; in quiet mode only failures are heard; silent while hidden,
  because a tray app has no business chirping at you from the background.
- **Today at a glance**: runs today, success rate, runs needing attention and the next scheduled
  workflow above the tiles. The dashboard title greets you and shows the date.
- **Ctrl+1 to Ctrl+6** run the six tiles from any page; each tile shows its shortcut.
- **Recent runs** show a coloured status chip per run.

## 0.2.1 — 2026-10-23

### Added

- **App icon** (ADR-056): my opossum artwork, the same at every size, on the program, taskbar,
  window and tray, plus PNGs for the Hub in `assets/branding/`.

### Fixed

- **High contrast** (ADR-055): "Running now" and the times in Today were in the accent colour,
  which stays the same under a contrast theme and could be unreadable. They now use the normal
  text colour.
- **Large text** (ADR-055): the navigation rail widens with text size instead of cutting off
  "Command palette" and the other labels; icons and the tile badges grow with the text.
- **Narrator** (ADR-055): the warning icon in Needs attention is read as "Warning".
- **Icon tool** (ADR-056): `tools/IconBuilder` builds again under the repo's analyzer rules.

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

First usable release. One person, one Windows PC, a standard user account.

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
- **Calendar** from a local JSON file written by your own Power Automate flow
  (`docs/POWER_AUTOMATE_FLOW.md`); no sign-in, no app registration, no network.
- **Notifications** through the tray, with a quiet mode that only lets errors through.
- **Smoke modes** `--smoke-test` and `--smoke-ui` that never touch real data or open anything.

### Hardening (phase 6)

Four adversarial reviews (security, stress correctness, accessibility, simplicity). Every finding
had to survive a second reviewer trying to refute it before it got fixed (ADR-041 to ADR-049).

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
  follows the Windows Text size slider is checked by hand, docs/ACCEPTANCE.md M5.4). The dashboard
  scrolls and keeps Cancel under the tiles. Access keys are unique, list rows show a thick focus ring, and the tray icon
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
