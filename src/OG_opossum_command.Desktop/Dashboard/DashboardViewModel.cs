using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.Feedback;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;
using OG_opossum_command.Desktop.Feedback;

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

    private readonly Dictionary<string, DispatcherTimer> _flashTimers = [];

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

    /// <summary>
    /// "Carryovers from yesterday": today's items from the profile's <c>tomorrow.json</c> (ADR-051), read on load and on
    /// Refresh. Display only: nothing in an item is opened or followed.
    /// </summary>
    public ObservableCollection<CarryoverRow> Carryovers { get; } = [];

    /// <summary>One line under the Carryovers heading: how many items, or a gentle note why there are none.</summary>
    [ObservableProperty]
    public partial string CarryoverNote { get; set; } = "Carryovers not read yet.";

    [ObservableProperty]
    public partial bool HasCarryovers { get; set; }

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
    [NotifyPropertyChangedFor(nameof(HasActiveRun), nameof(CanCancel), nameof(ActiveTitle), nameof(ActiveProgress), nameof(ActiveHeadline), nameof(ActiveFraction))]
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
            Tiles.Add(WorkflowTile.For(workflow, Tiles.Count));
        }

        foreach (var running in _host.Engine.ActiveExecutions.Where(e => e.Profile == profile))
        {
            SetTileState(running.WorkflowId, TileState.Running);
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
        else
        {
            // The calendar was already read for this profile; carryovers are re-read on every load (forDate may have moved on).
            _ = RefreshCarryoversAsync();
        }

        if (!_firstBind.Task.IsCompleted)
        {
            _ = _dispatcher.InvokeAsync(() => _firstBind.TrySetResult(), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Re-reads recent runs and today's numbers for the active profile from history.</summary>
    public void RefreshRecentRuns()
    {
        // Rows are updated in place where they match, so an unchanged list does not replay its entrance.
        var rows = _host.History.GetRecentExecutions(_profile, RecentRunLimit).Select(RunSummary.From).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < RecentRuns.Count)
            {
                if (RecentRuns[i] != rows[i])
                {
                    RecentRuns[i] = rows[i];
                }
            }
            else
            {
                RecentRuns.Add(rows[i]);
            }
        }

        while (RecentRuns.Count > rows.Count)
        {
            RecentRuns.RemoveAt(RecentRuns.Count - 1);
        }

        RefreshTodayCounts();
    }

    /// <summary>Today's headline numbers: a count query, no steps read.</summary>
    public void RefreshTodayCounts()
    {
        var counts = _host.History.CountFinishedSince(_profile, new DateTimeOffset(DateTime.Today));
        RunsToday = counts.Finished;
        SuccessRate = counts.Finished == 0 ? double.NaN : Math.Round(100.0 * counts.Completed / counts.Finished);
    }

    /// <summary>Finished runs today in the active profile (the first headline number).</summary>
    [ObservableProperty]
    public partial double RunsToday { get; set; }

    /// <summary>Percent of today's finished runs that completed; NaN (shown as a dash) when none finished.</summary>
    [ObservableProperty]
    public partial double SuccessRate { get; set; } = double.NaN;

    /// <summary>Runs waiting for Resume or Dismiss, as a number for the headline row.</summary>
    [ObservableProperty]
    public partial double AttentionCount { get; set; }

    /// <summary>The next scheduled workflow's name, or a dash.</summary>
    [ObservableProperty]
    public partial string NextUpName { get; set; } = "—";

    /// <summary>When the next scheduled workflow runs, or why nothing is scheduled.</summary>
    [ObservableProperty]
    public partial string NextUpWhen { get; set; } = "Nothing scheduled";

    /// <summary>Settled steps of the shown run as a fraction, for the progress bar.</summary>
    public double ActiveFraction => ActiveRun is { Steps.Count: > 0 } run ? (double)run.SettledStepCount / run.Steps.Count : 0;

    /// <summary>Ctrl+1..6: runs the tile at that position (1-based, as printed on the card).</summary>
    [RelayCommand]
    private void RunAt(string? position)
    {
        if (int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Tiles.Count)
        {
            if (Tiles[n - 1].IsPlaceholder)
            {
                StatusMessage = string.Create(CultureInfo.CurrentCulture, $"Slot {n} is empty. {Tiles[n - 1].Description}.");
                UiFeedback.Play(SoundCue.Tap);
                return;
            }

            Run(Tiles[n - 1]);
        }
    }

    private void SetTileState(string workflowId, TileState state)
    {
        foreach (var tile in Tiles.Where(t => !t.IsPlaceholder && t.WorkflowId == workflowId))
        {
            tile.State = state;
        }
    }

    /// <summary>Shows Done or Failed on the tile for a few seconds, then returns it to idle (unless it started again).</summary>
    private void FlashTileResult(string workflowId, TileState state)
    {
        SetTileState(workflowId, state);

        // One timer per workflow, restarted by each result, so a second result is shown for its full 3 seconds.
        if (!_flashTimers.TryGetValue(workflowId, out var timer))
        {
            timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                foreach (var tile in Tiles.Where(t => t.WorkflowId == workflowId && t.State is TileState.Succeeded or TileState.Failed))
                {
                    tile.State = TileState.Idle;
                }
            }, _dispatcher);
            _flashTimers[workflowId] = timer;
        }

        timer.Stop();
        timer.Start();
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
        AttentionCount = Attention.Count;
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
        if (next is null)
        {
            NextUpName = "—";
            NextUpWhen = "Nothing scheduled";
        }
        else
        {
            var due = next.NextDueAt!.Value.ToLocalTime();
            NextUpName = Tiles.FirstOrDefault(t => t.WorkflowId == next.WorkflowId)?.Name ?? next.WorkflowId;
            NextUpWhen = (due.Date == DateTime.Today
                ? due.ToString("'Today' HH:mm", CultureInfo.CurrentCulture)
                : due.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture)) + (_host.Scheduler.IsPaused ? " · paused" : string.Empty);
        }
    }

    /// <summary>
    /// Re-reads the active profile's calendar file and carryovers file off the dispatcher (the Today panel's Refresh).
    /// The calendar result arrives through <see cref="CalendarService.Changed"/>; a bad or missing file only changes a
    /// status line, never throws.
    /// </summary>
    [RelayCommand]
    private async Task RefreshCalendarAsync()
    {
        var profile = _host.Profiles.Get(_profile);
        TodayStatus = "Reading calendar…";
        var carryovers = RefreshCarryoversAsync();
        try
        {
            await Task.Run(() => _host.Calendar.ReadAsync(profile)).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }

        await carryovers.ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the active profile's <c>tomorrow.json</c> on the thread pool and shows the result. The reader is tolerant
    /// (missing file = none, bad file = a readable note), so this never throws into the UI.
    /// </summary>
    public async Task RefreshCarryoversAsync()
    {
        var profile = _profile;
        CarryoverNote = "Reading carryovers…";
        try
        {
            var result = await _host.Exports.Carryovers.ReadAsync(_host.Exports.GetProfile(profile)).ConfigureAwait(true);
            if (profile == _profile)
            {
                ApplyCarryovers(result);
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void ApplyCarryovers(CarryoverReadResult result)
    {
        Carryovers.Clear();
        if (result.Status == CarryoverStatus.Loaded)
        {
            foreach (var item in result.Items)
            {
                Carryovers.Add(CarryoverRow.From(item));
            }
        }

        HasCarryovers = Carryovers.Count > 0;
        CarryoverNote = result.Message;
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
            UiFeedback.Play(started.WasAlreadyRunning ? SoundCue.Tap : SoundCue.Start);
            if (started.Execution.IsActive)
            {
                tile.State = TileState.Running;
            }
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

        if (snapshot.IsActive)
        {
            SetTileState(snapshot.WorkflowId, TileState.Running);
        }
        else
        {
            var ok = snapshot.Status == ExecutionStatus.Completed;
            var bad = snapshot.Status is ExecutionStatus.Failed or ExecutionStatus.NeedsAttention or ExecutionStatus.Interrupted;
            FlashTileResult(snapshot.WorkflowId, ok ? TileState.Succeeded : bad ? TileState.Failed : TileState.Idle);
            if (ok || bad)
            {
                UiFeedback.Play(ok ? SoundCue.Success : SoundCue.Failure);
            }
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

/// <summary>What a tile shows on its badge (ADR-057).</summary>
public enum TileState
{
    Idle,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// One dashboard tile: a workflow, or a disabled placeholder that keeps the grid stable. <see cref="State"/> follows
/// the tile's latest run: Running while active, then Succeeded or Failed for a few seconds, then Idle again.
/// </summary>
public sealed partial class WorkflowTile : ObservableObject
{
    private WorkflowTile(string workflowId, string name, string description, bool isPlaceholder, int index)
    {
        WorkflowId = workflowId;
        Name = name;
        Description = description;
        IsPlaceholder = isPlaceholder;
        Shortcut = string.Create(CultureInfo.InvariantCulture, $"Ctrl+{index + 1}");
    }

    public string WorkflowId { get; }

    public string Name { get; }

    public string Description { get; }

    public bool IsPlaceholder { get; }

    public bool IsEnabled => !IsPlaceholder;

    /// <summary>The keyboard shortcut that runs this tile, shown on the card.</summary>
    public string Shortcut { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(StateText))]
    public partial TileState State { get; set; }

    /// <summary>A short word under the name while the tile is not idle.</summary>
    public string StateText => State switch
    {
        TileState.Running => "Running",
        TileState.Succeeded => "Done",
        TileState.Failed => "Failed",
        _ => string.Empty,
    };

    /// <summary>Screen-reader name.</summary>
    public string AutomationName => IsPlaceholder
        ? $"{Name}. {Description}"
        : State == TileState.Idle ? $"Run {Name}, {Shortcut}" : $"Run {Name}, {StateText}, {Shortcut}";

    public override string ToString() => AutomationName;

    public static WorkflowTile For(WorkflowDefinition workflow, int index)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return new WorkflowTile(workflow.Id, workflow.Name, workflow.Description ?? string.Empty, isPlaceholder: false, index);
    }

    public static WorkflowTile Placeholder(int index, string workflowsDirectory) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"<empty-{index}>"),
            "Empty slot",
            $"Add a workflow file to {workflowsDirectory}",
            isPlaceholder: true,
            index);
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

/// <summary>One "Carryovers from yesterday" row. Text only: the app never opens anything from <c>tomorrow.json</c>.</summary>
public sealed record CarryoverRow(string Title, string Detail)
{
    /// <summary>Screen-reader name.</summary>
    public string AutomationName => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;

    public override string ToString() => AutomationName;

    public static CarryoverRow From(CarryoverItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var source = item.Source switch
        {
            CarryoverSource.Todo => "To Do",
            CarryoverSource.Email => "Email",
            CarryoverSource.Teams => "Teams",
            _ => null,
        };
        var due = item.Due is { } date ? $"due {date.ToString("ddd d MMM", CultureInfo.CurrentCulture)}" : null;
        var detail = string.Join(" · ", new[] { source, due, item.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new CarryoverRow(item.Title, detail);
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
    /// <summary><c>ok</c>, <c>bad</c>, <c>active</c> or <c>idle</c>: picks the step dot's colour.</summary>
    public string Kind => Status switch
    {
        nameof(StepStatus.Completed) => "ok",
        nameof(StepStatus.Failed) or nameof(StepStatus.Interrupted) => "bad",
        nameof(StepStatus.Running) => "active",
        _ => "idle",
    };

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
    /// <summary><c>ok</c>, <c>bad</c>, <c>warn</c>, <c>active</c> or <c>idle</c>: picks the status chip's colour.</summary>
    public string Kind => Status switch
    {
        "Completed" => "ok",
        "Failed" => "bad",
        "Needs attention" or "Interrupted" => "warn",
        "Queued" or "Running" or "Cancelling" => "active",
        _ => "idle",
    };

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
