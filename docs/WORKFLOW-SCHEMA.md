# Workflow schema (v1 and v2)

> Status: **current** (Phase 4). The loader and validator live in
> `src/OG_opossum_command.Core/Workflows` (`WorkflowLoader`, `JsonObjectReader`); the actions that
> define each `inputs` shape live in `src/OG_opossum_command.Core/Actions/Windows`; schedules in
> `src/OG_opossum_command.Core/Scheduling`. This document describes what that code accepts. If they
> ever disagree, the code is the bug or this file is.

A workflow is one hand-editable JSON file (ADR-008). Each profile has its own folder:

```
%LOCALAPPDATA%\OG_opossum_command\profiles\Work\workflows\*.json
%LOCALAPPDATA%\OG_opossum_command\profiles\Personal\workflows\*.json
```

(`OGOC_DATA_DIR` replaces `%LOCALAPPDATA%\OG_opossum_command` when set; see ADR-020.) On first run,
a profile whose folder is missing or has no `.json` files receives copies of the matching examples
from `examples\workflows\<work|personal>` next to the exe; likewise `examples\scripts\<profile>` is
copied into the profile's `scripts` folder when that folder is empty. Existing files are never
overwritten.

## Versions

| `schemaVersion` | Status | What it adds |
|---|---|---|
| `1` | still loads | Manual trigger, three Windows actions, dependencies, timeouts, onFailure. |
| `2` | current | `trigger.type: "schedule"`, per-step `locks`, top-level `aliases`, the `windows.script.run` action. |

A v1 file that uses a v2 feature is rejected with "needs `"schemaVersion": 2`" (ADR-032), so an old
file never silently changes behaviour. Any other version is rejected naming the range this build
understands.

## How files are read

- Every `*.json` in the folder is loaded, sorted by file name. Other files are ignored.
- JSON comments (`//`, `/* */`) and trailing commas are allowed, to make hand editing forgiving.
- Validation is **strict**: a property that is not listed below is an error (typos are the usual
  cause), a wrong type is an error, and the loader keeps going so one pass reports every problem.
- A property name that appears **twice** in the same object is an error (it would otherwise be
  "last one wins", so the file would not do what its first lines say). The same holds for
  `profile.json` and the calendar file.
- A NUL character (`\u0000`) in a path or lock is an error, never a crash.
- A file with errors is **not loaded**. Its errors appear on the dashboard under
  "Workflow files with problems" and in `--smoke-test` output. Other files still load.
- Error format: `<file>: <json path>: <message>`, for example
  `C:\...\Work\workflows\focus-mode.json: steps[1].inputs.path: 'path' must point to an .exe file.`
- The whole file is hashed (SHA-256) and recorded in the `workflows` table and on every run, so
  history can tell which version of a definition a run used and recovery can tell whether the file
  changed since (ADR-035).

## Top level

| Property | Type | Required | Rules |
|---|---|---|---|
| `schemaVersion` | integer | yes | `1` or `2` (see above). |
| `id` | string | yes | Slug: 1–64 lower-case letters, digits, or hyphens, starting with a letter or digit (`^[a-z0-9][a-z0-9-]{0,63}$`). Unique within the profile; a second file with the same id is reported and skipped. |
| `name` | string | yes | Shown on the tile. 1–80 characters, not blank. |
| `description` | string | no | Shown as the tile tooltip. Examples start theirs with `EXAMPLE - edit me`. |
| `profileId` | string | yes | `"work"` or `"personal"`. **Must match the folder the file is in** (ADR-012). A work file in the personal folder is an error, never a silent move. |
| `trigger` | object | yes | `{ "type": "manual" }` or a schedule (below). A scheduled workflow can still be run by hand. |
| `execution` | object | no | See below. |
| `requiresApproval` | boolean | no | **Reserved** for a future AI-assisted mode (ADR-013). Must be `false` or omitted; `true` is rejected so no file can pretend an approval flow exists before it does. |
| `aliases` | array of strings | no (v2) | Extra names the command palette matches, 1–40 characters each, e.g. `["swd", "workday"]`. |
| `steps` | array of objects | yes | At least one step. |

### `trigger` (schedule, v2)

```json
"trigger": {
  "type": "schedule",
  "at": ["08:30", "13:00"],
  "days": ["mon", "tue", "wed", "thu", "fri"],
  "timeZone": "Pacific Standard Time",
  "missedRuns": "run-once",
  "expiresAfterMinutes": 10
}
```

| Property | Type | Required | Rules |
|---|---|---|---|
| `at` | array of strings | yes | 1–24 wall-clock times in 24-hour `HH:mm`. Duplicates are merged. |
| `days` | array of strings | no | Weekdays `"mon"` … `"sun"` (or full names). Omitted = every day. |
| `timeZone` | string | yes | **Explicit**, always. A Windows ID (`"Pacific Standard Time"`) or an IANA ID (`"America/Los_Angeles"`) this PC knows. The machine's own zone is never assumed. |
| `missedRuns` | string | no | What to do with occurrences that were due while the app was not running or the PC was asleep. `"skip"` (default): nothing. `"run-once"`: one run now, for the most recent missed occurrence. `"catch-up-limited"`: the most recent missed occurrences, one after another, at most `catchUpLimit`. |
| `catchUpLimit` | integer | with `catch-up-limited` | 1–10. Not allowed with the other policies. |
| `expiresAfterMinutes` | integer | no | 1–1440, default 5. An occurrence found within this many minutes of its time is "on time" and runs at once; older ones are "missed" and follow `missedRuns`. |

Daylight-saving rules (ADR-034): a time that does not exist on a day (spring forward, e.g. 02:30 on
the day clocks jump from 02:00 to 03:00) runs at the first minute that does exist (03:00). A time
that exists twice (fall back) runs **once**, at its first pass. An occurrence never fires twice,
even across a restart: the scheduler records the latest occurrence it has accounted for *before*
starting a run. If the workflow is still running when its next occurrence comes due, that occurrence
is suppressed (focus-existing), not queued. Occurrences more than 7 days old are never considered.
Settings → "Pause all schedules" skips due occurrences instead of deferring them.

### `execution`

| Property | Type | Default | Rules |
|---|---|---|---|
| `maxParallelSteps` | integer | `1` | 1–8. How many steps may run at the same time once their dependencies are complete. |
| `onDuplicate` | string | `"focus-existing"` | The only policy: pressing a tile (or a schedule firing) while that workflow is still Queued, Running, or Cancelling does **not** start a second run; the dashboard shows the existing one. |

## Steps

| Property | Type | Required | Rules |
|---|---|---|---|
| `id` | string | yes | Slug (same pattern as the workflow id). Unique within the file. |
| `name` | string | no | Shown in progress and history. Defaults to the action's display name. |
| `action` | string | yes | One of the stable action IDs below. Unknown IDs are rejected with the list of known ones. An action outside the profile's allowlist is rejected here **and** again by the engine at start (ADR-025). |
| `inputs` | object | yes | Shape depends on `action`; validated by the action itself at load time. |
| `timeoutSeconds` | integer | no | 1–3600. Defaults to the action's own default. On timeout the step fails with error code `timeout` and any launched process tree is killed. |
| `dependsOn` | array of strings | no | Step ids that must be **Completed** first. Unknown ids, self-references, and cycles are rejected. If a dependency fails, is skipped, or is cancelled, this step is Skipped (`dependency-failed`). |
| `onFailure` | string | no | `"stop"` (default): no further steps start and the run ends Failed. `"continue"`: steps that do not depend on the failed one still run; the run still ends Failed. |
| `locks` | array of strings | no (v2) | Resource locks, `"<kind>:<name>"` with kind `window`, `file`, or `custom`. Steps that share a lock never run at the same time, in this run or any other, even with `maxParallelSteps > 1`. `file:` names must be full paths (`%VAR%` allowed) and are compared case-insensitively. Locks are released when the step ends, including on cancel or failure (ADR-033). |

There is no `retry`. Launching a program or script is not safely repeatable (spec §06), and the
retry-safe actions here finish in milliseconds.

## Actions

All actions run through injectable launchers, so tests and the smoke modes never open anything real
(rule 7). `%VAR%` environment references (`%USERPROFILE%`, `%SystemRoot%`, …) are allowed in every
path-like input and are expanded **at run time**, so a file can be shared between machines.

### `windows.url.open` — Open web page

| Input | Type | Rules |
|---|---|---|
| `url` | string | Absolute `http://` or `https://` URL with a host. Anything else (`file:`, `javascript:`, `ms-settings:`, a bare domain) is rejected at load time and refused again at run time. |

Idempotent. Default timeout 15 s. Hands the URL to the user's default browser; nothing is fetched by
the app.

### `windows.folder.open` — Open folder

| Input | Type | Rules |
|---|---|---|
| `path` | string | Full path to a folder (after `%VAR%` expansion). Must exist at run time, else the step fails with `folder-not-found`. |

Idempotent. Default timeout 15 s. Opens the folder in the shell's file manager.

### `windows.process.launch` — Launch program

| Input | Type | Default | Rules |
|---|---|---|---|
| `path` | string | — | Full path to an `.exe` (after expansion). Relative paths and non-`.exe` files are rejected at load time; a missing file fails the step at run time with `executable-not-found`. **Shells and script hosts are refused** (`cmd`, `powershell`, `powershell_ise`, `pwsh`, `wscript`, `cscript`, `mshta`, `rundll32`, `regsvr32`, `wsl`, `bash`, `conhost`, `forfiles`, compared by file name), at load time and again at run time (`interpreter-refused`): put the script in the profile's scripts folder and use `windows.script.run`. |
| `arguments` | array of strings | `[]` | **Always an array.** Each element is passed as one argument; there is no shell and nothing is ever interpolated into a command string (spec §07, §09). A string here is an error. |
| `workingDirectory` | string | process default | Full path; must exist at run time (`working-directory-not-found`). |
| `waitForExit` | boolean | `false` | `false`: the step completes as soon as the program starts. `true`: the step waits, captures stdout/stderr (bounded to 8 KB per stream, redacted, capped at 4000 stored characters), and fails with `exit-code` on a non-zero exit. Cancellation or timeout kills the **whole process tree**. |

Non-idempotent. Default timeout 5 minutes (only matters with `waitForExit: true`).

### `windows.script.run` — Run script (v2)

Runs a **trusted script from the profile's own scripts folder** and nothing else (ADR-031):

```
%LOCALAPPDATA%\OG_opossum_command\profiles\<Work|Personal>\scripts\
```

| Input | Type | Default | Rules |
|---|---|---|---|
| `interpreter` | string | — | `"powershell"` (Windows PowerShell 5.1, run as `powershell.exe -NoProfile -NonInteractive -File <script> <arguments…>`) or `"cmd"` (`cmd.exe /d /c .\<script> <arguments…>`, started in the scripts folder). Always the copy under `%SystemRoot%\System32`. |
| `script` | string | — | A **bare file name** inside the scripts folder: letters, digits, spaces, `.`, `_`, `-`; no folders, no `..`. **No spaces for cmd.** Extension must match the interpreter (`.ps1` ↔ powershell; `.cmd`/`.bat` ↔ cmd). The resolved path is checked again at run time to be inside the folder (`script-outside-folder`); a missing file fails with `script-not-found`. |
| `arguments` | array of strings | `[]` | Passed after the script path, one element per argument, `%VAR%` expanded. **For cmd**, which parses its command line again, no argument may contain `&` `\|` `<` `>` `^` `%` `!` `"` `(` `)` or a line break; this is rejected at load time and checked again at run time (`script-unsafe-for-cmd`). Spaces are fine. Use the powershell interpreter for scripts that need such values or `%VAR%` arguments. |

Non-idempotent. Default timeout 5 minutes. Always waits for exit and captures stdout/stderr (same
bounds as `windows.process.launch`); the script's working directory is the scripts folder. A
non-zero exit fails the step with `exit-code`. If PowerShell refuses the script because of the
PC's **execution policy**, the step fails with `execution-policy-blocked` and a message that says so
and lists the options (ask IT, or write the script as `.cmd`). The app never passes
`-ExecutionPolicy` and never changes policy (rule 2). cmd cannot use a network (UNC) folder as its
current folder, so a cmd script refuses to run when the data folder is on a `\\server\share` path.

### `m365.calendar.readFile` — Read calendar file

Reads the profile's calendar JSON file, which your own Power Automate flow writes to OneDrive
(ADR-011, ADR-039; build steps and the exact file format in `docs/POWER_AUTOMATE_FLOW.md`). No
sign-in, no network: one local file is read. The **path comes from the profile**, never from the
workflow: Work defaults to `%OneDriveCommercial%\OG_opossum_command\calendar.json`, Personal has none,
and either can be changed with `calendarFile` in `profile.json` (below).

| Input | Type | Default | Rules |
|---|---|---|---|
| `maxAgeMinutes` | integer | `60` | 1–1440. A file whose `generatedAt` is older than this still shows its events but is reported as stale. |

Idempotent. Default timeout 20 s. Outcomes: fresh or stale events → completed; missing, empty,
locked, or half-written file (retried once), online-only placeholder, or no file configured →
**completed** with "Calendar unavailable …" (the rest of the workflow runs); well-formed JSON in the
wrong shape → fails with `calendar-invalid`; a path inside the other context's OneDrive → fails with
`calendar-refused`. History stores the status line and counts only, never event details. **Work
profile only by default** (ADR-040).

### `dashboard.schedule.show` — Show today's schedule

No inputs (`"inputs": {}`). Brings the dashboard forward on the run's profile with the **Today**
panel showing that profile's latest calendar read (normally from a `m365.calendar.readFile` step just
before it, via `dependsOn`). It never reads the file itself, so it can't sidestep the allowlist.
Idempotent. Default timeout 10 s. Example: `examples/workflows/work/today-schedule.json`.

## Complete example (v2, scheduled, with a lock and aliases)

```json
{
  "schemaVersion": 2,
  "id": "start-workday",
  "name": "Start Workday",
  "description": "Opens Outlook and Teams on the web, then the Documents folder.",
  "profileId": "work",
  "aliases": ["swd", "workday"],
  "trigger": {
    "type": "schedule",
    "at": ["08:30"],
    "days": ["mon", "tue", "wed", "thu", "fri"],
    "timeZone": "Pacific Standard Time",
    "missedRuns": "run-once"
  },
  "execution": { "maxParallelSteps": 2, "onDuplicate": "focus-existing" },
  "steps": [
    { "id": "outlook",   "name": "Open Outlook (web)", "action": "windows.url.open",
      "inputs": { "url": "https://outlook.office.com/mail/" }, "timeoutSeconds": 15, "locks": ["window:browser"] },
    { "id": "teams",     "name": "Open Teams (web)",   "action": "windows.url.open",
      "inputs": { "url": "https://teams.microsoft.com/" }, "timeoutSeconds": 15, "locks": ["window:browser"] },
    { "id": "documents", "name": "Open Documents folder", "action": "windows.folder.open",
      "inputs": { "path": "%USERPROFILE%\\Documents" }, "dependsOn": ["outlook", "teams"], "onFailure": "continue" }
  ]
}
```

The shipped examples are all manual; none of them is scheduled, so installing the app never starts
anything on its own.

## Run states a workflow can reach

`Queued → Running → Completed | Failed | Cancelling → Cancelled`. A run that was still active when
the app stopped is marked `Interrupted` on the next start and reviewed (ADR-035): if every unfinished
step is safe to repeat (never started, or an idempotent action such as opening a URL) and the file is
unchanged, a new run resumes it automatically, skipping the steps that already completed. Otherwise
the run becomes `NeedsAttention` with a plain explanation and waits on the dashboard for **Resume**
(a new run from the current file, completed steps not repeated) or **Dismiss**. See
`docs/ARCHITECTURE.md` §06 and §13.

## Profile policy file (optional)

`profiles\<Work|Personal>\profile.json` may narrow which actions that profile can run:

```json
{ "allowedActions": ["windows.url.open", "windows.folder.open"] }
```

It may also set the calendar file `m365.calendar.readFile` reads (a full path; `%VAR%` allowed):

```json
{ "calendarFile": "%OneDriveCommercial%\\OG_opossum_command\\calendar.json" }
```

Without the file, Work may run every built-in action and Personal every action **except** the
`m365.*` family (ADR-040). An unreadable or invalid policy file is reported as an error and the
profile **fails closed**: every built-in local action, but no `m365.*` action (ADR-025 as revised by
ADR-040). The engine refuses to start any workflow whose steps use an action outside the
allowlist, even if the definition somehow bypassed the loader.
