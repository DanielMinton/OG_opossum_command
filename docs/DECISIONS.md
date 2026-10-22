# Decisions — OG Opossum Command Central

Pre-approved by the operator (22 Oct 2026). Format: decision · why · revisit when.

- **ADR-000 · Name.** Product "OG Opossum Command Central"; code name `OG_opossum_command` (exe `OG_opossum_command.exe`, data `%LOCALAPPDATA%\OG_opossum_command`). Supersedes "OPOSSUM CONTROL" in the spec. · Operator's choice. · Never.
- **ADR-004 · One tray process.** Hiding the window keeps workflows running; quitting stops them. · Simplest lifecycle for daily use. · Workflows must survive a full app exit.
- **ADR-005 · WPF on .NET 10 LTS.** · No MSIX packaging, mature tray support, per-user deployment. · A WPF limitation blocks a needed feature.
- **ADR-008 · SQLite for runs and history; JSON files for workflow definitions.** · Workflows stay hand-editable and diffable without an editor. · A visual editor is built.
- **ADR-011 · Calendar via Power Automate → OneDrive JSON file.** · Works with New Outlook; no app registration or admin consent. · IT approves a Graph app registration.
- **ADR-012 · Strict work/personal separation.** No data, tokens, or files cross the YMCA and Ohana tenants; enforced in the engine, not just the UI. · Never.
- **ADR-013 · AI deferred.** Not part of this build. · Spec Phase 5 is started deliberately.
- **ADR-014 · Feasibility spike shape.** One console-type (`OutputType=Exe`) WPF project under `spikes/feasibility/Spike`, WPF `Application` and `TaskbarIcon` created in code (no App.xaml), SQLite opened with `Pooling=False` so the file can be deleted, 55 s watchdog in both modes. · `--check` must print to the console under `dotnet run` and exit on its own within 60 s; a second entry point from App.xaml would break the single-project rule. · The spike is deleted or the real Desktop project supersedes it.
- **ADR-015 · Repo-level `nuget.config` with nuget.org as the only source.** · The user-level `%APPDATA%\NuGet\nuget.config` on this PC has an empty `<packageSources>` list, so every restore failed with NU1100; the fix lives in the repo because the build never writes outside the project root (rule 5), and it makes restores reproducible on any machine. nuget.org is reachable anonymously; no sign-in is involved. · The user adds nuget.org to their user-level config, or the project moves to a private feed.
