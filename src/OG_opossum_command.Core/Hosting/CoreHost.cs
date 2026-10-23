using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Seeding;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Hosting;

/// <summary>
/// Composes Core for one data root: opens and migrates the database, reconciles interrupted runs, loads
/// profiles, seeds examples on first run, loads and validates workflows, and builds the engine.
/// The desktop app, the smoke modes, and tests all start here, differing only in the launchers they pass.
/// </summary>
public sealed class CoreHost : IDisposable
{
    private readonly Dictionary<ProfileId, ProfileWorkflows> _workflows = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private bool _disposed;

    private CoreHost(IAppPaths paths, ActionRegistry actions, SqliteHistoryStore history, ProfileCatalog profiles, WorkflowEngine engine, TimeProvider time, IReadOnlyList<ProfileSeedingResult> seeding, int interrupted)
    {
        Paths = paths;
        Actions = actions;
        History = history;
        Profiles = profiles;
        Engine = engine;
        Seeding = seeding;
        InterruptedOnStartup = interrupted;
        _time = time;
    }

    public IAppPaths Paths { get; }

    public ActionRegistry Actions { get; }

    public SqliteHistoryStore History { get; }

    public ProfileCatalog Profiles { get; }

    public WorkflowEngine Engine { get; }

    /// <summary>What first-run seeding did, per profile.</summary>
    public IReadOnlyList<ProfileSeedingResult> Seeding { get; }

    /// <summary>Runs found active in the database at startup and marked Interrupted.</summary>
    public int InterruptedOnStartup { get; }

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

    /// <summary>Opens Core for <paramref name="paths"/>. Creates the data root if needed.</summary>
    /// <param name="paths">Where everything lives (rule 6).</param>
    /// <param name="launchers">Real launchers for the app; recording fakes for tests and smoke modes.</param>
    /// <param name="examplesRoot">Folder holding <c>work\</c> and <c>personal\</c> example workflows; may not exist.</param>
    /// <param name="options">Engine limits.</param>
    /// <param name="time">Clock; tests may inject one.</param>
    public static CoreHost Open(IAppPaths paths, LauncherSet launchers, string examplesRoot, EngineOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(launchers);
        ArgumentException.ThrowIfNullOrWhiteSpace(examplesRoot);

        var clock = time ?? TimeProvider.System;
        Directory.CreateDirectory(paths.DataRoot);
        Directory.CreateDirectory(paths.ProfilesDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);

        var history = SqliteHistoryStore.Open(paths.DatabasePath);
        try
        {
            var interrupted = history.MarkInterrupted(clock.GetUtcNow());
            var actions = ActionRegistry.CreateDefault(launchers);
            var profiles = ProfileCatalog.Load(paths, actions);
            foreach (var profile in profiles.Profiles)
            {
                history.UpsertProfile(profile);
            }

            var seeding = FirstRunSeeder.Seed(profiles.Profiles, examplesRoot);
            var engine = new WorkflowEngine(actions, history, options, clock);
            var host = new CoreHost(paths, actions, history, profiles, engine, clock, seeding, interrupted);
            foreach (var profile in profiles.Profiles)
            {
                host.ReloadWorkflows(profile.Id);
            }

            return host;
        }
        catch
        {
            history.Dispose();
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

    /// <summary>Re-reads one profile's workflow folder from disk and records the definitions in history.</summary>
    public ProfileWorkflows ReloadWorkflows(ProfileId profileId)
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

        return loaded;
    }

    /// <summary>Starts a workflow of a profile by ID; the engine enforces the allowlist.</summary>
    /// <exception cref="KeyNotFoundException">No loaded workflow has that ID in that profile.</exception>
    public StartResult Run(ProfileId profileId, string workflowId)
    {
        var loaded = GetWorkflows(profileId);
        var workflow = loaded.Workflows.FirstOrDefault(w => w.Id == workflowId)
            ?? throw new KeyNotFoundException($"No workflow '{workflowId}' is loaded for the {profileId.DisplayName()} profile.");
        return Engine.Start(workflow, loaded.Profile);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Engine.Dispose();
        History.Dispose();
    }
}
