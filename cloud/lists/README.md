# SharePoint lists

Three custom lists (BaseTemplate 100) on your personal SharePoint site (`__SITE_URL__`), in your work
tenant only. The flows find them by display name. Each `*.json` here describes the columns, where
each value comes from (`source`), and the ordered `spRestCreate` calls. Send those calls one by one, in
order, with the SharePoint connector's **Send an HTTP request to SharePoint** (standard). Use
`spRestDefaultHeaders` on every call and add the per-call `headers` where given (index/unique changes
are `MERGE`s). A field is indexed before it is made unique, because SharePoint insists on that
order.

| List | Written by | Row key | Kept |
|---|---|---|---|
| Opossum Snapshot | Status sync, on each change of `status.json` | `Title = 'status'` (unique), one row | overwritten in place; versioning off |
| Opossum Alerts | Needs-Attention alert, one row per `outbox\*.json` | `EventId` (unique) | 90 days by `At` |
| Opossum Wraps | 4:30 Wrap, one row per working day | `Date` (unique, date only) | 1 year by `Date` |

The monthly **Alerts cleanup** flow deletes Alerts rows with `At` older than 90 days and Wraps rows
with `Date` older than 365 days.

## Columns

**Opossum Snapshot** (from `status.json`): `Profile`, `GeneratedAt`, `AppVersion`, `CompletedToday`,
`FailedCount` = length of `failed`, `FailedSince`, `NeedsAttentionCount` = length of `needsAttention`,
`RunningNow` = length of `runningNow`, `NextScheduled` / `NextScheduledName` = the first
`scheduledToday` entry (the app sorts it by time), `RawJson` = the whole file (a plain multi-line
column holds at most 63,999 characters; Status sync writes the text when it is at most 63,000
characters and an empty value otherwise, never a truncated copy that the next run could not parse).

**Opossum Alerts** (from the alert file): `EventId`, `Event` (`run.failed`, `run.needsAttention`,
`run.overrun`; plain text so a new event name is still stored), `Workflow` (id), `WorkflowName`,
`RunId`, `Step` (null becomes empty), `Message` (scrubbed, at most 200 characters), `At`. Set by the
flows and the Hub, not by the file: `State` (New → Seen → Resolved, default New) and `ResolvedAt`.
`Title` is `<workflowName>: <event>`. The `Open` view shows rows that are not Resolved, newest first.

**Opossum Wraps** (from the 4:30 Wrap run): `Date` (the day wrapped, Pacific), `Done`, `Open`,
`Overdue`, `Waiting` (To Do counts: completed today, not completed, not completed and past due,
status `waitingOnOthers`), `ForDate` and `TomorrowCount` (from `tomorrow.json`), `Tomorrow` (the
`tomorrow.json` item titles, one per line), `Summary` (the Teams message text).

## Changes from the blueprint

- Snapshot: `RunningNow` is a count. `Profile`, `AppVersion`, `FailedSince` and `NextScheduledName`
  were added. `Carryovers` was **dropped**: it is not in `status.json`, and the Hub shows carryovers
  from the latest Wraps row (`ForDate`, `TomorrowCount`, `Tomorrow`), which the 4:30 Wrap writes
  together with `tomorrow.json`. The row key is `Title` = `status`.
- Alerts: `RunId` was added. `profile` from the file is not stored (it is always `Work`).
- Wraps: `ForDate` and `TomorrowCount` were added. `Done`, `Open`, `Overdue` and `Waiting` come from
  To Do, not from any file the app writes.
- `status.json` has no list of completed runs. "Resolve the alerts of workflows that completed" has to
  be inferred: an open alert whose `RunId` is no longer in `needsAttention` or `failed` (or whose
  workflow has a later run) can be marked Resolved.
