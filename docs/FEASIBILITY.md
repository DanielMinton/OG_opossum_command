# Feasibility report — OG Opossum Command Central

Phase 1 of the build (spec §17 "Phase 0 · Feasibility gate"), run 22 Oct 2026 on this PC.

- Ran as a **standard user**: process integrity level `Medium Mandatory Level` (S-1-16-8192); no elevation requested, no UAC prompt shown.
- Windows 11 Pro, version 10.0.26300.9711. .NET SDK 10.0.402 (per-user install under `%LOCALAPPDATA%\Microsoft\dotnet`). Git Bash.
- Spike code: `spikes/feasibility/Spike/Spike.csproj` (the only project under `spikes/feasibility`, throwaway, never referenced from `src/`).
- Packages resolved from nuget.org: Microsoft.Data.Sqlite 10.0.13 (SQLitePCLRaw 2.1.13, SQLite engine 3.53.3), H.NotifyIcon.Wpf 2.4.1.

| Check | Result | Evidence |
|---|---|---|
| (a) WPF `net10.0-windows` app with H.NotifyIcon.Wpf builds | PASS | `dotnet build spikes/feasibility/Spike/Spike.csproj -warnaserror` → `Build succeeded. 0 Warning(s) 0 Error(s)` |
| (a) Tray icon starts and the app exits cleanly | PASS | `dotnet run ... -- --tray` printed `TRAY: icon created`, `TRAY: icon disposed, shutting down`, `TRAY OK`; exit code 0; 5 s wall time; `tasklist` showed no `Spike.exe` afterwards |
| (b) Child process from an argument array; capture stdout and exit code | PASS | `ProcessStartInfo("cmd")` + `ArgumentList {"/c","echo","hi"}` → `stdout='hi' stderr-length=0 exit=0` |
| (b) Cancel a child by killing its process tree | PASS | `cmd /c ping -n 30 127.0.0.1`, then `Process.Kill(entireProcessTree: true)` after 1.5 s → `killed tree in 1.8 s; PING count 0->1->0; exit=-1`; `tasklist` showed no `PING.EXE` afterwards |
| (c) Microsoft.Data.Sqlite create, write, read, delete under `%LOCALAPPDATA%\OG_opossum_command-spike` | PASS | `sqlite 3.53.3; wrote+read 1 row` (CREATE TABLE, INSERT, SELECT COUNT = 1, read-back `feasibility`); `spike.db` and the folder deleted by the spike; folder confirmed absent after every run |
| (d) Self-contained `dotnet publish -r win-x64` runs from a folder inside the project | PASS | `dotnet publish -c Release -r win-x64 --self-contained true -o spikes/feasibility/Spike/bin/publish` → exit 0, 252 files, 135 MB; `bin/publish/Spike.exe --check` → `ALL CHECKS PASSED`, exit 0, 3 s; `bin/publish/Spike.exe --tray` → `TRAY OK`, exit 0 |
| `--check` contract: one line per check, exit 0 only if all pass, exits within 60 s | PASS | 3 `PASS` lines + `ALL CHECKS PASSED`; exit 0; 3 s wall time from both `dotnet run` and the published exe (55 s internal watchdog never fired) |
| Exactly one project under `spikes/feasibility` | PASS | `find spikes/feasibility -name "*.csproj"` → `spikes/feasibility/Spike/Spike.csproj` only |
| Policy restrictions (AppLocker, WDAC, SmartScreen, Defender, PowerShell execution policy) | PASS | None observed: no dialogs, no blocked executable or DLL (`e_sqlite3.dll` loaded from the publish folder under the user profile), no access-denied errors, no network block for nuget.org |

## Observations (not restrictions, nothing worked around)

- **Package restore failed at first with NU1100 for every package.** Cause: the user-level `%APPDATA%\NuGet\nuget.config` on this PC has an empty `<packageSources>` list. This is not an employer policy: the file is user-writable, dated 16 Oct 2026, there is no machine-level config under `C:\ProgramData\NuGet`, and `https://api.nuget.org/v3/index.json` answers HTTP 200 anonymously. Fix: a repo-root `nuget.config` that lists nuget.org (ADR-015). Nothing outside the project root was changed.
- The spike written in the earlier, tool-less session needed one compile fix: `using System.IO;` (the WPF SDK's implicit usings omit `System.IO`). `System.Drawing.SystemIcons.Application` compiled and rendered without any extra package.
- The spike's stderr capture was added so the echo check reports both streams, as spec §17 Phase 0 asks.
- `%LOCALAPPDATA%\OG_opossum_command-spike` is created and deleted by the spike itself on every `--check`; it was confirmed absent at the end of the session.

## Verdict

Local execution, child-process control, SQLite persistence under the per-user data root, the WPF tray runtime, and self-contained per-user publishing are all viable on this PC as a standard user.

RESULT: PASS
