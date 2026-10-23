using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Recovery;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Seeding;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Hosting;

/// <summary>How to compose Core.</summary>
/// <param name="Engine">Engine limits.</param>
/// <param name="StartSchedulerLoop">Run the scheduler's background loop. Tests turn this off and call <see cref="Scheduler.Tick"/> themselves.</param>
/// <param name="ReviewInterruptedRuns">Run the recovery review at startup. Always on in the app; tests may disable it to inspect raw Interrupted rows.</param>
/// <param name="StartExportsLoop">Run the exports loop (status.json timer, overrun checks; ADR-050..052). Tests turn this off and call <see cref="ExportsHub.Tick"/> themselves.</param>
public sealed record CoreHostOptions(EngineOptions? Engine = null, bool StartSchedulerLoop = true, bool ReviewInterruptedRuns = true, bool StartExportsLoop = true);

/// <summary>A workflow found by <see cref="CoreHost.Search"/>, with the profile it runs under.</summary>
public sealed record WorkflowMatch(WorkflowDefinition Workflow, Profile Profile, int Rank);

/// <summary>
/// Composes Core for one data root: opens and migrates the database, reconciles interrupted runs, loads
/// profiles, seeds examples on first run, loads and validates workflows, builds the engine, reviews interrupted
/// runs (spec §13), and starts the scheduler (spec §08). The desktop app, the smoke modes, and tests all start
/// here, differing only in the launchers they pass.
/// </summary>
public sealed class CoreHost : IDisposable
{
    private readonly Dictionary<ProfileId, ProfileWorkflows> _workflows = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private bool _disposed;
    private bool _opened;

    private CoreHost(IAppPaths paths, LauncherSet launchers, ActionRegistry actions, CalendarService calendar, SqliteHistoryStore history, ProfileCatalog profiles, WorkflowEngine engine, TimeProvider time, IReadOnlyList<ProfileSeedingResult> seeding, IReadOnlyList<Guid> interrupted)
    {
        Paths = paths;
        Launchers = launchers;
        Calendar = calendar;
        Actions = actions;
        History = history;
        Profiles = profiles;
        Engine = engine;
        Seeding = seeding;
        InterruptedOnStartup = interrupted.Count;
        _time = time;
        Scheduler = new Scheduler(history, engine, () => Workflows, time);
        Exports = new ExportsHub(paths, profiles.Profiles, history, engine, () => Workflows, actions, time);
    }

    public IAppPaths Paths { get; }

    /// <summary>The launchers the actions are wired to; the UI uses <see cref="LauncherSet.Urls"/> for its own links so the smoke run never opens a browser.</summary>
    public LauncherSet Launchers { get; }

    public ActionRegistry Actions { get; }

    /// <summary>Calendar reads (ADR-039): the actions and the dashboard share this instance.</summary>
    public CalendarService Calendar { get; }

    public SqliteHistoryStore History { get; }

    public ProfileCatalog Profiles { get; }

    public WorkflowEngine Engine { get; }

    public Scheduler Scheduler { get; }

    /// <summary>
    /// The OneDrive file integrations (ADR-050..052): <c>status.json</c>, the alert outbox, and carryovers. Already fed
    /// the startup recovery decisions; writes a final <c>status.json</c> when the host is disposed after a clean start.
    /// </summary>
    public ExportsHub Exports { get; }

    /// <summary>What first-run seeding did, per profile.</summary>
    public IReadOnlyList<ProfileSeedingResult> Seeding { get; }

    /// <summary>Runs found active in the database at startup and marked Interrupted.</summary>
    public int InterruptedOnStartup { get; }

    /// <summary>What the recovery review decided for each interrupted run at startup.</summary>
    public IReadOnlyList<RecoveryDecision> RecoveryDecisions { get; private set; } = [];

    /// <summary>Loaded workflows and load errors, per profile.</summary>
    public IReadOnlyList<ProfileWorkflows> Workflows
    {
        get
        {
            lock (_gate)
            {
                return ProfileIdExtensions.All.Select(id => _workflows[id]).ToList();
            }
        }
    }

    /// <summary>Runs waiting for the operator (Resume / Dismiss), newest first.</summary>
    public IReadOnlyList<ExecutionSnapshot> NeedsAttention => History.GetExecutionsByStatus(ExecutionStatus.NeedsAttention, null, 50);

    /// <summary>Opens Core for <paramref name="paths"/>. Creates the data root if needed.</summary>
    /// <param name="paths">Where everything lives (rule 6).</param>
    /// <param name="launchers">Real launchers for the app; recording fakes for tests and smoke modes.</param>
    /// <param name="examplesRoot">Folder holding <c>work\</c> and <c>personal\</c> example workflows; may not exist.</param>
    /// <param name="options">Composition options.</param>
    /// <param name="time">Clock; tests may inject one.</param>
    public static CoreHost Open(IAppPaths paths, LauncherSet launchers, string examplesRoot, CoreHostOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(launchers);
        ArgumentException.ThrowIfNullOrWhiteSpace(examplesRoot);

        options ??= new CoreHostOptions();
        var clock = time ?? TimeProvider.System;
        Directory.CreateDirectory(paths.DataRoot);
        Directory.CreateDirectory(paths.ProfilesDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);

        var history = SqliteHistoryStore.Open(paths.DatabasePath);
        CoreHost? host = null;
        try
        {
            var now = clock.GetUtcNow();
            var interrupted = history.MarkInterrupted(now);
            var calendar = new CalendarService(paths, clock);
            var actions = ActionRegistry.CreateDefault(launchers, calendar);
            var profiles = ProfileCatalog.Load(paths, actions);
            foreach (var profile in profiles.Profiles)
            {
                history.UpsertProfile(profile);
            }

            var seeding = FirstRunSeeder.Seed(profiles.Profiles, examplesRoot);
            var engine = new WorkflowEngine(actions, history, options.Engine, clock);
            host = new CoreHost(paths, launchers, actions, calendar, history, profiles, engine, clock, seeding, interrupted);
            foreach (var profile in profiles.Profiles)
            {
                host.ReloadWorkflows(profile.Id, reconcileSchedules: false);
            }

            if (options.ReviewInterruptedRuns)
            {
                // From the database, not just this boot's list: a run an earlier boot marked but never reviewed is not stranded.
                host.RecoveryDecisions = RecoveryReviewer.Review(history.GetPendingRecovery(), history, engine, actions, host.Workflows, now);
            }

            host.Scheduler.Reconcile(now);
            if (options.StartSchedulerLoop)
            {
                host.Scheduler.Start();
            }

            host.Exports.ObserveRecovery(host.RecoveryDecisions);
            if (options.StartExportsLoop)
            {
                host.Exports.Start();
            }

            host._opened = true;

            return host;
        }
        catch
        {
            if (host is not null)
            {
                host.Dispose();
            }
            else
            {
                history.Dispose();
            }

            throw;
        }
    }

    /// <summary>The loaded workflows of one profile.</summary>
    public ProfileWorkflows GetWorkflows(ProfileId profile)
    {
        lock (_gate)
        {
            return _workflows[profile];
        }
    }

    /// <summary>Re-reads one profile's workflow folder from disk, records the definitions in history, and re-registers schedules.</summary>
    public ProfileWorkflows ReloadWorkflows(ProfileId profileId) => ReloadWorkflows(profileId, reconcileSchedules: true);

    /// <summary>Starts a workflow of a profile by ID; the engine enforces the allowlist.</summary>
    /// <exception cref="KeyNotFoundException">No loaded workflow has that ID in that profile.</exception>
    public StartResult Run(ProfileId profileId, string workflowId, StartOptions? options = null)
    {
        var loaded = GetWorkflows(profileId);
        var workflow = loaded.Workflows.FirstOrDefault(w => w.Id == workflowId)
            ?? throw new KeyNotFoundException($"No workflow '{workflowId}' is loaded for the {profileId.DisplayName()} profile.");
        return Engine.Start(workflow, loaded.Profile, options);
    }

    /// <summary>Operator Resume for a run that needs attention (ADR-035).</summary>
    public StartResult Resume(Guid executionId) => RecoveryReviewer.Resume(executionId, History, Engine, Workflows, _time.GetUtcNow());

    /// <summary>Operator Dismiss for a run that needs attention (ADR-035).</summary>
    public void Dismiss(Guid executionId) => RecoveryReviewer.Dismiss(executionId, History, _time.GetUtcNow());

    /// <summary>
    /// Command-palette search across both profiles: matches id, name, and aliases, case-insensitively. Ranked:
    /// alias or name equal (0), name or alias starts with (1), id starts with (2), contains (3). Empty query lists everything.
    /// </summary>
    public IReadOnlyList<WorkflowMatch> Search(string? query, int limit = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var needle = (query ?? string.Empty).Trim();
        var matches = new List<WorkflowMatch>();
        foreach (var loaded in Workflows)
        {
            foreach (var workflow in loaded.Workflows)
            {
                var rank = Rank(workflow, needle);
                if (rank is not null)
                {
                    matches.Add(new WorkflowMatch(workflow, loaded.Profile, rank.Value));
                }
            }
        }

        return matches.OrderBy(m => m.Rank).ThenBy(m => m.Workflow.Name, StringComparer.CurrentCultureIgnoreCase).Take(limit).ToList();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Scheduler.Dispose();

        // Stop the exports loop first so no pass runs during or after the final write, or against a disposed engine or history.
        Exports.Stop();
        if (_opened)
        {
            // Clean exit (Quit has already let the engine settle): one last status.json so the flow sees the final state.
            Exports.WriteFinalStatus();
        }

        Exports.Dispose();
        Engine.Dispose();
        History.Dispose();
    }

    private ProfileWorkflows ReloadWorkflows(ProfileId profileId, bool reconcileSchedules)
    {
        var profile = Profiles.Get(profileId);
        var loaded = new WorkflowLoader(Actions).LoadProfile(profile);
        var now = _time.GetUtcNow();
        foreach (var workflow in loaded.Workflows)
        {
            History.UpsertWorkflow(workflow, now);
        }

        lock (_gate)
        {
            _workflows[profileId] = loaded;
        }

        if (reconcileSchedules)
        {
            Scheduler.Reconcile(now);
        }

        return loaded;
    }

    private static int? Rank(WorkflowDefinition workflow, string needle)
    {
        if (needle.Length == 0)
        {
            return 3;
        }

        var names = workflow.Aliases.Append(workflow.Name).ToList();
        if (names.Any(n => string.Equals(n, needle, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (names.Any(n => n.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (workflow.Id.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (names.Any(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)) || workflow.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return null;
    }
}
