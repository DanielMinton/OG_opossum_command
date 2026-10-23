# OG Opossum Command Central

A small Windows tray app that runs your repeatable routines (open these apps, these URLs, this
checklist) with one click, keeps a durable history of every run, and keeps work and personal
contexts strictly apart. It is built for one person on one PC, as a **standard user**: no admin
rights, no services, no network listeners, no telemetry, no AI calls.

Code name: `OG_opossum_command` (solution, namespaces, `OG_opossum_command.exe`, data folder).
Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (it still says "OPOSSUM CONTROL"; that name
is superseded). Decisions: [docs/DECISIONS.md](docs/DECISIONS.md).

## Status

Phase 2 (scaffold). The solution builds and tests pass, the exe starts, and the data-folder
resolution is in place. No workflow features yet.

## Layout

| Path | What |
|---|---|
| `src/OG_opossum_command.Core` | Engine, workflows, actions, scheduling, history, profiles, paths. Plain `net10.0`, no UI. |
| `src/OG_opossum_command.Desktop` | WPF tray app (`net10.0-windows`). Builds `OG_opossum_command.exe`. |
| `tests/OG_opossum_command.Core.Tests` | xUnit tests for Core. |
| `examples/workflows/{work,personal}` | Example workflow files per profile (added in Phase 3). |
| `docs/` | Architecture, decisions, feasibility report, workflow schema. |
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

Headless self-check (no window; prints `SMOKE OK` and exits 0 when the data folder resolves):

```bash
src/OG_opossum_command.Desktop/bin/Debug/net10.0-windows/OG_opossum_command.exe --smoke-test
```

The exe is a windowed program, so its output only shows when the caller captures it, for
example `out=$(... --smoke-test)`. The exit code is the contract.

## Where data lives

Every per-user path is derived from one data root by the `AppPaths` service in Core:

| Situation | Data root |
|---|---|
| Normal use | `%LOCALAPPDATA%\OG_opossum_command` |
| `OGOC_DATA_DIR` set (tests, build script, experiments) | that folder; it must be an absolute path |

Under the root: `OG_opossum_command.db` (runs, history, settings), `workflows\`, `profiles\`,
`logs\`. The app never reads or writes relative to the current directory, never touches the
registry, and never writes outside the data root.

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
