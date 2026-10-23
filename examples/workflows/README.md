# Example workflows

These are **examples to edit**, one folder per profile. On first run the app copies a profile's
examples into that profile's workflows folder
(`%LOCALAPPDATA%\OG_opossum_command\profiles\<Work|Personal>\workflows`) only when that folder is
missing or has no `.json` files. It never overwrites a file you already have, so edit the copies
there, not these. The same happens for `examples/scripts/<profile>` and the profile's `scripts`
folder.

The JSON format is documented in `docs/WORKFLOW-SCHEMA.md`. The build never runs these for real;
tests load and validate them with fake launchers only. None of them is scheduled, so installing the
app never starts anything on its own.

| File | Profile | What it does |
|---|---|---|
| `work/start-workday.json` | Work | Outlook on the web and Teams on the web in parallel, then the Documents folder. |
| `work/focus-mode.json` | Work | Microsoft To Do on the web, then a focus-notes file in Notepad. |
| `personal/plan-tomorrow.json` | Personal | Personal calendar on the web, then a tomorrow-notes file in Notepad. |
| `personal/hello-script.json` | Personal | Runs the trusted `scripts/personal/hello.cmd` with two arguments and keeps its output in History; shows `windows.script.run` and `aliases`. |

- `work/` belongs to the Work profile and may reference only work destinations.
- `personal/` belongs to the Personal profile and may reference only personal destinations.

A workflow file never belongs to both profiles (ADR-012): the `profileId` inside the file must match
the folder it lives in, or the file is reported as invalid.
