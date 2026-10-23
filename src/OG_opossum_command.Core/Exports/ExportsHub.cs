using System.Globalization;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Recovery;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Exports;

/// <summary>Everything Settings shows about one profile's exports.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="Settings">What <c>profile.json</c> (or the default) switched on.</param>
/// <param name="Status"><c>status.json</c>: last written, folder missing, disabled by policy.</param>
/// <param name="Alerts">The outbox: last alert.</param>
/// <param name="Carryovers">The last <c>tomorrow.json</c> read, or <c>null</c> before the first.</param>
public sealed record ExportsStatus(ProfileId Profile, ExportSettings Settings, StatusExportState Status, AlertOutboxState Alerts, CarryoverReadResult? Carryovers);

/// <summary>
/// Runs the OneDrive file integrations for both profiles (ADR-050..052): one <see cref="StatusExporter"/> and one
/// <see cref="AlertOutbox"/> per profile plus the shared <see cref="CarryoverService"/>. Listens to
/// <see cref="WorkflowEngine.ExecutionChanged"/> (terminal runs → status soon; failed runs → alert), takes crash-recovery
/// decisions through <see cref="ObserveRecovery"/> (Needs attention never passes through the engine), and drives the
/// timers from <see cref="Tick"/>, which a background loop calls every <see cref="LoopInterval"/> once <see cref="Start"/>
/// is called. No network: everything is a local file that OneDrive syncs. State changes are logged once to
/// <c>logs\exports.log</c>.
/// </summary>
public sealed class ExportsHub : IDisposable
{
    /// <summary>How often the background loop ticks.</summary>
    public static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often running steps are checked for an overrun.</summary>
    public static readonly TimeSpan OverrunCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Log file name under the logs folder.</summary>
    public const string LogFileName = "exports.log";

    private readonly IAppPaths _paths;
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly IHistoryStore _history;
    private readonly WorkflowEngine _engine;
    private readonly Func<IReadOnlyList<ProfileWorkflows>> _workflows;
    private readonly ActionRegistry _registry;
    private readonly TimeProvider _time;
    private readonly Dictionary<ProfileId, StatusExporter> _status = [];
    private readonly Dictionary<ProfileId, AlertOutbox> _outboxes = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Lock _logGate = new();
    private readonly Dictionary<ProfileId, (ExportOutcome Outcome, ExportFolderState Folder, string? Message)> _lastLoggedStatus = [];
    private DateTimeOffset? _lastOverrunCheck;
    private Task? _loop;
    private bool _disposed;

    /// <param name="paths">Data root (for the log) and OneDrive roots (for the guard).</param>
    /// <param name="profiles">Both profiles, with their <see cref="Profile.Exports"/>.</param>
    /// <param name="history">Run history.</param>
    /// <param name="engine">The engine whose runs are watched.</param>
    /// <param name="workflows">Loaded workflows (schedules for <c>scheduledToday</c>, step timeouts for overruns).</param>
    /// <param name="registry">Actions, for default step timeouts.</param>
    /// <param name="time">Clock and local zone.</param>
    /// <param name="appVersion">Written into <c>status.json</c>; defaults to <see cref="ExportText.AppVersion"/>.</param>
    /// <param name="newEventId">Alert event ID source (tests).</param>
    public ExportsHub(IAppPaths paths, IReadOnlyList<Profile> profiles, IHistoryStore history, WorkflowEngine engine, Func<IReadOnlyList<ProfileWorkflows>> workflows, ActionRegistry registry, TimeProvider? time = null, string? appVersion = null, Func<Guid>? newEventId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(registry);

        _paths = paths;
        _profiles = profiles;
        _history = history;
        _engine = engine;
        _workflows = workflows;
        _registry = registry;
        _time = time ?? TimeProvider.System;
        Carryovers = new CarryoverService(paths, _time);

        foreach (var profile in profiles)
        {
            var exporter = new StatusExporter(profile, paths, history, () => _engine.ActiveExecutions, workflows, _time, appVersion);
            var outbox = new AlertOutbox(profile, paths, _time, newEventId);
            exporter.Changed += (_, state) => OnStatusChanged(profile.Id, state);
            outbox.Changed += (_, state) => OnStateChanged(profile.Id, $"outbox: {state.Outcome}: {state.Message}");
            _status[profile.Id] = exporter;
            _outboxes[profile.Id] = outbox;
        }

        Carryovers.Changed += (_, result) => RaiseChanged(result.Profile);
        _engine.ExecutionChanged += OnExecutionChanged;
    }

    /// <summary>Raised (on a worker thread) when any export state of a profile changes. Never throws into the caller.</summary>
    public event EventHandler<ProfileId>? Changed;

    /// <summary>Carryovers reader shared with the Today panel.</summary>
    public CarryoverService Carryovers { get; }

    /// <summary>The profile object the hub uses.</summary>
    public Profile GetProfile(ProfileId profile) => _profiles.First(p => p.Id == profile);

    /// <summary>Read-only summary for Settings.</summary>
    public ExportsStatus GetStatus(ProfileId profile) =>
        new(profile, GetProfile(profile).Exports, _status[profile].State, _outboxes[profile].State, Carryovers.GetLatest(profile));

    /// <summary>
    /// Feeds the startup recovery decisions (normally <c>CoreHost.RecoveryDecisions</c>): each run that became Needs
    /// attention raises <see cref="AlertEvents.RunNeedsAttention"/> and asks for a status export.
    /// </summary>
    public void ObserveRecovery(IReadOnlyList<RecoveryDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var now = _time.GetUtcNow();
        foreach (var decision in decisions.Where(d => d.Outcome == RecoveryStates.NeedsAttention))
        {
            try
            {
                var execution = _history.GetExecution(decision.ExecutionId);
                if (execution is null || !_outboxes.TryGetValue(execution.Profile, out var outbox))
                {
                    continue;
                }

                outbox.ObserveNeedsAttention(execution);
                _status[execution.Profile].RequestSoon(now);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log(decision.Profile, $"recovery alert failed: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// One pass: writes each profile's <c>status.json</c> when due (first pass, every 15 min, soon after a run ended, or
    /// 30 s after a failed write), and every <see cref="OverrunCheckInterval"/> retries alerts whose write was skipped or
    /// failed and checks running steps for overruns. Never throws.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var exporter in _status.Values)
        {
            try
            {
                exporter.Tick(now);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log(exporter.Profile, $"status tick failed: {ex.GetType().Name}");
            }
        }

        bool checkOverruns;
        lock (_gate)
        {
            checkOverruns = _lastOverrunCheck is null || now - _lastOverrunCheck.Value >= OverrunCheckInterval || now < _lastOverrunCheck.Value;
            if (checkOverruns)
            {
                _lastOverrunCheck = now;
            }
        }

        if (!checkOverruns)
        {
            return;
        }

        foreach (var outbox in _outboxes.Values)
        {
            try
            {
                outbox.RetryPending(now);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log(outbox.Profile, $"alert retry failed: {ex.GetType().Name}");
            }
        }

        try
        {
            var active = _engine.ActiveExecutions;
            foreach (var outbox in _outboxes.Values.Where(o => o.IsEnabled))
            {
                outbox.CheckOverruns(active, StepTimeout, now);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The engine is shutting down; the next pass tries again.
        }
    }

    /// <summary>Runs <see cref="Housekeep"/> and starts the background loop. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_loop is not null || _stopping.IsCancellationRequested)
            {
                return;
            }

            _loop = Task.Run(LoopAsync);
        }
    }

    /// <summary>
    /// Start-up cleanup in each profile's exports folder: deletes leftover <see cref="AtomicFile"/> temporary files (a
    /// crash between create and move) and prunes the outbox. Touches only the app's own name patterns. Never throws.
    /// </summary>
    public void Housekeep(DateTimeOffset now)
    {
        foreach (var profile in _profiles.Where(p => p.Exports.Folder is not null))
        {
            try
            {
                var folder = ExportFolder.Resolve(profile, _paths, createIfParentExists: false);
                if (folder.IsReady)
                {
                    AtomicFile.DeleteOrphans(folder.FullPath!, now);
                }

                if (_outboxes[profile.Id].IsEnabled)
                {
                    _outboxes[profile.Id].Prune(now);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Tried again at the next start (and the outbox after the next alert).
            }
        }
    }

    /// <summary>
    /// Stops the background loop and waits (at most 2 s) for a pass in flight to finish, so nothing runs against the
    /// engine or history after this returns. Idempotent; called before the final write on Quit. Returns whether the
    /// loop is stopped (always <c>true</c> when it never started).
    /// </summary>
    public bool Stop()
    {
        Task? loop;
        lock (_gate)
        {
            if (!_stopping.IsCancellationRequested)
            {
                _engine.ExecutionChanged -= OnExecutionChanged;
                _stopping.Cancel();
            }

            loop = _loop;
        }

        if (loop is null)
        {
            return true;
        }

        try
        {
            return loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            return true; // The loop ended (it never throws, but a fault is still an end).
        }
    }

    /// <summary>Clean exit: writes every enabled <c>status.json</c> once more (call <see cref="Stop"/> first). Never throws.</summary>
    public void WriteFinalStatus()
    {
        var now = _time.GetUtcNow();
        foreach (var exporter in _status.Values.Where(e => e.IsEnabled))
        {
            try
            {
                exporter.WriteNow(now);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Exiting anyway.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _stopping.Dispose();
    }

    private async Task LoopAsync()
    {
        var token = _stopping.Token;
        Housekeep(_time.GetUtcNow());
        while (!token.IsCancellationRequested)
        {
            Tick(_time.GetUtcNow());
            try
            {
                await Task.Delay(LoopInterval, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs on engine worker threads: must never throw (the engine does not isolate subscribers).</summary>
    private void OnExecutionChanged(object? sender, ExecutionSnapshot execution)
    {
        try
        {
            if (execution.IsActive || !_status.TryGetValue(execution.Profile, out var exporter))
            {
                return;
            }

            exporter.RequestSoon(_time.GetUtcNow());
            if (execution.Status == ExecutionStatus.Failed)
            {
                _outboxes[execution.Profile].ObserveExecution(execution);
            }
            else if (execution.Status == ExecutionStatus.NeedsAttention)
            {
                _outboxes[execution.Profile].ObserveNeedsAttention(execution);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An export problem must never disturb a run.
        }
    }

    private TimeSpan? StepTimeout(ExecutionSnapshot run, StepSnapshot step)
    {
        var definition = _workflows()
            .FirstOrDefault(w => w.Profile.Id == run.Profile)?.Workflows
            .FirstOrDefault(w => w.Id == run.WorkflowId)?
            .FindStep(step.Id);
        if (definition?.Timeout is { } own)
        {
            return own;
        }

        return _registry.TryGet(step.ActionId, out var action) ? action.DefaultTimeout : null;
    }

    /// <summary>
    /// Settings hears about every new state (so "last written" moves on), but the log gets one line per transition: a
    /// change of outcome or folder state, or a new failure message. Routine successful writes are not logged.
    /// </summary>
    private void OnStatusChanged(ProfileId profile, StatusExportState state)
    {
        var key = (state.Outcome, state.Folder, state.Outcome == ExportOutcome.Written ? null : state.Message);
        bool transition;
        lock (_logGate)
        {
            transition = !_lastLoggedStatus.TryGetValue(profile, out var last) || last != key;
            _lastLoggedStatus[profile] = key;
        }

        if (transition)
        {
            Log(profile, $"status.json: {state.Outcome}: {state.Message}");
        }

        RaiseChanged(profile);
    }

    private void OnStateChanged(ProfileId profile, string line)
    {
        Log(profile, line);
        RaiseChanged(profile);
    }

    private void RaiseChanged(ProfileId profile)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ProfileId>>())
        {
            try
            {
                handler(this, profile);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A view failing is not an export failure.
            }
        }
    }

    /// <summary>One line per state change (not per write), so a missing folder is logged once, not every 15 minutes.</summary>
    private void Log(ProfileId profile, string line)
    {
        try
        {
            lock (_logGate)
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                var stamp = _time.GetUtcNow().ToString("u", CultureInfo.InvariantCulture);
                File.AppendAllText(Path.Combine(_paths.LogsDirectory, LogFileName), $"{stamp} [{profile.DisplayName()}] {ExportText.SingleLine(OutputRedactor.Redact(line, 1000) ?? string.Empty)}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort.
        }
    }
}
