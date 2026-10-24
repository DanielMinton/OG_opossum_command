# Opossum Maid: the reasoning hand-off

Opossum Maid sorts files with local rules (ADR-061). For **personal** files I can also ask Claude Code
to think harder about them. The app itself never calls an AI and never opens a network connection
(ADR-013, ADR-048). The hand-off is two files and a command I run myself:

```
 OpossumMaid bundle <plan>        Claude Code: /maid-reason <bundle>        OpossumMaid merge <plan> <decisions>
 ───────────────────────────▶  <plan>.bundle.json  ───────────────▶  <plan>.decisions.json  ───────────────▶  plan (unapproved again)
```

Both files live in `<DataRoot>\maid\bundles\`. Decisions ADR-069 (this hand-off), ADR-058 (the maid)
and ADR-012 (the work/personal wall) are the why.

## What goes in a bundle, and what never does

| In | Never in |
|---|---|
| Files found on the **personal side** (under the personal root) | Anything from the work OneDrive, ever (rule 3) |
| With `--include-downloads`: a Downloads file classified personal at ≥ 0.8 **and** with zero work evidence | Any file with work evidence (a work signal, the Company property, a learned work rule) |
| A text snippet of at most **2,000 characters**, scrubbed | SECURITY files (credential-looking names, secret-shaped text) |
| File names, never paths | Full paths, folder names outside the file's own name |
| Image facts (format, size, date taken, camera, software) | The file itself |

The snippet goes through the app's own secret redactor (`OutputRedactor`, ADR-045), and every run of
9 or more digits (account, card and phone numbers) becomes `[number]`. Dates survive.

## `opossum-maid.bundle/1`

```jsonc
{
  "schema": "opossum-maid.bundle/1",
  "planId": "plan-20261024-093000",
  "createdAt": "2026-10-24T09:30:05Z",
  "decisionsFile": "C:\\...\\maid\\bundles\\plan-20261024-093000.decisions.json",
  "instructions": "short version of the rules",
  "fallback": "_Review",
  "categories": [ { "name": "Money", "description": "Bank, taxes, bills, receipts, insurance.", "subfolders": [] } ],
  "items": [
    {
      "id": "i0007",                      // the plan item id; decisions must use it
      "side": "personal",                 // always personal
      "originalName": "Scan_0001.pdf",
      "size": 48211, "modified": "2026-10-20T17:02:11Z",
      "docType": "statement",             // what the local rules think it is
      "title": null,
      "snippet": "Contoso Bank … Account [number] … closing balance …",
      "image": null,                      // or { format, width, height, taken, cameraMake, cameraModel, software, ... }
      "siblings": ["Scan_0002.pdf"],      // other bundled files from the same folder or group
      "existingTags": ["statement"],
      "keywords": ["balance", "statement"],
      "proposal": { "category": "Money", "subfolder": null, "name": "Contoso Bank Statement", "tags": ["statement"], "verdict": "keep", "confidence": 0.62 },
      "group": "g03"
    }
  ]
}
```

## `opossum-maid.decisions/1`

```jsonc
{
  "schema": "opossum-maid.decisions/1",
  "planId": "plan-20261024-093000",
  "decisions": [
    {
      "id": "i0007",
      "category": "Money",                // one of the bundle's categories, or the fallback
      "subfolder": null,                  // only a subfolder listed for that category
      "name": "Contoso Bank Statement",   // no extension, 40 characters at most
      "tags": ["bank", "statement", "2026"],
      "verdict": "keep",                  // keep | junk | review
      "confidence": 0.9,                  // 0 to 1
      "reason": "Monthly statement from the bank; belongs with the other two statements.",
      "group": "contoso-bank"             // optional: the same label on files that belong together
    }
  ]
}
```

## What `merge` accepts

`merge` is strict. It reports one line per decision and refuses rather than guessing:

- `schema` must be `opossum-maid.decisions/1`, and `planId` must be the plan being merged.
- Each `id` must be an item that was **in the bundle**, on the personal side, and not SECURITY. Unknown
  ids, work items and a second decision for the same id are rejected.
- **The side never changes.** Nothing a decision says can move a file across the wall.
- `category` must be a personal category or the fallback. `subfolder` must be listed for that category
  in `taxonomy.json`; otherwise it is ignored.
- `name` goes through the same cleaner as everything else (no path characters, 40 characters at most).
  Saved web pages keep their names.
- `tags`: lower case, hyphenated, at most 8; `group` is added as a tag.
- `junk` on an official, financial, legal or signed file becomes `review`. Those are always kept.
- `reason` is cut to one line of 200 characters.
- Items at the plan's auto-approve confidence (0.75 by default) are ticked. **The plan goes back to
  unapproved**, so I read the report again, then `approve` and `apply`.

## Running it

```bash
OpossumMaid plan                       # read-only; writes plans\<plan>.json and .html
OpossumMaid bundle <plan>              # personal files only; prints the bundle path
# in Claude Code, in this repo:
/maid-reason <path to the bundle>      # writes <plan>.decisions.json next to it
OpossumMaid merge <plan> <path to the decisions>
OpossumMaid report <plan>              # or open plans\<plan>.html again
OpossumMaid approve <plan>
OpossumMaid apply <plan>               # prints the run id; OpossumMaid undo <run> reverses it
```

The command file is [`.claude/commands/maid-reason.md`](../.claude/commands/maid-reason.md). It's
generic and holds no personal data.
