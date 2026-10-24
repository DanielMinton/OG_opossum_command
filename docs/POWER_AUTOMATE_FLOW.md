# Power Automate flows: build them yourself

OG Opossum Command Central never signs in, never calls Microsoft 365, and makes no network calls
(ADR-011, ADR-048, ADR-053). Everything it shares with Microsoft 365 goes through **files** in one
folder of your work OneDrive, `%OneDriveCommercial%\OG_opossum_command\`, which OneDrive syncs.

Why files? Because the "proper" way is a Graph app registration, and that needs admin consent I
don't have at work. Power Automate flows run as me, with connectors I'm already allowed to use, so
**you** build the flows yourself, in your own work account:

| Flow | Direction | File | Section |
|---|---|---|---|
| Calendar feed (every 15 min) | flow → app | `calendar.json` | [Calendar feed](#calendar-feed) |
| Morning Briefing (daily) | app → flow | `status.json` | [Morning Briefing](#morning-briefing-read-statusjson) |
| Needs-Attention alert (on each file) | app → flow | `outbox\*.json` | [Needs-Attention alert](#needs-attention-alert-files-in-outbox) |
| Carryovers (daily, 4:30 pm) | flow → app | `tomorrow.json` | [Carryovers](#carryovers-write-tomorrowjson-at-430-pm) |

Each flow is optional and independent. If one is blocked (DLP, licence, connector), skip it: that
feature just stays quiet, and the app shows why in Settings → Exports or the Today panel.

> The app writes only `status.json` and `outbox\*.json` in that folder (ADR-050, ADR-052). It
> reads `calendar.json` and `tomorrow.json` and never writes them. The Personal profile does none of
> this unless you turn it on in its own `profile.json`, pointing at a folder outside the work
> OneDrive (docs/WORKFLOW-SCHEMA.md, `exports`).

## Calendar feed

The **Today** panel and the `m365.calendar.readFile` action show your work calendar without any app
registration, admin consent, or sign-in inside OG Opossum Command Central (ADR-011). Instead, **you**
build a small flow in your own work account. Every 15 minutes it writes your upcoming events to
one JSON file in your work OneDrive, and OneDrive syncs that file to this PC. The app only **reads**
that local file. It never signs in, never calls Microsoft 365, never downloads anything, and never
writes `calendar.json`.

You need about 15 minutes. Use only the two **standard** connectors named below. If anything is
blocked (a DLP policy, a missing licence, a disabled connector), stop there: the feature simply stays
off and the panel says "No calendar file is configured" or "Calendar unavailable". Nothing else in
the app depends on it.

> Build this flow **in your work account only**. Never in a personal account, and never pointing at a
> personal OneDrive. The Personal profile is not allowed to read this file (ADR-012, ADR-040).

### What you will end up with

```
Recurrence (every 15 min)
  → Office 365 Outlook: Get calendar view of events (V3)   (now − 12 h … now + 36 h)
  → Data Operation: Select                                 (keep 7 fields per event)
  → Data Operation: Compose                                (wrap them with schemaVersion + generatedAt)
  → OneDrive for Business: Update file                     (OG_opossum_command/calendar.json)
      ↳ if that failed: OneDrive for Business: Create file (first run, or the file was deleted)
```

`Select` and `Compose` are built-in data operations, not connectors.

### Before you start: the OneDrive folder

1. In File Explorer, open your **work** OneDrive (the folder named like `OneDrive - <Your Org>`).
2. Create a folder named exactly `OG_opossum_command`.
3. Right-click the folder and choose **Always keep on this device**. The app never opens an
   online-only placeholder, because opening one would make OneDrive download it. If the folder isn't
   kept on the device, the panel tells you so.

The app looks for `%OneDriveCommercial%\OG_opossum_command\calendar.json` by default.
`%OneDriveCommercial%` is the variable Windows sets to that work OneDrive folder.

### Build the flow, click by click

Microsoft renames things in the portal whenever it feels like it. If a name below doesn't match
exactly, look for the closest one. The JSON at the end of this page is the part that must be exact.

1. Go to Power Automate in your browser (signed in with your work account, as usual) and choose
   **Create → Scheduled cloud flow**.
2. **Flow name:** `OG Opossum calendar feed`. **Starting:** today, a few minutes from now.
   **Repeat every:** `15` `Minute`. Click **Create**.
3. Open the **Recurrence** trigger and set **Time zone** to `(UTC-08:00) Pacific Time (US & Canada)`
   (or yours).
4. **+ New step** → search for `Office 365 Outlook` → choose **Get calendar view of events (V3)**.
   - **Calendar Id:** `Calendar`
   - **Start Time:** click into the box → **Expression** (fx) → `addHours(utcNow(), -12)` → **Add**/**OK**
   - **End Time:** expression `addHours(utcNow(), 36)`
   - Leave the rest empty.
5. **+ New step** → search for `Select` → choose **Data Operation → Select**.
   - **From:** dynamic content → under *Get calendar view of events (V3)* pick **value**
     (or the expression `body('Get_calendar_view_of_events_(V3)')?['value']`).
   - Switch **Map** to text mode (the small **T** / "Switch Map to text mode" icon) and paste:

     ```json
     {
       "subject": "@{item()?['subject']}",
       "start": "@{item()?['startWithTimeZone']}",
       "end": "@{item()?['endWithTimeZone']}",
       "isAllDay": "@item()?['isAllDay']",
       "location": "@{item()?['location']}",
       "link": "@{item()?['webLink']}",
       "showAs": "@{item()?['showAs']}"
     }
     ```

     Use **startWithTimeZone / endWithTimeZone**, not plain start / end. The plain fields have no
     time-zone offset, and the app rejects them because every event would shift by hours. If your
     connector version names them differently, pick the start/end field whose sample value ends in
     `Z` or `+hh:mm`.
6. **+ New step** → **Data Operation → Compose**. In **Inputs** type this text exactly:

   ```
   {
     "schemaVersion": 1,
     "generatedAt": "@{utcNow()}",
     "source": "power-automate",
     "timeZone": "Pacific Standard Time",
     "events": @{body('Select')}
   }
   ```

   The designer loves to "help" here. If it turns the `events` line into a quoted string, delete it,
   put the cursor after `"events": `, open **Expression**, type `body('Select')` and click **Add**.
   `events` must be a JSON **array**, not text.
7. **+ New step** → `OneDrive for Business` → **Update file**.
   - **File:** browse to `/OG_opossum_command/calendar.json`. On the very first build the file doesn't
     exist yet, so type the path `/OG_opossum_command/calendar.json`.
   - **File Content:** dynamic content → **Outputs** of *Compose*.
8. Add the fallback that creates the file the first time:
   - **+ New step** directly below *Update file* (not a parallel branch) → `OneDrive for Business`
     → **Create file**.
   - **Folder Path:** `/OG_opossum_command` · **File Name:** `calendar.json` ·
     **File Content:** **Outputs** of *Compose*.
   - On *Create file*, open **⋯ → Configure run after**, untick **is successful**, tick
     **has failed**, then **Done**. Now it runs only when *Update file* failed (no file yet).
9. **Save**, then **Test → Manually → Run flow**. The first run creates the file (Update fails,
   Create succeeds). Later runs update it in place. After a minute, check that
   `OG_opossum_command\calendar.json` appears in your OneDrive folder on this PC.

Want the flow to stop? Turn it off in Power Automate. The app then shows the events as **stale**
(older than 60 minutes) and eventually only the status line.

### Tell the app where the file is (only if you changed the path)

If you kept the folder and file names above, there is nothing to do: the Work profile reads
`%OneDriveCommercial%\OG_opossum_command\calendar.json` on its own. To use a different file, put a
`calendarFile` in the Work profile's policy file
`%LOCALAPPDATA%\OG_opossum_command\profiles\Work\profile.json`:

```json
{ "calendarFile": "%OneDriveCommercial%\\Somewhere Else\\calendar.json" }
```

`%VAR%` references are expanded when the file is read. A Personal-profile path that points into
the work OneDrive is refused, and so is the reverse (ADR-040).

### The exact JSON contract (schemaVersion 1)

The app is strict, so a mistake in the flow shows up as a clear message instead of wrong times.

| Field | Type | Rules |
|---|---|---|
| `schemaVersion` | number | Must be `1`. |
| `generatedAt` | string | When the flow ran, ISO 8601 **with an offset** (`utcNow()` gives `…Z`). Staleness is judged from this, never from the file's modified time. |
| `source` | string or null | Free text, informational (`power-automate`). Optional. |
| `timeZone` | string or null | Free text, informational. Optional. |
| `events` | array | 0–500 event objects. Required, even when empty. |

Each event:

| Field | Type | Rules |
|---|---|---|
| `subject` | string, null, or `""` | Blank becomes `(no subject)`; cut at 200 characters. |
| `start`, `end` | string | ISO 8601 **with an offset** (`2026-10-22T09:00:00-07:00`, `2026-10-22T16:00:00Z`, up to 7 fractional digits). `end` must not be before `start`. For all-day events a bare date (`2026-10-22`) is also accepted. |
| `isAllDay` | boolean | Optional, default `false`. All-day events show as "All day" on the day of their `start`. |
| `location` | string, null, or `""` | Optional. |
| `link` | string, null, or `""` | Optional; if present it must be an absolute `https://` address. **Open** next to the event hands it to your default browser. |
| `showAs` | string, null, or `""` | Optional (`busy`, `tentative`, `free`, `oof`, `workingElsewhere`, …). |

Any other property, at the top level or in an event, is an error. That's deliberate: a renamed field
in the flow should fail loudly, not vanish quietly.

### Sample

The same file is used by the tests: `tests/fixtures/calendar.sample.json`.

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-10-22T15:45:00Z",
  "source": "power-automate",
  "timeZone": "Pacific Standard Time",
  "events": [
    {
      "subject": "Team stand-up",
      "start": "2026-10-22T09:00:00.0000000-07:00",
      "end": "2026-10-22T09:15:00.0000000-07:00",
      "isAllDay": false,
      "location": "Microsoft Teams Meeting",
      "link": "https://teams.microsoft.com/l/meetup-join/19%3ameeting_EXAMPLE%40thread.v2/0",
      "showAs": "busy"
    },
    {
      "subject": "Site walkthrough",
      "start": "2026-10-22T19:00:00Z",
      "end": "2026-10-22T20:30:00Z",
      "isAllDay": false,
      "location": "Main branch, front desk",
      "link": "",
      "showAs": "tentative"
    },
    {
      "subject": "Staff appreciation week",
      "start": "2026-10-22",
      "end": "2026-10-23",
      "isAllDay": true,
      "location": null,
      "link": null,
      "showAs": "free"
    },
    {
      "subject": "Budget review",
      "start": "2026-10-23T10:00:00-07:00",
      "end": "2026-10-23T11:00:00-07:00",
      "isAllDay": false,
      "location": "",
      "link": "https://outlook.office.com/calendar/item/EXAMPLE",
      "showAs": "busy"
    }
  ]
}
```

### What the app does with the file

- **Reads only.** It never creates, edits, or deletes `calendar.json`. (The exports may create the
  `OG_opossum_command` folder itself, and only when the OneDrive root already exists; ADR-050.)
- **Missing, empty, locked, or half-written** (OneDrive mid-sync): one retry after about a second,
  then "Calendar unavailable" with the reason. The workflow step still completes, so the rest of the
  workflow runs. It never crashes.
- **Online-only placeholder:** not opened (that would trigger a download). The status line asks you
  to choose *Always keep on this device*.
- **Well-formed JSON in the wrong shape:** the `m365.calendar.readFile` step fails with
  `calendar-invalid` and quotes the first problems, so you can fix the flow.
- **Older than 60 minutes** (by `generatedAt`; a step may set `maxAgeMinutes`): events still show,
  marked stale, with "is the flow still running?".
- **History** keeps only the status line and event counts, never subjects, locations, or links.
- **Personal profile:** can't run `m365.calendar.readFile` by default, and even with an explicit
  allowlist it's refused any path inside the work OneDrive.

### If DLP or policy blocks the flow

If saving the flow fails with a data loss prevention (DLP) message, or a connector is unavailable,
don't look for a workaround. It's annoying as hell, but it's IT's call, not yours. The calendar feature stays off, the Today panel says why, and every
other part of the app works the same. Ask IT only if you want the feature; the flow uses nothing
beyond the standard Office 365 Outlook and OneDrive for Business connectors.

---

## Morning Briefing: read status.json

While the app runs, the Work profile writes `OG_opossum_command\status.json`: on start, every 15
minutes, within a few seconds of any run finishing or needing attention (at most once every 30 s),
and once when you quit (ADR-050). If a write fails because OneDrive or antivirus has the file open,
the app tries again 30 s later (ADR-054). The write is atomic (a temporary file, then a rename), so the flow never sees half a
file. The file holds no step output, arguments, file paths, or links, and every text field is
scrubbed and cut to 200 characters.

```
Recurrence (weekdays 07:30)
  → OneDrive for Business: Get file content using path   (/OG_opossum_command/status.json)
  → Data Operation: Parse JSON                            (schema below)
  → Condition: is generatedAt older than 30 minutes?      (yes → "the app isn't running")
  → Data Operation: Compose                                (the message text)
  → Microsoft Teams: Post message in a chat or channel     (Flow bot → you)
```

1. **Create → Scheduled cloud flow**, name `OG Opossum morning briefing`, repeat every `1` `Week`
   on Monday to Friday at `7:30`, time zone Pacific (or yours).
2. **OneDrive for Business → Get file content using path**, **File Path**
   `/OG_opossum_command/status.json`.
3. **Data Operation → Parse JSON**. **Content:** expression
   `json(base64ToString(body('Get_file_content_using_path')?['$content']))`. (If your connector
   version already returns the text, `json(body('Get_file_content_using_path'))` works instead.)
   **Schema:** paste the schema below.
4. **Condition:** expression
   `less(ticks(body('Parse_JSON')?['generatedAt']), ticks(addMinutes(utcNow(), -30)))` is equal to
   `true`. (`ticks` compares instants, so the local offset in `generatedAt` doesn't matter.)
   - **Yes:** post "OG Opossum hasn't updated since …: is the PC on and the app running?"
   - **No:** build the briefing (next step).
5. **Compose** the message. For example: `length(body('Parse_JSON')?['needsAttention'])` runs need
   attention, `length(body('Parse_JSON')?['failed'])` failed since `failedSince`,
   `body('Parse_JSON')?['completedToday']` completed today, and the `scheduledToday` names and times.
   Use **Select** + **Join** on an array to list names.
6. **Microsoft Teams → Post message in a chat or channel**: **Post as** Flow bot, **Post in** Chat
   with Flow bot, **Recipient** your work address, **Message** the Compose output.

### status.json contract (schemaVersion 1)

| Field | Type | Meaning |
|---|---|---|
| `schemaVersion` | number | `1`. |
| `profile` | string | `"Work"` (the profile's display name). |
| `generatedAt` | string | When the app wrote the file: local time with its offset (`2026-10-23T07:15:02-07:00`). If it's old, the app isn't running. |
| `appVersion` | string | For example `"0.2.0"`. |
| `needsAttention` | array | Runs waiting for Resume or Dismiss (at most 50): `runId`, `workflow` (id), `workflowName`, `step` (the step id it stopped in, or `null`), `reason` (scrubbed), `since`. |
| `failedSince` | string | `generatedAt` minus 24 hours. `failed` covers this window. |
| `failed` | array | Failed runs since `failedSince`, newest first, at most 20: `runId`, `workflow`, `workflowName`, `step` (the failed step's id, or `null`), `error` (scrubbed), `at`. |
| `completedToday` | number | Runs that completed on today's local date. |
| `runningNow` | array | Active runs (queued, running, or cancelling): `workflow`, `startedAt`. |
| `scheduledToday` | array | Remaining schedule occurrences before local midnight, enabled schedules only, empty while schedules are paused (at most 50): `workflow`, `workflowName`, `at`. |

Only Work runs appear in the Work file. All times are local with their offset, to the whole second
and without a fraction: exactly `yyyy-MM-ddTHH:mm:ss±hh:mm`, as in `2026-10-23T07:15:02-07:00`.

```json
{
  "schemaVersion": 1,
  "profile": "Work",
  "generatedAt": "2026-10-23T07:15:02-07:00",
  "appVersion": "0.2.0",
  "needsAttention": [
    {
      "runId": "6f1c2a9e-3b7d-4c55-9d0e-1a2b3c4d5e6f",
      "workflow": "start-workday",
      "workflowName": "Start Workday",
      "step": "launch-erp",
      "reason": "The app stopped while 'launch-erp' was running; it may already have happened.",
      "since": "2026-10-22T17:02:10-07:00"
    }
  ],
  "failedSince": "2026-10-22T07:15:02-07:00",
  "failed": [
    {
      "runId": "0b7e1d42-8c3f-4a9b-b1e2-7f6a5d4c3b2a",
      "workflow": "end-of-day",
      "workflowName": "End of Day",
      "step": "backup",
      "error": "Exit code 2 from [path].",
      "at": "2026-10-22T16:31:44-07:00"
    }
  ],
  "completedToday": 1,
  "runningNow": [
    { "workflow": "focus-mode", "startedAt": "2026-10-23T07:14:40-07:00" }
  ],
  "scheduledToday": [
    { "workflow": "end-of-day", "workflowName": "End of Day", "at": "2026-10-23T16:30:00-07:00" }
  ]
}
```

Parse JSON schema (paste as is; `step`, `reason` and `error` may be `null`):

```json
{
  "type": "object",
  "properties": {
    "schemaVersion": { "type": "integer" },
    "profile": { "type": "string" },
    "generatedAt": { "type": "string" },
    "appVersion": { "type": "string" },
    "needsAttention": { "type": "array", "items": { "type": "object", "properties": {
      "runId": { "type": "string" }, "workflow": { "type": "string" }, "workflowName": { "type": "string" },
      "step": { "type": ["string", "null"] }, "reason": { "type": ["string", "null"] }, "since": { "type": "string" } } } },
    "failedSince": { "type": "string" },
    "failed": { "type": "array", "items": { "type": "object", "properties": {
      "runId": { "type": "string" }, "workflow": { "type": "string" }, "workflowName": { "type": "string" },
      "step": { "type": ["string", "null"] }, "error": { "type": ["string", "null"] }, "at": { "type": "string" } } } },
    "completedToday": { "type": "integer" },
    "runningNow": { "type": "array", "items": { "type": "object", "properties": {
      "workflow": { "type": "string" }, "startedAt": { "type": "string" } } } },
    "scheduledToday": { "type": "array", "items": { "type": "object", "properties": {
      "workflow": { "type": "string" }, "workflowName": { "type": "string" }, "at": { "type": "string" } } } }
  },
  "required": ["schemaVersion", "generatedAt", "needsAttention", "failed", "completedToday", "runningNow", "scheduledToday"]
}
```

If `status.json` isn't there, open the app's Settings → **Exports**. It says whether the file was
written, whether the folder is missing (OneDrive not signed in or not synced yet), or whether
exports are off by policy.

## Needs-Attention alert: files in outbox\

This replaces the HTTP webhook from the original plan: the app makes no network calls, so it drops a
file and **your** flow posts it (ADR-052, ADR-053). When a Work run **fails**, lands in **Needs
attention**, or **overruns** (a step is still running 5 minutes past its timeout), the app writes
one file:

```
OG_opossum_command\outbox\<yyyyMMdd-HHmmss>-<eventId>.json      (timestamp in UTC; eventId is 32 hex digits)
```

The app writes the file under a temporary name **beside** `outbox\` and then moves it in, so "When a
file is created" only ever sees complete files. It writes at most one alert per workflow and event
per 30 minutes. That memory is lost on restart, so the flow should dedupe on `eventId` too. If the
folder is missing or locked when a run fails or needs attention, the app keeps the alert in memory and
tries again every 30 s for up to 30 minutes (ADR-054); a late alert keeps its original `at`. **Quiet
mode in the app does not hold alerts back**; the flow decides about quiet hours. The app also cleans
up after itself: it keeps at most 200 alert files and none older than 7 days, and it deletes only
files whose names match the pattern above, directly in `outbox\`.

```
OneDrive for Business: When a file is created          (folder /OG_opossum_command/outbox)
  → OneDrive for Business: Get file content            (the trigger's file)
  → Data Operation: Parse JSON                          (schema below)
  → Dedupe: skip if this eventId was already posted
  → Quiet hours: between 21:00 and 07:00, Delay until 07:00
  → Microsoft Teams: Post message in a chat or channel  (Flow bot → you)
  → OneDrive for Business: Delete file                  (the trigger's file)
```

1. **Create → Automated cloud flow**, name `OG Opossum alerts`, trigger **OneDrive for Business →
   When a file is created**, **Folder** `/OG_opossum_command/outbox`. In the trigger's **⋯ →
   Settings**, turn **Concurrency control** on with **Degree of parallelism** `1`, so two alerts
   never race on the dedupe file.
2. **OneDrive for Business → Get file content**, **File:** expression
   `triggerOutputs()?['headers']?['x-ms-file-id']`.
3. **Parse JSON**, **Content:** `json(base64ToString(body('Get_file_content')?['$content']))` (or
   `json(body('Get_file_content'))`), **Schema:** below.
4. **Dedupe on eventId.** Keep the last 50 posted IDs in `/OG_opossum_command/alerts-posted.txt`
   (the app never reads or touches that file):
   - **Get file content using path** `/OG_opossum_command/alerts-posted.txt`. On the first run it
     doesn't exist, so set the next steps to also run after it **has failed**.
   - **Condition:** `contains(string(body('Get_file_content_using_path')), body('Parse_JSON')?['eventId'])`
     is `true` → **Terminate** (Succeeded), after deleting the file (step 7).
   - Otherwise, at the end, **Update file** (or **Create file**) `alerts-posted.txt` with
     `join(take(concat(createArray(body('Parse_JSON')?['eventId']), split(string(body('Get_file_content_using_path')), decodeUriComponent('%0A'))), 50), decodeUriComponent('%0A'))`.
5. **Quiet hours.** **Condition:** `convertFromUtc(utcNow(), 'Pacific Standard Time', 'HH:mm')` is
   greater than or equal to `21:00` **or** less than `07:00`. If yes, **Schedule → Delay until**,
   **Timestamp:**
   `convertToUtc(concat(convertFromUtc(addHours(utcNow(), 10), 'Pacific Standard Time', 'yyyy-MM-dd'), 'T07:00:00'), 'Pacific Standard Time')`
   (the next 07:00; it relies on the 21:00 to 07:00 window). Use different hours if you like.
   Skip the delay for `run.needsAttention` if you'd rather hear about those at any hour.
6. **Microsoft Teams → Post message in a chat or channel** (Flow bot, chat with you). For example:
   `@{body('Parse_JSON')?['workflowName']}: @{body('Parse_JSON')?['event']} at @{body('Parse_JSON')?['at']}. @{body('Parse_JSON')?['message']}`
7. **OneDrive for Business → Delete file**, **File:** `triggerOutputs()?['headers']?['x-ms-file-id']`.
   (Or **Move or rename a file** into `/OG_opossum_command/outbox-done/` if you want a record. Don't
   move it to another folder *inside* `outbox\`, which the trigger is watching.)

### Alert file contract (schemaVersion 1)

| Field | Type | Meaning |
|---|---|---|
| `schemaVersion` | number | `1`. |
| `event` | string | `run.failed`, `run.needsAttention`, or `run.overrun`. |
| `eventId` | string | 32 lowercase hex digits, unique per alert (also in the file name). Dedupe on this. |
| `at` | string | When the app raised the alert (a retried alert keeps its first time): local time with offset, to the second, as in `2026-10-23T09:41:07-07:00`. |
| `profile` | string | `"Work"`. |
| `workflow`, `workflowName` | string | The workflow's id and name. |
| `runId` | string | The run (a GUID). |
| `step` | string or null | The step id that failed, was interrupted, or overran. |
| `message` | string | Scrubbed, one line, at most 200 characters, no paths or links. |

```json
{
  "schemaVersion": 1,
  "event": "run.failed",
  "eventId": "3f2b8c1d9e4a4b7c8d6e5f4a3b2c1d0e",
  "at": "2026-10-23T09:41:07-07:00",
  "profile": "Work",
  "workflow": "start-workday",
  "workflowName": "Start Workday",
  "runId": "6f1c2a9e-3b7d-4c55-9d0e-1a2b3c4d5e6f",
  "step": "open-erp",
  "message": "Exit code 1 from [path]."
}
```

Parse JSON schema:

```json
{
  "type": "object",
  "properties": {
    "schemaVersion": { "type": "integer" }, "event": { "type": "string" }, "eventId": { "type": "string" },
    "at": { "type": "string" }, "profile": { "type": "string" }, "workflow": { "type": "string" },
    "workflowName": { "type": "string" }, "runId": { "type": "string" },
    "step": { "type": ["string", "null"] }, "message": { "type": "string" }
  },
  "required": ["schemaVersion", "event", "eventId", "workflow", "message"]
}
```

## Carryovers: write tomorrow.json at 4:30 pm

At the end of your day, this flow writes what you didn't finish to `OG_opossum_command\tomorrow.json`.
The next morning the dashboard's Today panel lists it under **Carryovers from yesterday**. The app
only reads the file and treats it as untrusted: it shows the text, never opens anything from it,
and shows nothing unless `forDate` is **today's local date** (ADR-051).

```
Recurrence (weekdays 16:30)
  → Microsoft To Do (Business): List to-do's by folder (V2)   (your Tasks list)
  → Data Operation: Filter array                               (not completed)
  → Data Operation: Select                                     (title / source / due / note)
  → Data Operation: Compose                                    (wrap with schemaVersion, forDate, generatedAt)
  → OneDrive for Business: Update file                         (/OG_opossum_command/tomorrow.json)
      ↳ if that failed: Create file
```

1. **Scheduled cloud flow**, name `OG Opossum carryovers`, weekly on Monday to Friday at `16:30`,
   Pacific (or yours).
2. **Microsoft To Do (Business) → List to-do's by folder (V2)**, your **Tasks** folder. (Flagged
   emails or Teams follow-ups can be added the same way, with `source` `email` or `teams`.)
3. **Filter array**, **From** the to-do list's **value**, condition
   `@not(equals(item()?['status'], 'completed'))`.
4. **Select** from the filter's body, Map in text mode:

   ```json
   {
     "title": "@{take(item()?['title'], 200)}",
     "source": "todo",
     "due": "@{if(empty(item()?['dueDateTime']?['dateTime']), '', formatDateTime(item()?['dueDateTime']?['dateTime'], 'yyyy-MM-dd'))}",
     "note": ""
   }
   ```

5. **Compose**, Inputs exactly:

   ```
   {
     "schemaVersion": 1,
     "forDate": "@{formatDateTime(addDays(convertFromUtc(utcNow(), 'Pacific Standard Time'), if(equals(dayOfWeek(convertFromUtc(utcNow(), 'Pacific Standard Time')), 5), 3, 1)), 'yyyy-MM-dd')}",
     "generatedAt": "@{utcNow()}",
     "items": @{take(body('Select'), 10)}
   }
   ```

   `forDate` is the next working day (Friday → Monday). As with the calendar, if the designer quotes
   the `items` line, re-add it as the expression `take(body('Select'), 10)`; it must be an array.
6. **OneDrive for Business → Update file** `/OG_opossum_command/tomorrow.json` with the Compose
   output, plus a **Create file** fallback that runs only if Update **has failed** (as in the
   calendar flow, step 8).

### tomorrow.json contract (schemaVersion 1)

The app is strict: anything outside these rules makes it show "tomorrow.json is not in the expected
shape" with the first problems, and no items.

| Field | Type | Rules |
|---|---|---|
| `schemaVersion` | number | Must be `1`. |
| `forDate` | string | `yyyy-MM-dd`. Items show only on that local date; on any other day the panel says "No carryovers for today". |
| `generatedAt` | string | ISO 8601 **with an offset** (`utcNow()` gives `…Z`). |
| `items` | array | 0–10 items. More than 10 is an error, not a truncation, so use `take(…, 10)`. |

Each item:

| Field | Type | Rules |
|---|---|---|
| `title` | string | Required, not blank, at most 200 characters. |
| `source` | string | Required, lowercase: `todo`, `email`, `teams`, or `other`. |
| `due` | string, null, or `""` | Optional; `yyyy-MM-dd`. |
| `note` | string, null, or `""` | Optional; at most 200 characters. |

Also: any other property (top level or in an item) is an error, duplicate keys are an error,
comments and trailing commas are not allowed, the file may be at most 64 KB, and a NUL character
(raw or `\u0000`) makes the whole file refused. Other control characters (tabs, line breaks) are
turned into spaces. Any string longer than 200 characters is rejected, not cut, so keep the
`take(…, 200)` in the Select. The app never follows anything in the file; a link in a title is
just text.

```json
{
  "schemaVersion": 1,
  "forDate": "2026-10-23",
  "generatedAt": "2026-10-22T16:30:05.1234567-07:00",
  "items": [
    { "title": "Finish budget memo", "source": "todo", "due": "2026-10-24", "note": "Draft is in Teams" },
    { "title": "Reply to Sam", "source": "email", "due": null, "note": "" },
    { "title": "Follow up in channel", "source": "teams" },
    { "title": "Call the vendor", "source": "other", "due": "" }
  ]
}
```

(This is the sample `CarryoverFileParserTests.Parses_the_documented_sample` checks.)
