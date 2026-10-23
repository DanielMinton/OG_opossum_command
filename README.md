# OG Opossum Command Central

A small Windows tray app that runs your repeatable routines (open these apps, these URLs, this
checklist) with one click, keeps a durable history of every run, and keeps work and personal
contexts strictly apart. It is built for one person on one PC, as a **standard user**: no admin
rights, no services, no network listeners, no telemetry, no AI calls.

Code name: `OG_opossum_command` (solution, namespaces, `OG_opossum_command.exe`, data folder).
Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (it still says "OPOSSUM CONTROL"; that name
is superseded). Decisions: [docs/DECISIONS.md](docs/DECISIONS.md).

## Status

Phase 5 (calendar without app registration). Workflows are JSON files per profile (schema v1 and
v2, see [docs/WORKFLOW-SCHEMA.md](docs/WORKFLOW-SCHEMA.md)); six actions (open a web page, open a
folder, launch a program with an argument array, run a trusted script from the profile's own
`scripts` folder, read the calendar file, show today's schedule); an engine with execution IDs, dependencies, bounded parallelism, resource locks, timeouts,
and cancellation that kills launched process trees; a scheduler (explicit time zone, DST-safe,
missed-run policies, duplicate suppression, startup reconciliation); crash recovery that resumes only
provably safe steps and asks about the rest (Resume / Dismiss); SQLite history; Work and Personal
profiles with action allowlists; a tray app with a six-tile dashboard, live progress, Cancel, a
Needs-attention panel, recent runs, History, Settings (hotkey, quiet mode, pause schedules), tray
notifications, and a command palette on **Ctrl+Alt+Space** (type a name or alias, Enter runs).
Closing the window hides it to the tray; Quit is in the tray menu. Starting the exe a second time
just brings the existing window forward.

Scripts: `windows.script.run` only runs files that live in `profiles\<Work|Personal>\scripts`, with
an explicit interpreter (`powershell` or `cmd`) and an argument array. If PowerShell's execution
policy on this PC blocks a `.ps1`, the step says so plainly; the app never overrides that policy.

Calendar: the dashboard's **Today** panel lists the rest of today's work events. They come from a
JSON file that **your own** Power Automate flow writes to your work OneDrive every 15 minutes (step
by step in [docs/POWER_AUTOMATE_FLOW.md](docs/POWER_AUTOMATE_FLOW.md)). The app only reads that local
file: no sign-in, no app registration, no network. Without the flow the panel just says the calendar
isn't configured or is unavailable. The Personal profile can't read the work calendar.

## Layout

| Path | What |
|---|---|
| `src/OG_opossum_command.Core` | Engine, workflows, actions, scheduling, history, profiles, paths. Plain `net10.0`, no UI. |
| `src/OG_opossum_command.Desktop` | WPF tray app (`net10.0-windows`). Builds `OG_opossum_command.exe`. |
| `tests/OG_opossum_command.Core.Tests` | xUnit tests for Core. |
| `examples/workflows/{work,personal}` | Example workflow files per profile. None is scheduled. |
| `examples/scripts/personal` | Example trusted script (`hello.cmd`) seeded into the Personal profile's `scripts` folder. |
| `docs/` | Architecture, decisions, feasibility report, workflow schema, Power Automate calendar flow (for you to build). |
| `tests/fixtures` | Shared test fixtures (`calendar.sample.json`, also the sample in the flow guide). |
| `spikes/feasibility` | Throwaway Phase 1 spike, not in the solution. |

## Requirements

- Windows 10/11, .NET SDK 10.0 (a per-user install under `%LOCALAPPDATA%\Microsoft\dotnet` is enough).
- Git Bash for the scripts. Package restore uses the repo's `nuget.config` (nuget.org only).

## Build, test, run

From the repo root:

```bash
dotnet build OG_opossum_command.slnx -warnaserror
dotnet test  OG_opossum_command.slnx
```

Run the app during development:

```bash
dotnet run --project src/OG_opossum_command.Desktop
```

Self-checks (no window is ever shown, nothing real is opened, no balloon pops, no hotkey is grabbed,
the real data folder is never touched; both exit on their own within 30 s and work even while the
app itself is running):

```bash
exe=src/OG_opossum_command.Desktop/bin/Debug/net10.0-windows/OG_opossum_command.exe
"$exe" --smoke-test   # paths, DB migration, profiles, example workflows, one engine run with fakes → SMOKE OK
"$exe" --smoke-ui     # real App start-up, off-screen window, dashboard/settings/palette bound, tray icon created/disposed → SMOKE-UI OK
```

The exe is a windowed program, so its output only shows when the caller captures it, for
example `out=$(... --smoke-test)`. The exit code is the contract. Without `OGOC_DATA_DIR` the smoke
modes use a throwaway folder under `%TEMP%`.

## Where data lives

Every per-user path is derived from one data root by the `AppPaths` service in Core:

| Situation | Data root |
|---|---|
| Normal use | `%LOCALAPPDATA%\OG_opossum_command` |
| `OGOC_DATA_DIR` set (tests, build script, experiments) | that folder; it must be an absolute path |

Under the root:

| Path | What |
|---|---|
| `data.db` (+ `-wal`, `-shm`) | SQLite (schema 2): profiles, workflow versions, schedule triggers and their state, executions (with trigger source and recovery outcome), step states, redacted events, settings, schema migrations. A schema-1 database from Phase 3 is migrated in place. |
| `profiles\Work\workflows\*.json` | The Work profile's workflows. Edit these; on first run the examples are copied here. |
| `profiles\Personal\workflows\*.json` | Same for Personal. A file's `profileId` must match its folder. |
| `profiles\<Work|Personal>\scripts\` | The only place `windows.script.run` reads scripts from. |
| `profiles\<Work|Personal>\profile.json` | Optional: `allowedActions` (action IDs that profile may run) and `calendarFile` (where its calendar JSON is). |
| `logs\crash.log` | Written only if the app fails to start or hits an unhandled error. |

The app never reads or writes relative to the current directory, never touches the registry, and
never writes outside the data root. The one thing it reads outside the data root is the Work
profile's calendar file in your work OneDrive (read-only, never created or downloaded). Example workflows live in `examples/workflows/` in the repo and
next to the exe; they are clearly marked `EXAMPLE - edit me`.

## The build script

`./build.sh` at the repo root drives the whole build in phases (scaffold, core loop, reliability,
hardening, publish) with deterministic gates between them: `dotnet build -warnaserror`, then
`dotnet test`, then the `--smoke-test` / `--smoke-ui` checks against the newest Debug exe with
`OGOC_DATA_DIR` pointed at a scratch folder. Run it from Git Bash:

```bash
./build.sh            # resume from the last completed phase
./build.sh --status   # which phases are done
./build.sh --from 3   # restart from phase 3
./build.sh --dry-run  # print what would run, change nothing
./build.sh --help     # all options
```

It never installs or registers anything without an explicit approval flag, and it never touches
your real data folder. Publishing produces a self-contained `win-x64` build under `artifacts/`.
