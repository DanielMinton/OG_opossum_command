using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Desktop.Dashboard;

/// <summary>
/// The spec §14 dashboard: six tiles from the active profile, a compact Today panel from the profile's calendar
/// file (ADR-039), the active run with live step progress and a Cancel button, and recent runs. Engine and
/// calendar events arrive on worker threads and are marshalled to the dispatcher.
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
        _host.Calendar.Changed += OnCalendarChanged;
    }

    /// <summary>The active profile's remaining events today (all-day first), from the latest calendar read.</summary>
    public ObservableCollection<TodayEvent> Today { get; } = [];

    /// <summary>One line under the Today heading: when the file was updated, or why there is nothing to show.</summary>
    [ObservableProperty]
    public partial string TodayStatus { get; set; } = "Calendar not read yet.";

    [ObservableProperty]
    public partial bool HasTodayEvents { get; set; }

    /// <summary>Exactly <see cref="TileCount"/> tiles for the active profile.</summary>
    public ObservableCollection<WorkflowTile> Tiles { get; } = [];

    /// <summary>Newest first, scoped to the active profile.</summary>
    public ObservableCollection<RunSummary> RecentRuns { get; } = [];

    /// <summary>Workflow files in the profile folder that did not validate, one line each.</summary>
    public ObservableCollection<string> LoadErrors { get; } = [];

    /// <summary>Steps of <see cref="ActiveRun"/>, in order.</summary>
    public ObservableCollection<StepRow> ActiveSteps { get; } = [];

    /// <summary>Interrupted runs waiting for Resume or Dismiss (spec §13, ADR-035), newest first, across both profiles.</summary>
    public ObservableCollection<AttentionItem> Attention { get; } = [];

    /// <summary>One line about the next schedule occurrence in the active profile; empty when none.</summary>
    [ObservableProperty]
    public partial string NextScheduled { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasAttention { get; set; }

    /// <summary>Completes once the first profile has been bound and the dispatcher has processed layout (used by <c>--smoke-ui</c>).</summary>
    public Task FirstBindCompleted => _firstBind.Task;

    /// <summary>The run shown in the Active execution panel, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveRun), nameof(CanCancel), nameof(ActiveTitle), nameof(ActiveProgress), nameof(ActiveHeadline))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial ExecutionSnapshot? ActiveRun { get; set; }

    /// <summary>One line of feedback under the tiles (why a tile did nothing, what just finished).</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLoadErrors { get; set; }

    public bool HasActiveRun => ActiveRun is not null;

    /// <summary>Cancel is only offered while the shown run can still be stopped; a finished run stays visible with Cancel disabled.</summary>
    public bool CanCancel => ActiveRun?.IsActive == true;

    public string ActiveTitle => ActiveRun?.WorkflowName ?? string.Empty;

    public string ActiveProgress => ActiveRun is null
        ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{StatusText(ActiveRun.Status)} · {ActiveRun.SettledStepCount} of {ActiveRun.Steps.Count} steps");

    /// <summary>Title and progress as one string, so a live-region change is a single announcement.</summary>
    public string ActiveHeadline => ActiveRun is null ? string.Empty : $"{ActiveTitle}  ▸  {ActiveProgress}";

    /// <summary>Binds the dashboard to <paramref name="profile"/>: tiles, errors, recent runs, active run.</summary>
    public void Load(ProfileId profile)
    {
        _profile = profile;

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
        RefreshAttention();
        RefreshNextScheduled();

        var latest = _host.Calendar.GetLatest(profile);
        ApplyCalendar(latest);
        if (latest is null)
        {
            _ = RefreshCalendarAsync();
        }

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

    /// <summary>Re-reads the runs that need attention.</summary>
    public void RefreshAttention()
    {
        Attention.Clear();
        foreach (var execution in _host.NeedsAttention)
        {
            Attention.Add(AttentionItem.From(execution));
        }

        HasAttention = Attention.Count > 0;
    }

    /// <summary>Re-reads the next schedule occurrence for the active profile.</summary>
    public void RefreshNextScheduled()
    {
        var next = _host.Scheduler.Triggers
            .Where(t => t.Profile == _profile && t.Enabled && t.NextDueAt is not null)
            .OrderBy(t => t.NextDueAt)
            .FirstOrDefault();
        NextScheduled = next is null
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"Next scheduled: {next.WorkflowId} at {next.NextDueAt!.Value.ToLocalTime():ddd d MMM HH:mm}{(_host.Scheduler.IsPaused ? " (schedules paused)" : string.Empty)}");
    }

    /// <summary>
    /// Re-reads the active profile's calendar file off the dispatcher. The result arrives through
    /// <see cref="CalendarService.Changed"/>; a bad or missing file only changes the status line, never throws.
    /// </summary>
    [RelayCommand]
    private async Task RefreshCalendarAsync()
    {
        var profile = _host.Profiles.Get(_profile);
        TodayStatus = "Reading calendar…";
        try
        {
            await Task.Run(() => _host.Calendar.ReadAsync(profile)).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>Opens an event's https link (Teams join or Outlook page) through the injectable opener, so smoke runs only record it.</summary>
    [RelayCommand]
    private void OpenEvent(TodayEvent? item)
    {
        if (item?.Link is null)
        {
            return;
        }

        try
        {
            _host.Launchers.Urls.Open(item.Link);
            StatusMessage = $"Opened {item.Subject}.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            // No browser registered for https, or the shell refused: say so instead of taking the tray app down.
            StatusMessage = $"Could not open the link for {item.Subject}: {ex.Message}";
        }
    }

    private void OnCalendarChanged(object? sender, CalendarReadResult result)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (result.Profile == _profile)
            {
                ApplyCalendar(result);
            }
        });
    }

    private void ApplyCalendar(CalendarReadResult? result)
    {
        Today.Clear();
        if (result?.Snapshot is { } snapshot)
        {
            var now = DateTimeOffset.Now;
            foreach (var item in snapshot.UpcomingOn(now))
            {
                Today.Add(TodayEvent.From(item, now));
            }
        }

        HasTodayEvents = Today.Count > 0;
        TodayStatus = result switch
        {
            null => "Calendar not read yet.",
            { Status: CalendarStatus.Loaded } when Today.Count == 0 => $"Nothing else today. {result.Message}",
            { Status: CalendarStatus.Unavailable or CalendarStatus.Invalid } => result.Message.StartsWith("Calendar unavailable", StringComparison.Ordinal)
                ? result.Message
                : $"Calendar unavailable. {result.Message}",
            _ => result.Message,
        };
    }

    /// <summary>Sets the feedback line (used by the command palette).</summary>
    public void ShowMessage(string message) => StatusMessage = message ?? string.Empty;

    /// <summary>Resume an interrupted run: a new run that skips the steps already completed (ADR-035).</summary>
    [RelayCommand]
    private void Resume(AttentionItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var started = _host.Resume(item.Id);
            SetActiveRun(started.Execution);
            StatusMessage = $"Resumed {item.WorkflowName} as a new run.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (KeyNotFoundException ex)
        {
            StatusMessage = ex.Message;
        }

        RefreshAttention();
        RefreshRecentRuns();
    }

    /// <summary>Dismiss an interrupted run: nothing is re-run.</summary>
    [RelayCommand]
    private void Dismiss(AttentionItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            _host.Dismiss(item.Id);
            StatusMessage = $"Dismissed {item.WorkflowName}; nothing was re-run.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (KeyNotFoundException ex)
        {
            StatusMessage = ex.Message;
        }

        RefreshAttention();
        RefreshRecentRuns();
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (ActiveRun is { } run && _host.Engine.Cancel(run.Id))
        {
            StatusMessage = $"Cancelling {run.WorkflowName}…";
        }
    }

    private void OnExecutionChanged(object? sender, ExecutionSnapshot snapshot)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ApplyChange(snapshot));
    }

    private void ApplyChange(ExecutionSnapshot snapshot)
    {
        if (!snapshot.IsActive)
        {
            // Any finished run may have changed the attention list (recovery) or fired a schedule.
            RefreshAttention();
            RefreshNextScheduled();
        }

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
    public string AutomationName => IsPlaceholder ? $"{Name}. {Description}" : $"Run {Name}";

    public override string ToString() => AutomationName;

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

/// <summary>One row in the Today panel. Times are shown in local time.</summary>
public sealed record TodayEvent(string Time, string Subject, string Detail, Uri? Link, bool IsNow)
{
    public bool HasLink => Link is not null;

    /// <summary>Screen-reader name.</summary>
    public string AutomationName => $"{Time}, {Subject}{(IsNow ? ", in progress" : string.Empty)}{(Detail.Length > 0 ? $", {Detail}" : string.Empty)}";

    public override string ToString() => AutomationName;

    public static TodayEvent From(CalendarEvent item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        var time = item.IsAllDay
            ? "All day"
            : string.Create(CultureInfo.CurrentCulture, $"{item.Start.ToLocalTime():HH:mm}–{item.End.ToLocalTime():HH:mm}");
        var detail = item.ShowAs is "tentative" or "free" or "oof" or "workingelsewhere"
            ? string.Join(" · ", new[] { item.Location, item.ShowAs }.Where(s => !string.IsNullOrEmpty(s)))
            : item.Location ?? string.Empty;
        return new TodayEvent(time, item.Subject, detail, item.Link, item.IsInProgressAt(now));
    }
}

/// <summary>One run waiting for Resume / Dismiss.</summary>
public sealed record AttentionItem(Guid Id, string WorkflowName, string Profile, string When, string Explanation)
{
    /// <summary>Screen-reader name.</summary>
    public string AutomationName => $"{WorkflowName} ({Profile}) needs attention: {Explanation}";

    public override string ToString() => AutomationName;

    public static AttentionItem From(ExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var when = (execution.StartedAt ?? execution.QueuedAt).ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
        return new AttentionItem(execution.Id, execution.WorkflowName, execution.Profile.DisplayName(), when, execution.Error ?? "The app stopped while this run was active.");
    }
}

/// <summary>One line in the Active execution panel.</summary>
public sealed record StepRow(string Glyph, string Text, string Status)
{
    /// <summary>Screen-reader name: the step text and its status.</summary>
    public string AutomationName => $"{Text}, {Status}";

    public override string ToString() => AutomationName;

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
    /// <summary>Screen-reader name.</summary>
    public string AutomationName => $"{WorkflowName}, {Status}, {When}, {Summary}";

    public override string ToString() => AutomationName;

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
