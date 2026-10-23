using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Desktop.Dashboard;

/// <summary>
/// The spec §14 dashboard: six tiles from the active profile, the active run with live step progress and a
/// Cancel button, and recent runs. Engine events arrive on worker threads and are marshalled to the dispatcher.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    /// <summary>Tiles always shown, so positions stay stable (spec §14). Missing workflows become placeholders.</summary>
    public const int TileCount = 6;

    private const int RecentRunLimit = 8;

    private readonly CoreHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly TaskCompletionSource _firstBind = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProfileId _profile = ProfileId.Work;

    public DashboardViewModel(CoreHost host, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _host = host;
        _dispatcher = dispatcher;
        _host.Engine.ExecutionChanged += OnExecutionChanged;
    }

    /// <summary>Exactly <see cref="TileCount"/> tiles for the active profile.</summary>
    public ObservableCollection<WorkflowTile> Tiles { get; } = [];

    /// <summary>Newest first, scoped to the active profile.</summary>
    public ObservableCollection<RunSummary> RecentRuns { get; } = [];

    /// <summary>Workflow files in the profile folder that did not validate, one line each.</summary>
    public ObservableCollection<string> LoadErrors { get; } = [];

    /// <summary>Steps of <see cref="ActiveRun"/>, in order.</summary>
    public ObservableCollection<StepRow> ActiveSteps { get; } = [];

    /// <summary>Completes once the first profile has been bound and the dispatcher has processed layout (used by <c>--smoke-ui</c>).</summary>
    public Task FirstBindCompleted => _firstBind.Task;

    /// <summary>The run shown in the Active execution panel, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveRun), nameof(ActiveTitle), nameof(ActiveProgress))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial ExecutionSnapshot? ActiveRun { get; set; }

    /// <summary>One line of feedback under the tiles (why a tile did nothing, what just finished).</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileName { get; set; } = ProfileId.Work.DisplayName();

    [ObservableProperty]
    public partial bool HasLoadErrors { get; set; }

    public bool HasActiveRun => ActiveRun is not null;

    public string ActiveTitle => ActiveRun?.WorkflowName ?? string.Empty;

    public string ActiveProgress => ActiveRun is null
        ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{StatusText(ActiveRun.Status)} · {ActiveRun.SettledStepCount} of {ActiveRun.Steps.Count} steps");

    /// <summary>Binds the dashboard to <paramref name="profile"/>: tiles, errors, recent runs, active run.</summary>
    public void Load(ProfileId profile)
    {
        _profile = profile;
        ProfileName = profile.DisplayName();

        var loaded = _host.GetWorkflows(profile);
        Tiles.Clear();
        foreach (var workflow in loaded.Workflows.Take(TileCount))
        {
            Tiles.Add(WorkflowTile.For(workflow));
        }

        while (Tiles.Count < TileCount)
        {
            Tiles.Add(WorkflowTile.Placeholder(Tiles.Count, loaded.Profile.WorkflowsDirectory));
        }

        LoadErrors.Clear();
        foreach (var error in loaded.Errors)
        {
            LoadErrors.Add(error.ToString());
        }

        HasLoadErrors = LoadErrors.Count > 0;
        StatusMessage = loaded.Workflows.Count > TileCount
            ? string.Create(CultureInfo.CurrentCulture, $"Showing the first {TileCount} of {loaded.Workflows.Count} workflows.")
            : string.Empty;

        SetActiveRun(_host.Engine.ActiveExecutions.LastOrDefault(e => e.Profile == profile));
        RefreshRecentRuns();

        if (!_firstBind.Task.IsCompleted)
        {
            _ = _dispatcher.InvokeAsync(() => _firstBind.TrySetResult(), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Re-reads recent runs for the active profile from history.</summary>
    public void RefreshRecentRuns()
    {
        RecentRuns.Clear();
        foreach (var execution in _host.History.GetRecentExecutions(_profile, RecentRunLimit))
        {
            RecentRuns.Add(RunSummary.From(execution));
        }
    }

    /// <summary>Tile click: start the workflow, or show the run that is already active (focus-existing).</summary>
    [RelayCommand]
    private void Run(WorkflowTile? tile)
    {
        if (tile is null || tile.IsPlaceholder)
        {
            return;
        }

        try
        {
            var started = _host.Run(_profile, tile.WorkflowId);
            SetActiveRun(started.Execution);
            StatusMessage = started.WasAlreadyRunning
                ? $"{tile.Name} is already running; showing that run."
                : $"Queued {tile.Name}.";
        }
        catch (ActionNotAllowedException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (KeyNotFoundException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (ObjectDisposedException)
        {
            StatusMessage = "The app is shutting down.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveRun))]
    private void Cancel()
    {
        if (ActiveRun is { } run && _host.Engine.Cancel(run.Id))
        {
            StatusMessage = $"Cancelling {run.WorkflowName}…";
        }
    }

    private void OnExecutionChanged(object? sender, ExecutionSnapshot snapshot)
    {
        if (_dispatcher.HasShutdownStarted || snapshot.Profile != _profile)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ApplyChange(snapshot));
    }

    private void ApplyChange(ExecutionSnapshot snapshot)
    {
        if (snapshot.Profile != _profile)
        {
            return;
        }

        if (ActiveRun is null || ActiveRun.Id == snapshot.Id)
        {
            if (snapshot.IsActive)
            {
                SetActiveRun(snapshot);
            }
            else if (ActiveRun?.Id == snapshot.Id)
            {
                SetActiveRun(snapshot);
                StatusMessage = snapshot.Error is null
                    ? $"{snapshot.WorkflowName}: {StatusText(snapshot.Status)}."
                    : $"{snapshot.WorkflowName}: {StatusText(snapshot.Status)}. {snapshot.Error}";
                RefreshRecentRuns();

                // Keep showing the finished run until another one starts, unless one is already active.
                var next = _host.Engine.ActiveExecutions.LastOrDefault(e => e.Profile == _profile);
                if (next is not null)
                {
                    SetActiveRun(next);
                }
            }
        }

        if (!snapshot.IsActive)
        {
            RefreshRecentRuns();
        }
    }

    private void SetActiveRun(ExecutionSnapshot? snapshot)
    {
        ActiveRun = snapshot;
        ActiveSteps.Clear();
        if (snapshot is null)
        {
            return;
        }

        foreach (var step in snapshot.Steps)
        {
            ActiveSteps.Add(StepRow.From(step));
        }
    }

    internal static string StatusText(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Queued => "Queued",
        ExecutionStatus.Running => "Running",
        ExecutionStatus.Completed => "Completed",
        ExecutionStatus.Failed => "Failed",
        ExecutionStatus.Cancelling => "Cancelling",
        ExecutionStatus.Cancelled => "Cancelled",
        ExecutionStatus.Interrupted => "Interrupted",
        ExecutionStatus.NeedsAttention => "Needs attention",
        _ => status.ToString(),
    };
}

/// <summary>One dashboard tile: a workflow, or a disabled placeholder that keeps the grid stable.</summary>
public sealed record WorkflowTile(string WorkflowId, string Name, string Description, bool IsPlaceholder)
{
    public bool IsEnabled => !IsPlaceholder;

    /// <summary>Screen-reader name.</summary>
    public string AutomationName => IsPlaceholder ? Name : $"Run {Name}";

    public static WorkflowTile For(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return new WorkflowTile(workflow.Id, workflow.Name, workflow.Description ?? string.Empty, IsPlaceholder: false);
    }

    public static WorkflowTile Placeholder(int index, string workflowsDirectory) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"<empty-{index}>"),
            "Empty slot",
            $"Add a workflow file to {workflowsDirectory}",
            IsPlaceholder: true);
}

/// <summary>One line in the Active execution panel.</summary>
public sealed record StepRow(string Glyph, string Text, string Status)
{
    public static StepRow From(StepSnapshot step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var glyph = step.Status switch
        {
            StepStatus.Completed => "✓",
            StepStatus.Failed => "✗",
            StepStatus.Running => "…",
            StepStatus.Pending => "·",
            StepStatus.Skipped => "–",
            StepStatus.Cancelled => "⊘",
            StepStatus.Interrupted => "!",
            _ => "?",
        };
        var text = step.Status is StepStatus.Pending or StepStatus.Running || string.IsNullOrEmpty(step.Message)
            ? step.Name
            : $"{step.Name}: {step.Message}";
        return new StepRow(glyph, text, step.Status.ToString());
    }
}

/// <summary>One line in Recent runs / History.</summary>
public sealed record RunSummary(Guid Id, string WorkflowName, string Status, string When, string Summary)
{
    public static RunSummary From(ExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var when = (execution.StartedAt ?? execution.QueuedAt).ToLocalTime();
        var summary = execution.Error
            ?? string.Create(CultureInfo.CurrentCulture, $"{execution.CompletedStepCount} of {execution.Steps.Count} steps completed");
        return new RunSummary(
            execution.Id,
            execution.WorkflowName,
            DashboardViewModel.StatusText(execution.Status),
            when.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture),
            summary);
    }
}
