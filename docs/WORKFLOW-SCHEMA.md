# Workflow schema v1

> Status: **current** (Phase 3). The loader and validator live in
> `src/OG_opossum_command.Core/Workflows` (`WorkflowLoader`, `JsonObjectReader`); the actions that
> define each `inputs` shape live in `src/OG_opossum_command.Core/Actions/Windows`. This document
> describes what that code accepts. If they ever disagree, the code is the bug or this file is.

A workflow is one hand-editable JSON file (ADR-008). Each profile has its own folder:

```
%LOCALAPPDATA%\OG_opossum_command\profiles\Work\workflows\*.json
%LOCALAPPDATA%\OG_opossum_command\profiles\Personal\workflows\*.json
```

(`OGOC_DATA_DIR` replaces `%LOCALAPPDATA%\OG_opossum_command` when set; see ADR-020.) On first run,
a profile whose folder is missing or has no `.json` files receives copies of the matching examples
from `examples\workflows\<work|personal>` next to the exe. Existing files are never overwritten.

## How files are read

- Every `*.json` in the folder is loaded, sorted by file name. Other files are ignored.
- JSON comments (`//`, `/* */`) and trailing commas are allowed, to make hand editing forgiving.
- Validation is **strict**: a property that is not listed below is an error (typos are the usual
  cause), a wrong type is an error, and the loader keeps going so one pass reports every problem.
- A file with errors is **not loaded**. Its errors appear on the dashboard under
  "Workflow files with problems" and in `--smoke-test` output. Other files still load.
- Error format: `<file>: <json path>: <message>`, for example
  `C:\...\Work\workflows\focus-mode.json: steps[1].inputs.path: 'path' must point to an .exe file.`
- The whole file is hashed (SHA-256) and recorded in the `workflows` table so history can tell
  which version of a definition a run used.

## Top level

| Property | Type | Required | Rules |
|---|---|---|---|
| `schemaVersion` | integer | yes | Must be `1`. Any other value is rejected with a message naming the version this build understands. |
| `id` | string | yes | Slug: 1–64 lower-case letters, digits, or hyphens, starting with a letter or digit (`^[a-z0-9][a-z0-9-]{0,63}$`). Unique within the profile; a second file with the same id is reported and skipped. |
| `name` | string | yes | Shown on the tile. 1–80 characters, not blank. |
| `description` | string | no | Shown as the tile tooltip. Examples start theirs with `EXAMPLE - edit me`. |
| `profileId` | string | yes | `"work"` or `"personal"`. **Must match the folder the file is in** (ADR-012). A work file in the personal folder is an error, never a silent move. |
| `trigger` | object | yes | `{ "type": "manual" }` is the only trigger in v1. Schedules and events come in a later schema version. |
| `execution` | object | no | See below. |
| `requiresApproval` | boolean | no | **Reserved** for a future AI-assisted mode (ADR-013). Must be `false` or omitted; `true` is rejected so no file can pretend an approval flow exists before it does. |
| `steps` | array of objects | yes | At least one step. |

### `execution`

| Property | Type | Default | Rules |
|---|---|---|---|
| `maxParallelSteps` | integer | `1` | 1–8. How many steps may run at the same time once their dependencies are complete. |
| `onDuplicate` | string | `"focus-existing"` | The only policy in v1: pressing a tile while that workflow is still Queued, Running, or Cancelling does **not** start a second run; the dashboard shows the existing one. |

## Steps

| Property | Type | Required | Rules |
|---|---|---|---|
| `id` | string | yes | Slug (same pattern as the workflow id). Unique within the file. |
| `name` | string | no | Shown in progress and history. Defaults to the action's display name (`Open web page`, `Launch program`, `Open folder`). |
| `action` | string | yes | One of the stable action IDs below. Unknown IDs are rejected with the list of known ones. An action outside the profile's allowlist is rejected here **and** again by the engine at start (ADR-025). |
| `inputs` | object | yes | Shape depends on `action`; validated by the action itself at load time. |
| `timeoutSeconds` | integer | no | 1–3600. Defaults to the action's own default. On timeout the step fails with error code `timeout` and any launched process tree is killed. |
| `dependsOn` | array of strings | no | Step ids that must be **Completed** first. Unknown ids, self-references, and cycles are rejected. If a dependency fails, is skipped, or is cancelled, this step is Skipped (`dependency-failed`). |
| `onFailure` | string | no | `"stop"` (default): no further steps start and the run ends Failed. `"continue"`: steps that do not depend on the failed one still run; the run still ends Failed. |

There is no `retry` in v1. Launching a program is not safely repeatable (spec §06), and the only
retry-safe actions here finish in milliseconds. Retry policy arrives with the durable-automation
phase and a schema bump.

## Actions

All three run through injectable launchers, so tests and the smoke modes never open anything real
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
| `path` | string | — | Full path to an `.exe` (after expansion). Relative paths and non-`.exe` files are rejected at load time; a missing file fails the step at run time with `executable-not-found`. |
| `arguments` | array of strings | `[]` | **Always an array.** Each element is passed as one argument; there is no shell and nothing is ever interpolated into a command string (spec §07, §09). A string here is an error. |
| `workingDirectory` | string | process default | Full path; must exist at run time (`working-directory-not-found`). |
| `waitForExit` | boolean | `false` | `false`: the step completes as soon as the program starts. `true`: the step waits, captures stdout/stderr (bounded to 8 KB per stream, redacted, capped at 4000 stored characters), and fails with `exit-code` on a non-zero exit. Cancellation or timeout kills the **whole process tree**. |

Non-idempotent. Default timeout 5 minutes (only matters with `waitForExit: true`).

## Complete example

```json
{
  "schemaVersion": 1,
  "id": "start-workday",
  "name": "Start Workday",
  "description": "EXAMPLE - edit me. Opens Outlook and Teams on the web, then the Documents folder.",
  "profileId": "work",
  "trigger": { "type": "manual" },
  "execution": { "maxParallelSteps": 2, "onDuplicate": "focus-existing" },
  "steps": [
    { "id": "outlook",   "name": "Open Outlook (web)", "action": "windows.url.open",
      "inputs": { "url": "https://outlook.office.com/mail/" }, "timeoutSeconds": 15 },
    { "id": "teams",     "name": "Open Teams (web)",   "action": "windows.url.open",
      "inputs": { "url": "https://teams.microsoft.com/" }, "timeoutSeconds": 15 },
    { "id": "documents", "name": "Open Documents folder", "action": "windows.folder.open",
      "inputs": { "path": "%USERPROFILE%\\Documents" }, "dependsOn": ["outlook", "teams"], "onFailure": "continue" }
  ]
}
```

## Run states a workflow can reach

`Queued → Running → Completed | Failed | Cancelling → Cancelled`. A run that was still active when
the app stopped is marked `Interrupted` on the next start (never resumed automatically in this
version). `NeedsAttention` is reserved for the recovery review of a later phase. See `docs/ARCHITECTURE.md` §06.

## Profile policy file (optional)

`profiles\<Work|Personal>\profile.json` may narrow which actions that profile can run:

```json
{ "allowedActions": ["windows.url.open", "windows.folder.open"] }
```

Without the file a profile may run every built-in action. An unreadable or invalid policy file is
reported as an error and the profile falls back to the built-in default (ADR-025). The engine
refuses to start any workflow whose steps use an action outside the allowlist, even if the
definition somehow bypassed the loader.
