# Feasibility spike (throwaway)

One project only: `Spike/Spike.csproj`. Not referenced from `src/`. Proves, as a standard
user on this PC, that the stack behind OG Opossum Command Central works.

Run everything from the repo root in Git Bash. Package restore uses the repo-root `nuget.config` (ADR-015). `OGOC_DATA_DIR` is irrelevant to the spike;
it writes only under `%LOCALAPPDATA%\OG_opossum_command-spike` and deletes that folder itself.

```bash
# (a) build
dotnet build spikes/feasibility/Spike/Spike.csproj -warnaserror

# (b)+(c) headless checks: one line per check, exit 0 only if all pass, < 60 s
dotnet run --project spikes/feasibility/Spike/Spike.csproj -- --check; echo "exit=$?"

# (a) tray icon: WPF Application + H.NotifyIcon for ~3 s, then exit 0
dotnet run --project spikes/feasibility/Spike/Spike.csproj -- --tray; echo "exit=$?"
tasklist //FI "IMAGENAME eq Spike.exe" //NH      # must show no Spike.exe

# (d) self-contained publish into a folder inside the project (bin/ is gitignored)
dotnet publish spikes/feasibility/Spike/Spike.csproj -c Release -r win-x64 --self-contained true \
  -o spikes/feasibility/Spike/bin/publish
./spikes/feasibility/Spike/bin/publish/Spike.exe --check; echo "exit=$?"

# confirm the spike data folder is gone
ls "$LOCALAPPDATA/OG_opossum_command-spike" 2>&1   # expect: No such file or directory
```

Verified `--check` output on this PC (22 Oct 2026, ~3 s wall time, exit 0; identical from `dotnet run` and from the published exe):

```
PASS process.echo: stdout='hi' stderr-length=0 exit=0
PASS process.cancel-tree: killed tree in 1.8 s; PING count 0->1->0; exit=-1
PASS sqlite.crud: sqlite 3.53.3; wrote+read 1 row; deleted C:\Users\<you>\AppData\Local\OG_opossum_command-spike
ALL CHECKS PASSED
```

If any step shows a policy message (AppLocker, WDAC, SmartScreen, Defender), record it in
`docs/FEASIBILITY.md` as a FAIL with the exact text. Never work around it.
