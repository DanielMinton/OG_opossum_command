# Calendar feed: build the Power Automate flow yourself

The **Today** panel and the `m365.calendar.readFile` action show your work calendar without any app
registration, admin consent, or sign-in inside OG Opossum Command Central (ADR-011). Instead, **you**
build a small flow in your own YMCA work account. Every 15 minutes it writes your upcoming events to
one JSON file in your work OneDrive, and OneDrive syncs that file to this PC. The app only **reads**
that local file. It never signs in, never calls Microsoft 365, never downloads anything, and never
writes to OneDrive.

You need about 15 minutes. Use only the two **standard** connectors named below. If anything is
blocked (a DLP policy, a missing licence, a disabled connector), stop there: the feature simply stays
off and the panel says "No calendar file is configured" or "Calendar unavailable". Nothing else in
the app depends on it.

> Build this flow **in your work account only**. Never in a personal account, and never pointing at a
> personal OneDrive. The Personal profile is not allowed to read this file (ADR-012, ADR-040).

## What you will end up with

```
Recurrence (every 15 min)
  → Office 365 Outlook: Get calendar view of events (V3)   (now − 12 h … now + 36 h)
  → Data Operation: Select                                 (keep 7 fields per event)
  → Data Operation: Compose                                (wrap them with schemaVersion + generatedAt)
  → OneDrive for Business: Update file                     (OG_opossum_command/calendar.json)
      ↳ if that failed: OneDrive for Business: Create file (first run, or the file was deleted)
```

`Select` and `Compose` are built-in data operations, not connectors.

## Before you start: the OneDrive folder

1. In File Explorer, open your **work** OneDrive (the folder named like `OneDrive - YMCA OF SAN FRANCISCO`).
2. Create a folder named exactly `OG_opossum_command`.
3. Right-click the folder and choose **Always keep on this device**. The app never opens an
   online-only placeholder, because opening one would make OneDrive download it. If the folder isn't
   kept on the device, the panel tells you so.

The app looks for `%OneDriveCommercial%\OG_opossum_command\calendar.json` by default.
`%OneDriveCommercial%` is the variable Windows sets to that work OneDrive folder.

## Build the flow, click by click

Portal labels change from time to time. If a name below doesn't match exactly, look for the closest
one. The JSON at the end of this page is the part that must be exact.

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

   If the designer turns the `events` line into a quoted string, delete it, put the cursor after
   `"events": `, open **Expression**, type `body('Select')` and click **Add**. `events` must be a JSON
   **array**, not text.
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

## Tell the app where the file is (only if you changed the path)

If you kept the folder and file names above, there is nothing to do: the Work profile reads
`%OneDriveCommercial%\OG_opossum_command\calendar.json` on its own. To use a different file, put a
`calendarFile` in the Work profile's policy file
`%LOCALAPPDATA%\OG_opossum_command\profiles\Work\profile.json`:

```json
{ "calendarFile": "%OneDriveCommercial%\\Somewhere Else\\calendar.json" }
```

`%VAR%` references are expanded when the file is read. A Personal-profile path that points into
the work OneDrive is refused, and so is the reverse (ADR-040).

## The exact JSON contract (schemaVersion 1)

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

## Sample

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

## What the app does with the file

- **Reads only.** It never creates, edits, or deletes the file or the folder.
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

## If DLP or policy blocks the flow

If saving the flow fails with a data loss prevention (DLP) message, or a connector is unavailable,
don't look for a workaround. The calendar feature stays off, the Today panel says why, and every
other part of the app works the same. Ask IT only if you want the feature; the flow uses nothing
beyond the standard Office 365 Outlook and OneDrive for Business connectors.
