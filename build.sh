#!/usr/bin/env bash
# ══════════════════════════════════════════════════════════════════════════════
#  OG Opossum Command Central — build.sh
#  Concept → live production, driven by Claude Code in headless mode.
#
#  Each phase = one Claude Code session + a deterministic gate checked here
#  (build, tests, smoke tests). Progress is saved, so a rerun resumes where it
#  stopped. Every completed phase is a git commit + tag (phase-N) you can
#  roll back to.
#
#  HOW TO RUN
#    Git Bash / Rider terminal set to Git Bash:   ./build.sh
#    Rider terminal (PowerShell):  & "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe" ./build.sh
#
#    ./build.sh                    run every remaining phase
#    ./build.sh --status           show progress
#    ./build.sh --from 3           restart from phase 3
#    ./build.sh --only 7           run just phase 7
#    ./build.sh --interactive      pause between phases (default: auto-continue)
#    ./build.sh --dry-run          print what would run, change nothing
#    ./build.sh --approve-install  pre-approve installing to your user folder
#    ./build.sh --approve-startup  pre-approve starting at Windows sign-in
#
#  IF CLAUDE CODE IS RUNNING THIS SCRIPT FOR THE USER
#    • Run it in the background (it takes hours); report each phase result.
#    • Never pass --approve-install or --approve-startup unless the user said
#      yes to that exact question in this conversation. Without a terminal the
#      script treats every unanswered question as "no" and tells you what to ask.
#    • If it stops with BLOCKED, show the user .build/BLOCKED.md. Do not work
#      around it.
#
#  PHASES
#    0  Preflight     tools, .NET 10 (per-user), git, spec copy, model check
#    1  Feasibility   tray app, child process, SQLite on this PC
#    2  Scaffold      solution, projects, rules
#    3  Core loop     tiles → engine → actions → history
#    4  Reliability   scripts, scheduler, locks, hotkey, crash recovery
#    5  Calendar      read OneDrive JSON written by Power Automate
#    6  Hardening     accessibility, security review, acceptance matrix
#    7  Release       publish, verify, per-user install with rollback
#
#  NEVER DONE
#    admin rights · policy workarounds · network listeners · signing in to any
#    tenant · claude.ai connectors or MCP servers · AI calls inside the app
# ══════════════════════════════════════════════════════════════════════════════
set -euo pipefail

# ── Helpers (defined first so everything below can use them) ─────────────────
c_ok=$'\e[32m'; c_warn=$'\e[33m'; c_err=$'\e[31m'; c_dim=$'\e[2m'; c_b=$'\e[1m'; c_0=$'\e[0m'
say()  { printf '%s\n' "${c_b}▸ $*${c_0}"; }
ok()   { printf '%s\n' "${c_ok}✓ $*${c_0}"; }
warn() { printf '%s\n' "${c_warn}! $*${c_0}"; }
die()  { printf '%s\n' "${c_err}✗ $*${c_0}" >&2; exit 1; }
dim()  { printf '%s\n' "${c_dim}$*${c_0}"; }
[[ -n "${OGOC_IN_SESSION:-}" ]] && die "build.sh must not be run from inside a build session. Only the user (or the user's own Claude session) runs it."

# ── Configuration ────────────────────────────────────────────────────────────
APP="OG_opossum_command"                       # code name: solution, projects, exe, folders
DISPLAY_NAME="OG Opossum Command Central"      # what the user sees
MODEL="${OGOC_MODEL:-claude-fable-5-1}"
PERMISSION_MODE="${OGOC_PERMISSION_MODE:-auto}"
ULTRACODE="${OGOC_ULTRACODE:-1}"               # 1 = multi-agent workflows inside each phase

# Per-phase choices. OGOC_MODEL / OGOC_ULTRACODE, if set, still override every phase.
declare -A PHASE_MODEL=( [1]=claude-fable-5-1 [2]=claude-fable-5-1 [3]=claude-fable-5-1
                         [4]=claude-fable-5-1 [5]=claude-fable-5-1 [6]=claude-fable-5-1 )
declare -A PHASE_ULTRA=( [1]=0 [2]=0 [3]=1 [4]=1 [5]=0 [6]=1 )
for p in "${!PHASE_MODEL[@]}"; do
  [[ "${PHASE_MODEL[$p]}" =~ ^claude-[a-z0-9.-]+$ ]] || die "PHASE_MODEL[$p] is not a claude-* model id."
done
# Repair sessions (after a failed gate) step up to ultracode and this model,
# unless OGOC_ULTRACODE / OGOC_MODEL were set explicitly.
ESCALATE_MODEL="${OGOC_ESCALATE_MODEL:-claude-fable-5-1}"
[[ "$ESCALATE_MODEL" =~ ^claude-[a-z0-9.-]+$ ]] || die "OGOC_ESCALATE_MODEL='$ESCALATE_MODEL' is not a claude-* model id."

MAX_BUDGET_USD="${OGOC_MAX_BUDGET_USD:-}"      # optional per-session spend cap, e.g. 40
REPAIR_ATTEMPTS="${OGOC_REPAIR_ATTEMPTS:-2}"
SMOKE_TIMEOUT=90
DOTNET_CHANNEL="10.0"
SPEC_SRC="${OGOC_SPEC:-/c/Users/DMinton/OneDrive - YMCA OF SAN FRANCISCO/_Inbox/OPOSSUM_CONTROL_ARCHITECTURE.md}"
LAST_PHASE=7

case "$PERMISSION_MODE" in
  auto|dontAsk) ;;
  *) die "OGOC_PERMISSION_MODE='$PERMISSION_MODE' is not allowed. Use auto (default) or dontAsk." ;;
esac
[[ "$MODEL" =~ ^claude-[a-z0-9.-]+$ ]] || die "OGOC_MODEL='$MODEL' is not a claude-* model id."
[[ "$REPAIR_ATTEMPTS" =~ ^[0-9]{1,2}$ ]] || die "OGOC_REPAIR_ATTEMPTS must be a whole number 0-99."
REPAIR_ATTEMPTS=$((10#$REPAIR_ATTEMPTS))
[[ -z "$MAX_BUDGET_USD" || "$MAX_BUDGET_USD" =~ ^[0-9]+(\.[0-9]+)?$ ]] || die "OGOC_MAX_BUDGET_USD must be a number."

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STATE_DIR="$ROOT/.build"
STATE_FILE="$STATE_DIR/completed"
LOG_DIR="$STATE_DIR/logs"
BLOCKED_FILE="$STATE_DIR/BLOCKED.md"
[[ -n "${LOCALAPPDATA:-}" ]] || die "LOCALAPPDATA is not set. Run this from Git Bash on Windows."
LAD="$(cygpath -u "$LOCALAPPDATA")"
INSTALL_DIR="$LAD/Programs/$APP"
LIVE_DATA_DIR="$LAD/$APP"
DOTNET_DIR="$LAD/Microsoft/dotnet"
EXE_NAME="$APP.exe"

# Claude Code only auto-detects Git Bash under Program Files. Point the build's
# sessions at a per-user install so they get a Bash tool (PowerShell is denied).
if [[ -z "${CLAUDE_CODE_GIT_BASH_PATH:-}" ]]; then
  for gb in "$LAD/Programs/Git/bin/bash.exe" "/c/Program Files/Git/bin/bash.exe"; do
    if [[ -f "$gb" ]]; then CLAUDE_CODE_GIT_BASH_PATH="$(cygpath -w "$gb")"; break; fi
  done
fi
[[ -n "${CLAUDE_CODE_GIT_BASH_PATH:-}" && -f "$(cygpath -u "$CLAUDE_CODE_GIT_BASH_PATH")" ]] \
  || die "Git Bash not found. Install Git for Windows, or set CLAUDE_CODE_GIT_BASH_PATH to its bin\\bash.exe."
export CLAUDE_CODE_GIT_BASH_PATH

# Headless sessions stop background work 10 minutes after their last reply,
# which cuts off ultracode review workflows. Wait for them instead.
export CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS="${CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS:-0}"

# ── Arguments ────────────────────────────────────────────────────────────────
FROM=""; ONLY=""; INTERACTIVE=0; DRY_RUN=0; APPROVE_INSTALL=0; APPROVE_STARTUP=0
phase_arg() { [[ "${2:-}" =~ ^[0-9]+$ ]] && (( 10#$2 <= LAST_PHASE )) || die "$1 needs a phase number 0-$LAST_PHASE"; echo $((10#$2)); }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from)            FROM="$(phase_arg "$1" "${2:-}")"; shift 2 ;;
    --only)            ONLY="$(phase_arg "$1" "${2:-}")"; shift 2 ;;
    --interactive)     INTERACTIVE=1; shift ;;
    --dry-run)         DRY_RUN=1; shift ;;
    --approve-install) APPROVE_INSTALL=1; shift ;;
    --approve-startup) APPROVE_STARTUP=1; shift ;;
    --status)          mkdir -p "$STATE_DIR"; touch "$STATE_FILE"
                       for p in $(seq 0 $LAST_PHASE); do
                         if grep -qx "$p" "$STATE_FILE"; then echo "  ✓ phase $p"; else echo "  · phase $p"; fi
                       done
                       if [[ -f "$BLOCKED_FILE" ]]; then echo; echo "BLOCKED:"; cat "$BLOCKED_FILE"; fi
                       exit 0 ;;
    -h|--help)         sed -n '2,46p' "$0"; exit 0 ;;
    *)                 die "Unknown option: $1 (see --help)" ;;
  esac
done
[[ -n "$FROM" && -n "$ONLY" ]] && die "Use --from or --only, not both."

# ── State, gates, questions ──────────────────────────────────────────────────
done_phase() { grep -qx "$1" "$STATE_FILE" 2>/dev/null; }
mark_done()  { grep -qx "$1" "$STATE_FILE" 2>/dev/null || echo "$1" >> "$STATE_FILE"; ok "Phase $1 complete"; }

# Hard question. Returns 0 only on an explicit yes (or matching --approve-* flag).
#   ask "<question>" [preapproved 0|1] [flag-name]
ask() {
  local q="$1" pre="${2:-0}" flag="${3:-}" reply=""
  if [[ $DRY_RUN -eq 1 ]]; then dim "[dry-run] would ask: $q"; return 1; fi
  if [[ "$pre" == "1" ]]; then ok "Pre-approved: $q"; return 0; fi
  if [[ ! -t 0 ]]; then
    warn "No terminal to answer: \"$q\" → treated as NO.${flag:+ To approve, ask the user, then rerun with $flag.}"
    return 1
  fi
  read -r -p "${c_warn}? $q [y/N] ${c_0}" reply || true
  [[ "$reply" =~ ^[Yy]$ ]]
}

pause_between() {
  [[ $INTERACTIVE -eq 1 && $DRY_RUN -eq 0 ]] || return 0
  ask "Phase $1 done. Continue to phase $(( $1 + 1 ))?" || { say "Stopped. Resume with: ./build.sh"; exit 0; }
}

with_dotnet() {
  if [[ -x "$DOTNET_DIR/dotnet.exe" ]]; then export PATH="$DOTNET_DIR:$PATH"; fi
  local d; d="$(command -v dotnet 2>/dev/null || true)"
  if [[ -n "$d" ]]; then export DOTNET_ROOT="$(cygpath -w "$(dirname "$d")")"; fi
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
}

# Inside ( ) subshells only: keeps sessions, builds, and tests away from the real data folder.
sandbox_data() { mkdir -p "$STATE_DIR/session-data" && export OGOC_DATA_DIR="$(cygpath -w "$STATE_DIR/session-data")"; }

should_run() {
  local p="$1"
  if [[ -n "$ONLY" ]]; then [[ "$p" == "$ONLY" ]]; return; fi
  if [[ -n "$FROM" ]]; then [[ "$p" -ge "$FROM" ]]; return; fi
  ! done_phase "$p"
}

# .NET 10 'dotnet new sln' creates .slnx by default; accept either.
solution() { if [[ -f "$ROOT/$APP.slnx" ]]; then echo "$APP.slnx"; else echo "$APP.sln"; fi; }

# By name — only after the user agreed to close the app (phase 7).
kill_app() { taskkill.exe //IM "$EXE_NAME" //F >/dev/null 2>&1 || true; }
# Only processes started from this exact exe path (never the user's installed app).
kill_exact() {
  OGOC_KILL_PATH="$(cygpath -w "$1")" powershell.exe -NoProfile -NonInteractive -Command \
    'Get-Process -Name "'"${EXE_NAME%.exe}"'" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $env:OGOC_KILL_PATH } | Stop-Process -Force -ErrorAction SilentlyContinue' >/dev/null 2>&1 || true
}

# ── Rules every Claude session follows (appended to its system prompt) ───────
read -r -d '' GUARDRAILS <<EOF || true
$DISPLAY_NAME BUILD RULES (non-negotiable):
0. NAMING: the product is "$DISPLAY_NAME" (shown to the user: window title, tray tooltip, README). The code name is "$APP": solution, project prefixes, root namespace, exe name ($EXE_NAME via AssemblyName), and data folder. docs/ARCHITECTURE.md calls it "OPOSSUM CONTROL" — that name is superseded. If analyzers flag the underscore (CA1707), suppress CA1707 for the namespace/assembly name only, with a comment, and record it in docs/DECISIONS.md.
1. Source of truth: docs/ARCHITECTURE.md. Decisions already made: docs/DECISIONS.md. Read both first.
2. Standard user only. Never request elevation; never use HKLM, Program Files, Windows services, or scheduled tasks; never bypass or change security policy (WDAC, AppLocker, SmartScreen, Defender, PowerShell execution policy). If something is blocked, STOP: write .build/BLOCKED.md (what, exact error, options for the user) and end the session.
3. Work/personal separation: never sign in to any Microsoft, Google, GitHub, or Azure account or tenant (no pac auth, az login, Connect-MgGraph, gh auth, m365 login, or similar). Never use connectors, MCP servers, or browser automation. The app makes no network calls except handing URLs to the user's default browser at runtime.
4. The app has no network listeners, no remote-control endpoints, no telemetry, and no AI calls.
5. Write only inside the project root, with these exceptions: (a) %LOCALAPPDATA%\\$APP-spike in phase 1 only, deleted afterwards; (b) dotnet/NuGet caches; (c) temp folders tests create under %TEMP% and clean up. NEVER touch %LOCALAPPDATA%\\$APP (the user's real data), %LOCALAPPDATA%\\Programs\\$APP (owned by build.sh), any OneDrive folder, other repos, or the registry. Never install the app, register it at startup, or run build.sh — the user does that. The PowerShell tool is unavailable; use Bash.
6. The app resolves every per-user path through ONE AppPaths service: data root = env OGOC_DATA_DIR if set, else %LOCALAPPDATA%\\$APP. Never use the current directory. Tests always set OGOC_DATA_DIR to a temp folder. build.sh exports OGOC_DATA_DIR to a scratch folder for your session; never unset it.
7. During the build, never open a browser, URL, folder window, or real application, and never run the example workflows for real. Side-effecting actions go through injectable launcher interfaces; tests and smoke modes use fakes that only record calls. Throwaway processes like 'cmd /c echo' or 'ping -n 30 127.0.0.1' are fine for process tests.
8. Prefer the simplest design that meets the spec: two projects (Core, Desktop) plus tests. Allowed dependencies: Microsoft.Data.Sqlite, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, xUnit and its runner. Justify anything else in docs/DECISIONS.md.
9. Make reasonable decisions yourself — the operator pre-approved choices that follow the spec and these rules. Record each meaningful one as a short ADR in docs/DECISIONS.md (decision · why · revisit when).
10. If you start a Workflow or any background task, wait for it to finish and merge its results before your final message. Never end your turn while one is running.
11. Before finishing: 'dotnet build <solution> -warnaserror' and 'dotnet test <solution>' must pass (solution = $APP.slnx or $APP.sln at the root, once it exists). Then commit with a clear message ending with:
    Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
12. End with a 5-line summary: built, decided, left, risks, anything the user must do.
EOF

# MCP servers are switched off entirely (--strict-mcp-config, no --mcp-config).
# This deny list is a second layer in case any still load.
DENIED_TOOLS=(
  mcp__claude_ai_Microsoft_365 mcp__claude_ai_Gmail mcp__claude_ai_Google_Drive
  mcp__claude_ai_Google_Calendar mcp__claude_ai_Claude_Docs
  mcp__plugin_power-automate_flowagent mcp__plugin_power-pages_playwright
  mcp__plugin_model-apps_playwright mcp__plugin_mobile-app_microsoft-learn
  mcp__plugin_process-intelligence_process-intelligence mcp__claude-in-chrome
  WebFetch WebSearch PowerShell
)
# Sign-in and tenant commands are denied at the permission layer.
SESSION_SETTINGS='{"enabledPlugins":{"dataverse@power-platform-skills":false,"model-apps@power-platform-skills":false,"mobile-app@power-platform-skills":false,"power-apps-mobile-extension@power-platform-skills":false,"power-automate@power-platform-skills":false,"process-intelligence@power-platform-skills":false,"power-pages@power-platform-skills":false,"copilot-studio@skills-for-copilot-studio":false,"mcp-apps@power-platform-skills":false},"permissions":{"deny":["PowerShell","Skill(dataverse:*)","Skill(model-apps:*)","Skill(mobile-app:*)","Skill(power-apps-mobile-extension:*)","Skill(power-automate:*)","Skill(process-intelligence:*)","Skill(power-pages:*)","Skill(copilot-studio:*)","Skill(mcp-apps:*)","Bash(*build.sh*)","Bash(dotnet tool *)","Bash(dnx *)","Bash(dataverse *)","Bash(*schtasks*)","Bash(*ScheduledTask*)","Bash(*CurrentVersion*Run*)","Bash(*Startup*.lnk*)","Bash(*powershell*)","Bash(*pwsh*)","Bash(pac *)","Bash(az *)","Bash(azd *)","Bash(gh auth *)","Bash(m365 *)","Bash(claude mcp *)","Bash(*Connect-MgGraph*)","Bash(*Connect-AzAccount*)","Bash(*Connect-PnPOnline*)","Bash(*Connect-ExchangeOnline*)","Bash(*ExecutionPolicy*)","Bash(*reg add*)","Bash(*reg.exe add*)","Bash(*Start-Process*-Verb*RunAs*)","PowerShell(*Connect-MgGraph*)","PowerShell(*Connect-AzAccount*)","PowerShell(*ExecutionPolicy*)","PowerShell(*New-ItemProperty*HKCU*Run*)"]}}'
# Used only when OGOC_PERMISSION_MODE=dontAsk (auto decides on its own).
ALLOWED_TOOLS=(Read Edit Write Glob Grep "Bash(dotnet *)" "Bash(git *)" "Bash(ls *)" "Bash(mkdir *)" "Bash(rm -rf spikes/*)" Workflow Agent Skill)

claude_args() {   # prints nothing; fills the global CLAUDE_ARGS array
  CLAUDE_ARGS=(-p --model "$MODEL" --permission-mode "$PERMISSION_MODE" --permission-prompts none
               --strict-mcp-config --settings "$SESSION_SETTINGS"
               --append-system-prompt "$GUARDRAILS")
  [[ -n "$MAX_BUDGET_USD" ]] && CLAUDE_ARGS+=(--max-budget-usd "$MAX_BUDGET_USD")
  [[ "$PERMISSION_MODE" == "dontAsk" ]] && CLAUDE_ARGS+=(--allowedTools "${ALLOWED_TOOLS[@]}")
  CLAUDE_ARGS+=(--disallowedTools "${DENIED_TOOLS[@]}")   # variadic: keep last
}

# run_claude <phase> <prompt>   — prompt goes on stdin (no command-line length limit)
run_claude() {
  local phase="$1" prompt="$2" allow_noop="${3:-0}" escalate="${4:-0}" log="$LOG_DIR/phase-$1.log" rc=0 sid before after h
  local MODEL="${OGOC_MODEL:-${PHASE_MODEL[$phase]:-$MODEL}}"
  local ULTRACODE="${OGOC_ULTRACODE:-${PHASE_ULTRA[$phase]:-$ULTRACODE}}"
  if [[ "$escalate" == "1" ]]; then
    if [[ -z "${OGOC_ULTRACODE:-}" ]]; then ULTRACODE=1; fi
    if [[ -z "${OGOC_MODEL:-}" ]]; then MODEL="$ESCALATE_MODEL"; fi
    warn "Repair session stepped up: model $MODEL · ultracode $ULTRACODE"
  fi
  if done_phase "$phase"; then allow_noop=1; fi
  [[ "$ULTRACODE" == "1" ]] && prompt="ultracode
$prompt"
  if [[ $DRY_RUN -eq 1 ]]; then
    dim "[dry-run] claude session for phase $phase (model $MODEL, mode $PERMISSION_MODE):"
    printf '%s\n' "$prompt" | sed 's/^/    /'
    return 0
  fi
  claude_args
  h="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')" || die "Could not generate a session id."
  sid="${h:0:8}-${h:8:4}-4${h:13:3}-$(printf '%x' $(( (16#${h:16:1} & 3) | 8 )))${h:17:3}-${h:20:12}"
  [[ "$sid" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$ ]] || die "Bad session id: '$sid'"
  CLAUDE_ARGS+=(--session-id "$sid")
  before="$(git -C "$ROOT" rev-parse -q --verify HEAD 2>/dev/null || echo none)"
  say "Claude: phase $phase ${c_dim}(session $sid · log .build/logs/phase-$phase.log)${c_0}"
  printf '\n===== %s · phase %s · session %s =====\n' "$(date '+%F %T')" "$phase" "$sid" >> "$log"
  { ( cd "$ROOT" && with_dotnet && sandbox_data && export ENABLE_CLAUDEAI_MCP_SERVERS=false OGOC_IN_SESSION=1 \
        && printf '%s' "$prompt" | claude "${CLAUDE_ARGS[@]}" ) 2>&1 | tee -a "$log"; } || rc=$?

  if [[ -f "$BLOCKED_FILE" ]]; then
    cat "$BLOCKED_FILE"
    die "Phase $phase is blocked. Resolve it, delete .build/BLOCKED.md, then rerun ./build.sh."
  fi
  [[ $rc -eq 0 ]] || die "Claude session for phase $phase exited with code $rc (budget cap, auth/API error, unknown model, or permission denial). See .build/logs/phase-$phase.log. Inspect with: claude --resume $sid"
  after="$(git -C "$ROOT" rev-parse -q --verify HEAD 2>/dev/null || echo none)"
  if [[ "$after" == "$before" && -z "$(git -C "$ROOT" status --porcelain)" ]]; then
    if [[ "$allow_noop" == "1" ]]; then
      warn "Phase $phase session made no changes; the gate will decide."
    else
      die "Phase $phase session finished without changing anything (it may have ended before a background workflow completed). Rerun ./build.sh, or set OGOC_ULTRACODE=0."
    fi
  fi
}

# Smoke test the built app with a throwaway data folder and a hard timeout.
#   smoke <exe> <mode: --smoke-test|--smoke-ui> [data-dir]
smoke() {
  local exe="$1" mode="$2" data="${3:-$STATE_DIR/smoke-data}" rc=0 out
  [[ -n "$exe" && -f "$exe" ]] || { echo "smoke: $EXE_NAME not found (is AssemblyName set to $APP?)"; return 1; }
  [[ "${3:-}" ]] || { rm -rf "$data"; mkdir -p "$data"; }
  out=$(OGOC_DATA_DIR="$(cygpath -w "$data")" timeout -k 5 "$SMOKE_TIMEOUT" "$exe" "$mode" 2>&1) || rc=$?
  kill_exact "$exe"
  [[ $rc -eq 124 || $rc -eq 137 ]] && { echo "smoke $mode TIMED OUT after ${SMOKE_TIMEOUT}s"; return 1; }
  [[ $rc -eq 0 ]] || { echo "smoke $mode exited $rc"; printf '%s\n' "$out"; return 1; }
  return 0
}

# Deterministic gate: build → test → extra checks. Auto-repairs on failure.
#   gate <phase> <brief> [extra-check-function ...]
gate() {
  local phase="$1" brief="$2"; shift 2
  local attempt=0 step out rc glog="$LOG_DIR/gate-$phase.log" check
  if [[ $DRY_RUN -eq 1 ]]; then dim "[dry-run] gate $phase: build → test${*:+ → $*}"; return 0; fi
  while :; do
    rc=0; step=build
    out=$( cd "$ROOT" && with_dotnet && sandbox_data && timeout -k 10 1200 dotnet build "$(solution)" -warnaserror -nologo -nodeReuse:false -clp:ErrorsOnly 2>&1 ) || rc=$?
    if [[ $rc -eq 0 ]]; then
      step=test
      out=$( cd "$ROOT" && with_dotnet && sandbox_data && timeout -k 10 1800 dotnet test "$(solution)" --no-build --nologo --blame-hang-timeout 5m 2>&1 ) || rc=$?
    fi
    if [[ $rc -eq 124 || $rc -eq 137 ]]; then
      taskkill.exe //IM testhost.exe //F >/dev/null 2>&1 || true
      out+=$'\n'"TIMED OUT: $step exceeded its time limit (hung test, deadlock, or a process that never exits)."
    fi
    for check in "$@"; do
      [[ $rc -eq 0 ]] || break
      step="$check"
      out=$( cd "$ROOT" && with_dotnet && sandbox_data && "$check" 2>&1 ) || rc=$?
    done
    if [[ $rc -eq 0 ]]; then ok "Gate passed (phase $phase)"; return 0; fi

    attempt=$(( attempt + 1 ))
    printf '\n===== %s · gate %s · attempt %s · failed at: %s =====\n%s\n' "$(date '+%F %T')" "$phase" "$attempt" "$step" "$out" >> "$glog"
    if [[ $attempt -gt $REPAIR_ATTEMPTS ]]; then
      printf '%s\n' "$out" | tail -40
      die "Gate $phase failed at '$step' after $REPAIR_ATTEMPTS repair attempts. Full output: .build/logs/gate-$phase.log"
    fi
    warn "Gate $phase failed at '$step' — repair attempt $attempt of $REPAIR_ATTEMPTS"
    run_claude "$phase" "REPAIR — the phase $phase gate failed at step '$step'.
Fix the root cause. Do not weaken, skip, or delete tests; do not disable warnings or analyzers; do not change build.sh.
The gate runs: dotnet build $(solution) -warnaserror, then dotnet test $(solution), then: ${*:-nothing else}.
Tests run with a 5-minute per-test hang limit and 30 minutes overall. Smoke checks run the newest $EXE_NAME under src/$APP.Desktop/bin/Debug (keep the default output path) with --smoke-test / --smoke-ui, OGOC_DATA_DIR set to an empty temp folder, and a ${SMOKE_TIMEOUT}s timeout; they must exit 0.

Phase brief (what this phase must deliver):
$brief

Failure output (last 150 lines; full log in .build/logs/gate-$phase.log):
$(printf '%s\n' "$out" | tail -150)" 1 1
  done
}

debug_exe() { find "$ROOT/src/$APP.Desktop/bin/Debug" -path '*/publish/*' -prune -o -name "$EXE_NAME" -type f -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2- || true; }
check_smoke()    { smoke "$(debug_exe)" --smoke-test; }
check_smoke_ui() { smoke "$(debug_exe)" --smoke-ui; }

# Commit whatever the phase produced and tag it, so every phase is a rollback point.
commit_phase() {
  ( cd "$ROOT"
    git add -A
    if ! git diff --cached --quiet; then
      git commit -q -m "Phase $1 complete (gate passed)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>" || die "git commit failed for phase $1."
    fi
    git tag -f "phase-$1" >/dev/null )
}

# ── Phase briefs ─────────────────────────────────────────────────────────────
read -r -d '' BRIEF1 <<EOF || true
PHASE 1 — FEASIBILITY (spec §17 Phase 0). Prove on this machine, as a standard user, with throwaway code in spikes/feasibility/ (never src/):
(a) a WPF net10.0-windows app with a system-tray icon (H.NotifyIcon.Wpf) builds, starts, and exits cleanly;
(b) launching a child process with an argument array works: capture stdout and exit code of 'cmd /c echo hi', and cancel 'ping -n 30 127.0.0.1' by killing its process tree;
(c) Microsoft.Data.Sqlite can create, write, read, and delete a database under %LOCALAPPDATA%\\$APP-spike;
(d) a self-contained 'dotnet publish -r win-x64' of the spike runs from a folder inside the project.
All spike code lives in exactly ONE project: spikes/feasibility/Spike/Spike.csproj (no other .csproj under spikes/feasibility). Its exe must accept --check: run (b) and (c) headless, print one line per check, exit 0 only if all pass, and exit within 60 seconds.
Write docs/FEASIBILITY.md: a table | Check | Result | Evidence | with PASS or FAIL in the Result column, any policy restriction seen (never work around it), and a final line exactly 'RESULT: PASS' or 'RESULT: FAIL'.
Delete %LOCALAPPDATA%\\$APP-spike afterwards. Commit.
EOF

read -r -d '' BRIEF2 <<EOF || true
PHASE 2 — SCAFFOLD. Create the solution $APP.slnx (dotnet new sln -n $APP) at the project root with:
- src/$APP.Core: class library, net10.0, NO UI or WPF references.
- src/$APP.Desktop: WPF, net10.0-windows, references Core, <AssemblyName>$APP</AssemblyName> so the exe is $EXE_NAME, window title "$DISPLAY_NAME".
- tests/$APP.Core.Tests: xUnit.
Directory.Build.props: Nullable enable, TreatWarningsAsErrors true, AnalysisLevel latest-recommended, ImplicitUsings enable, Deterministic true, <Version>0.1.0</Version>.
Folders: Core/{Workflows,Engine,Actions,Scheduling,History,Profiles,Paths}; Desktop/{Tray,Dashboard,History,CommandPalette,Settings}; examples/workflows/{work,personal}; docs/WORKFLOW-SCHEMA.md (stub).
Core/Paths: the AppPaths service from rule 6, with a test.
Desktop: handle '--smoke-test' (no window: resolve AppPaths, print 'SMOKE OK', exit 0) before any other startup work.
README.md: what it is, how to build/run, where data lives, how to run ./build.sh.
No feature code yet. Commit.
EOF

read -r -d '' BRIEF3 <<EOF || true
PHASE 3 — CORE LOOP (spec §05–07, §13, §14, §17 Phase 1).
CORE
- Workflow schema v1 (JSON; schemaVersion; 'requiresApproval' flag reserved for future AI). Loader + strict validation with readable errors. Document in docs/WORKFLOW-SCHEMA.md.
- IWorkflowAction contract: typed inputs, declared idempotency class, default timeout. Action registry keyed by stable IDs.
- Actions (all through injectable launchers per rule 7): windows.process.launch (exe path + argument ARRAY, never a shell string), windows.url.open (http/https only), windows.folder.open.
- Engine: execution IDs; states Queued, Running, Completed, Failed, Cancelling, Cancelled, Interrupted, NeedsAttention; dependsOn ordering; bounded parallelism; timeouts; cancellation that kills launched process trees; duplicate-press policy (focus-existing).
- History: SQLite at <data root>\\data.db with the tables in spec §13 plus schema_migrations; migrations run on open; WAL mode; bounded, redacted step output.
- Profiles: Work and Personal, each with its own workflows folder (<data root>\\profiles\\<Work|Personal>\\workflows), history scope, and action allowlist. The engine refuses any action outside the profile's allowlist.
- First run: ship examples/workflows as content next to the exe (CopyToOutputDirectory and CopyToPublishDirectory); if a profile's workflows folder is missing or empty, copy that profile's examples in. Never overwrite user files.
DESKTOP
- Tray icon (open, switch profile, quit). Closing the window hides to tray.
- Dashboard per spec §14 mockup: six tiles from the active profile, live step progress, prominent Cancel, recent runs. Keyboard navigable, visible focus, honors Windows high contrast.
- History view: past runs with status and per-step detail.
SMOKE MODES (handled before single-instance checks; never show a window to the user, never touch the real data root, never take the real single-instance lock, never touch the registry)
- --smoke-test: resolve paths, open + migrate the DB, load and validate all profiles and workflows, run a no-op workflow through the engine with fake launchers, print 'SMOKE OK', exit 0 (1 on failure).
- --smoke-ui: real App startup with App.xaml resources; build MainWindow off-screen with ShowActivated=false, Show() it so XAML and bindings load, create then dispose the tray icon, wait for Loaded and the first dashboard bind, print 'SMOKE-UI OK', exit 0. Any unhandled exception → exit 1.
- Both must exit on their own within 30 seconds.
EXAMPLES: examples/workflows/work/start-workday.json (Outlook web, Teams web, a folder) and focus-mode.json; one personal example. Clearly marked as examples to edit.
TESTS: validation, engine state transitions, cancellation kills child processes, allowlist denial, history persists across restart, first-run seeding never overwrites.
Commit.
EOF

read -r -d '' BRIEF4 <<EOF || true
PHASE 4 — RELIABILITY (spec §06, §08, §13, §17 Phase 2).
- Action windows.script.run: trusted scripts only, from the profile's scripts folder; explicit interpreter + argument array; capped stdout/stderr. If PowerShell execution policy blocks it, report it clearly — never pass -ExecutionPolicy.
- Scheduler: triggers in SQLite; explicit time zone; DST-safe; missed-run policy (skip | run-once | catch-up-limited); duplicate suppression; reconciles on startup.
- Resource locks (spec §06): steps may declare locks like window:<id>, file:<path>; same lock → serialized even with maxParallelSteps > 1; released on cancel/failure.
- Single-instance lock per Windows user (second launch focuses the first). Smoke modes are exempt — add a test proving --smoke-test succeeds while another instance holds the lock.
- Crash recovery per spec §13: on start, mark unfinished steps Interrupted; auto-resume only idempotent steps; everything else → NeedsAttention with a clear explanation and Resume / Dismiss in the UI.
- Global hotkey (default Ctrl+Alt+Space, configurable) opens a command palette: type to search workflows by name or alias, Enter runs.
- Quiet mode: suppress non-error notifications.
TESTS: DST and missed-run cases, duplicate trigger suppression, lock serialization, recovery classification after a simulated kill mid-run (no duplicate side effects), single-instance behavior, a DB migration test from a v1 fixture.
Commit.
EOF

read -r -d '' BRIEF5 <<EOF || true
PHASE 5 — CALENDAR WITHOUT APP REGISTRATION (ADR-011).
- Action m365.calendar.readFile: reads a JSON schedule file from a path configured per profile (Work default: %OneDriveCommercial%\\$APP\\calendar.json). Strict schema; tolerant of missing, stale, or mid-sync files (retry once, then show 'calendar unavailable' — never crash).
- Dashboard: compact 'Today' panel with upcoming events; action dashboard.schedule.show.
- The Personal profile cannot read the Work calendar path (engine allowlist) — add a test.
- docs/POWER_AUTOMATE_FLOW.md: click-by-click steps for the USER to build the flow themselves in their YMCA work account with STANDARD connectors only (Office 365 Outlook 'Get calendar view of events (V3)' on a 15-minute recurrence → OneDrive for Business 'Create file' / 'Update file'). Include the exact JSON shape and a sample. If DLP blocks it, the feature simply stays off.
- tests/fixtures/calendar.sample.json plus tests for parsing, staleness, and a missing file.
Do NOT create the flow, open a browser, or sign in to anything. Commit.
EOF

read -r -d '' BRIEF6 <<EOF || true
PHASE 6 — HARDENING (spec §12, §14, §18). Review the whole codebase adversarially, then fix what you find:
- Security and separation: no secrets or tokens in logs or DB; redaction works; allowlists cannot be bypassed with crafted JSON (path traversal, alternate exe paths, URL schemes like file:, javascript:, ms-settings:); no shell-string execution; no listeners.
- Correctness under stress: cancellation, recovery, scheduler, concurrency, locks.
- Accessibility: full keyboard use, focus visuals, high contrast, reduced motion, text scaling, screen-reader names on tiles and status.
- Simplicity: remove dead code and unused abstractions.
Write docs/ACCEPTANCE.md mapping every row of spec §18 to PASS (with test name), MANUAL (with steps for the user), or DEFERRED (with reason).
Add CHANGELOG.md for 0.1.0. Commit.
EOF

# ── Phase 0 · Preflight ──────────────────────────────────────────────────────
install_dotnet() {
  local meta ver url zip want got
  meta=$(curl.exe -fsSL "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$DOTNET_CHANNEL/releases.json") \
    || die "Could not reach the .NET download site (network or proxy policy)."
  ver=$(printf '%s' "$meta" | sed -n 's/.*"latest-sdk"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
  [[ -n "$ver" ]] || die "Could not determine the latest .NET $DOTNET_CHANNEL SDK version."
  url="https://builds.dotnet.microsoft.com/dotnet/Sdk/$ver/dotnet-sdk-$ver-win-x64.zip"
  want=$(printf '%s' "$meta" | tr -d '\r\n' \
    | grep -o "\"url\"[[:space:]]*:[[:space:]]*\"[^\"]*dotnet-sdk-$ver-win-x64\.zip\"[[:space:]]*,[[:space:]]*\"hash\"[[:space:]]*:[[:space:]]*\"[0-9a-fA-F]*\"" \
    | head -1 | sed 's/.*"hash"[[:space:]]*:[[:space:]]*"\([0-9a-fA-F]*\)"/\1/' || true)
  [[ ${#want} -eq 128 ]] || die "Could not find the published SHA-512 for .NET SDK $ver. Refusing to install an unverified download."
  zip="$STATE_DIR/dotnet-sdk-$ver-win-x64.zip"
  say "Downloading .NET SDK $ver (~250 MB, no admin, no installer)"
  curl.exe -fL --progress-bar -o "$zip" "$url" || die "SDK download failed or was blocked."
  got=$(sha512sum "$zip" | cut -d' ' -f1)
  [[ "${got,,}" == "${want,,}" ]] || { rm -f "$zip"; die "SDK download failed its SHA-512 check. Deleted it."; }
  mkdir -p "$DOTNET_DIR"
  powershell.exe -NoProfile -Command "Expand-Archive -LiteralPath '$(cygpath -w "$zip")' -DestinationPath '$(cygpath -w "$DOTNET_DIR")' -Force" \
    || die "Unpacking the SDK failed."
  rm -f "$zip"
  "$DOTNET_DIR/dotnet.exe" --version >/dev/null 2>&1 \
    || die "dotnet.exe was blocked from running. That is device policy — ask IT whether a per-user .NET SDK is allowed. Do not work around it."
  # So Rider and new terminals find it (current user only, HKCU).
  powershell.exe -NoProfile -Command "\$d='$(cygpath -w "$DOTNET_DIR")'; \$p=[Environment]::GetEnvironmentVariable('PATH','User'); if (\$p -notlike \"*\$d*\") { [Environment]::SetEnvironmentVariable('PATH', \$d + ';' + \$p, 'User') }; [Environment]::SetEnvironmentVariable('DOTNET_ROOT', \$d, 'User')" \
    || warn "Could not add .NET to your user PATH. The build still works; Rider may need the SDK path set manually."
  with_dotnet
  ok "Installed .NET SDK $ver → $(cygpath -w "$DOTNET_DIR") (added to your user PATH)"
}

phase0() {
  say "Phase 0 · Preflight"
  net session >/dev/null 2>&1 && die "This shell is running as administrator. Rerun from a normal (non-admin) terminal."
  command -v git    >/dev/null || die "git not found."
  command -v claude >/dev/null || die "Claude Code CLI not found."
  command -v timeout >/dev/null || die "'timeout' not found (it ships with Git Bash)."
  [[ -f "$SPEC_SRC" ]] || die "Spec not found at: $SPEC_SRC (set OGOC_SPEC=...)"
  git config user.email >/dev/null || die "git user.email is not set. Run: git config --global user.email \"you@example.com\" (and user.name)."

  with_dotnet
  if dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL%%.*}\."; then
    ok ".NET $DOTNET_CHANNEL SDK found"
  elif [[ $DRY_RUN -eq 1 ]]; then
    dim "[dry-run] would download, verify (SHA-512), and unpack the .NET $DOTNET_CHANNEL SDK to $(cygpath -w "$DOTNET_DIR")"
  else
    install_dotnet
  fi

  if [[ $DRY_RUN -eq 1 ]]; then dim "[dry-run] would init git, copy the spec, write .gitignore and docs/DECISIONS.md"; return 0; fi
  cd "$ROOT"
  [[ -d .git ]] || git init -q -b main
  mkdir -p docs
  cp "$SPEC_SRC" docs/ARCHITECTURE.md
  [[ -f .gitignore ]] || cat > .gitignore <<'EOF'
bin/
obj/
artifacts/
.build/
.vs/
.idea/
*.user
*.db
*.db-wal
*.db-shm
TestResults/
EOF
  [[ -f docs/DECISIONS.md ]] || cat > docs/DECISIONS.md <<EOF
# Decisions — $DISPLAY_NAME

Pre-approved by the operator (22 Oct 2026). Format: decision · why · revisit when.

- **ADR-000 · Name.** Product "$DISPLAY_NAME"; code name \`$APP\` (exe \`$EXE_NAME\`, data \`%LOCALAPPDATA%\\$APP\`). Supersedes "OPOSSUM CONTROL" in the spec. · Operator's choice. · Never.
- **ADR-004 · One tray process.** Hiding the window keeps workflows running; quitting stops them. · Simplest lifecycle for daily use. · Workflows must survive a full app exit.
- **ADR-005 · WPF on .NET 10 LTS.** · No MSIX packaging, mature tray support, per-user deployment. · A WPF limitation blocks a needed feature.
- **ADR-008 · SQLite for runs and history; JSON files for workflow definitions.** · Workflows stay hand-editable and diffable without an editor. · A visual editor is built.
- **ADR-011 · Calendar via Power Automate → OneDrive JSON file.** · Works with New Outlook; no app registration or admin consent. · IT approves a Graph app registration.
- **ADR-012 · Strict work/personal separation.** No data, tokens, or files cross the YMCA and Ohana tenants; enforced in the engine, not just the UI. · Never.
- **ADR-013 · AI deferred.** Not part of this build. · Spec Phase 5 is started deliberately.
EOF
  commit_phase 0
  ok "Repository, spec, and decision log ready"
}

# Confirms the model id, permission mode, and flags work before hours of work.
probe_claude() {
  [[ $DRY_RUN -eq 1 ]] && { dim "[dry-run] would probe Claude ($MODEL, $PERMISSION_MODE)"; return 0; }
  local out rc=0
  claude_args
  out=$( cd "$ROOT" && export ENABLE_CLAUDEAI_MCP_SERVERS=false \
         && printf 'Use the Bash tool to run: git --version\nThen reply with the word READY followed by that command'"'"'s exact output. If you have no Bash tool, reply with exactly NO_BASH.' | claude "${CLAUDE_ARGS[@]}" 2>&1 ) || rc=$?
  [[ $rc -eq 0 && "$out" == *READY* ]] || die "Claude check failed (model '$MODEL', mode '$PERMISSION_MODE'):
$out"
  [[ "$out" == *"git version"* ]] || die "Claude sessions have no working Bash tool (Git Bash: $CLAUDE_CODE_GIT_BASH_PATH). Phases need it to run dotnet and git:
$out"
  ok "Claude ready ($MODEL, $PERMISSION_MODE, Bash tool OK)"
}

# ── Phases 1–6 ───────────────────────────────────────────────────────────────
phase1() {
  say "Phase 1 · Feasibility on this PC"
  [[ $DRY_RUN -eq 1 ]] || rm -f "$ROOT/docs/FEASIBILITY.md"
  run_claude 1 "$BRIEF1"
  [[ $DRY_RUN -eq 1 ]] && return 0
  local f="$ROOT/docs/FEASIBILITY.md" last spike
  [[ -f "$f" ]] || die "docs/FEASIBILITY.md was not written. See .build/logs/phase-1.log."
  last=$(tr -d '\r' < "$f" | awk 'NF{l=$0} END{print l}')
  if [[ "$last" != "RESULT: PASS" ]] || tr -d '\r' < "$f" | grep -Eq '\|[[:space:]]*FAIL[[:space:]]*\|'; then
    die "Feasibility did not pass. Read docs/FEASIBILITY.md — this usually needs an IT conversation, not a workaround."
  fi
  # Independently re-run the spike's own checks rather than trusting the report.
  spike="$ROOT/spikes/feasibility/Spike/Spike.csproj"
  [[ -f "$spike" ]] || die "Spike project not found at spikes/feasibility/Spike/Spike.csproj (required by the phase 1 brief)."
  ( cd "$ROOT" && with_dotnet && sandbox_data && timeout -k 5 120 dotnet run --project "$spike" -- --check ) \
    || die "The feasibility spike's --check failed when re-run by build.sh."
  ok "This PC can run the app (report and independent re-check both pass)"
}

phase2() { say "Phase 2 · Scaffold";          run_claude 2 "$BRIEF2"; gate 2 "$BRIEF2" check_smoke; }
phase3() { say "Phase 3 · Core loop";         run_claude 3 "$BRIEF3"; gate 3 "$BRIEF3" check_smoke check_smoke_ui; }
phase4() { say "Phase 4 · Reliability";       run_claude 4 "$BRIEF4"; gate 4 "$BRIEF4" check_smoke check_smoke_ui; }
phase5() {
  say "Phase 5 · Calendar (local file only)"
  run_claude 5 "$BRIEF5"; gate 5 "$BRIEF5" check_smoke check_smoke_ui
  warn "Your step, whenever you like: build the calendar flow in your work account using docs/POWER_AUTOMATE_FLOW.md. The app works without it."
}
phase6() { say "Phase 6 · Hardening";         run_claude 6 "$BRIEF6"; gate 6 "$BRIEF6" check_smoke check_smoke_ui; }

# ── Phase 7 · Release ────────────────────────────────────────────────────────
# Sets PHASE_PENDING=1 when published but not installed (phase stays pending).
phase7() {
  say "Phase 7 · Release"
  local version out="$ROOT/artifacts/$APP-win-x64" trial prev="" i
  version=$( { sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" 2>/dev/null || true; } | head -1 )
  version="${version:-0.1.0}"
  if [[ $DRY_RUN -eq 1 ]]; then dim "[dry-run] publish v$version → artifacts/, smoke-test it, ask before installing to $(cygpath -w "$INSTALL_DIR")"; return 0; fi

  with_dotnet; cd "$ROOT"
  rm -rf "$out"
  dotnet publish "src/$APP.Desktop" -c Release -r win-x64 --self-contained true -o "$out" -nologo \
    || die "dotnet publish failed."
  smoke "$out/$EXE_NAME" --smoke-test || die "Published build failed --smoke-test."
  smoke "$out/$EXE_NAME" --smoke-ui   || die "Published build failed --smoke-ui."
  # Migration check: run the new build against a COPY of your real data, if any.
  if [[ -d "$LIVE_DATA_DIR" ]]; then
    trial="$STATE_DIR/upgrade-trial"; rm -rf "$trial"; cp -r "$LIVE_DATA_DIR" "$trial"
    smoke "$out/$EXE_NAME" --smoke-test "$trial" || die "New build failed against a copy of your current data (migration problem). Your real data is untouched."
    rm -rf "$trial"; ok "Upgrade check passed against a copy of your data"
  fi
  ( cd "$out" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) > "$ROOT/artifacts/$APP-$version.sha256"
  ok "Published v$version → artifacts/ (checksums written; unsigned — no code-signing certificate)"

  if ! ask "Install $DISPLAY_NAME v$version to $(cygpath -w "$INSTALL_DIR") (your user folder, no admin)?" "$APPROVE_INSTALL" --approve-install; then
    warn "Not installed. The published build is in artifacts/. Install later with: ./build.sh --only 7 --approve-install"
    commit_phase 7; git tag -f "v$version" >/dev/null
    PHASE_PENDING=1; return 0
  fi

  if tasklist.exe //FI "IMAGENAME eq $EXE_NAME" //NH 2>/dev/null | grep -qi "$EXE_NAME"; then
    ask "$DISPLAY_NAME is running. Close it now? Running workflows will stop." "$APPROVE_INSTALL" --approve-install \
      || { warn "Install cancelled; the app is still running."; PHASE_PENDING=1; return 0; }
    taskkill.exe //IM "$EXE_NAME" >/dev/null 2>&1 || true
    for i in $(seq 1 15); do tasklist.exe //FI "IMAGENAME eq $EXE_NAME" //NH 2>/dev/null | grep -qi "$EXE_NAME" || break; sleep 1; done
    kill_app
  fi

  if [[ -d "$INSTALL_DIR" ]]; then
    prev="$INSTALL_DIR.previous-$(date +%Y%m%d%H%M%S)"
    for i in $(seq 1 10); do mv "$INSTALL_DIR" "$prev" 2>/dev/null && break; sleep 1; done
    [[ -d "$INSTALL_DIR" ]] && die "Could not move the existing install aside (files in use?). Close the app and rerun ./build.sh --only 7."
  fi
  rollback() { rm -rf "$INSTALL_DIR"; if [[ -n "$prev" ]]; then mv "$prev" "$INSTALL_DIR"; fi; }
  if ! { mkdir -p "$INSTALL_DIR" && cp -r "$out/." "$INSTALL_DIR/" && smoke "$INSTALL_DIR/$EXE_NAME" --smoke-test; }; then
    rollback
    die "Install failed (copy or smoke test). ${prev:+Restored the previous version.}"
  fi
  ok "Installed v$version.${prev:+ Previous version kept at $(cygpath -w "$prev") for rollback.}"

  if ask "Start $DISPLAY_NAME automatically when you sign in to Windows?" "$APPROVE_STARTUP" --approve-startup; then
    if MSYS_NO_PATHCONV=1 reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run' /v "$APP" /t REG_SZ \
         /d "\"$(cygpath -w "$INSTALL_DIR/$EXE_NAME")\"" /f >/dev/null 2>&1; then
      ok "Added to sign-in startup (current user only)."
    else
      warn "Could not add the startup entry (policy may block it). Not working around it — start the app from its folder instead."
    fi
  fi
  if [[ -t 0 ]] && ask "Launch it now?"; then
    ( "$INSTALL_DIR/$EXE_NAME" >/dev/null 2>&1 & ); APP_LAUNCHED=1; ok "Running — look for the tray icon."
  fi
  commit_phase 7; git tag -f "v$version" >/dev/null
  return 0
}

# ── Main ─────────────────────────────────────────────────────────────────────
mkdir -p "$STATE_DIR" "$LOG_DIR"; touch "$STATE_FILE"
if [[ -f "$BLOCKED_FILE" ]]; then
  cat "$BLOCKED_FILE"
  die "A previous phase is blocked. Resolve it, delete .build/BLOCKED.md, then rerun."
fi

say "$DISPLAY_NAME build · model $MODEL · mode $PERMISSION_MODE · ultracode $ULTRACODE$([[ $DRY_RUN -eq 1 ]] && echo ' · DRY RUN')"
PROBED=0
APP_LAUNCHED=0
for p in $(seq 0 $LAST_PHASE); do
  if ! should_run "$p"; then
    [[ -z "$ONLY" ]] && done_phase "$p" && dim "  ✓ phase $p already done"
    continue
  fi
  if [[ $p -ge 1 && $p -le 6 && $PROBED -eq 0 ]]; then probe_claude; PROBED=1; fi
  PHASE_PENDING=0
  "phase$p"          # called plainly so set -e stays active inside every phase
  if [[ $DRY_RUN -eq 0 ]]; then
    if [[ $PHASE_PENDING -eq 1 ]]; then warn "Phase $p left pending."; exit 0; fi
    if [[ $p -ge 1 && $p -le 6 ]]; then commit_phase "$p"; fi
    mark_done "$p"
  fi
  if [[ "$p" -lt $LAST_PHASE && -z "$ONLY" ]]; then pause_between "$p"; fi
done

ok "Build sequence finished."
if [[ $APP_LAUNCHED -eq 1 ]]; then
  echo "  Next: open the tray icon and edit your workflows in %LOCALAPPDATA%\\$APP\\profiles\\Work\\workflows,"
else
  echo "  Next: start $(cygpath -w "$INSTALL_DIR/$EXE_NAME") once (it creates your workflow folders), then use its tray icon,"
fi
echo "  and (optional) build the calendar flow per docs/POWER_AUTOMATE_FLOW.md."
