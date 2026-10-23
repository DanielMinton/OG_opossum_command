using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Scheduling;

namespace OG_opossum_command.Core.Notifications;

/// <summary>How important a notification is. Quiet mode suppresses everything below <see cref="Error"/>.</summary>
public enum NotificationLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>One notification that reached (or would have reached) the user.</summary>
public sealed record Notification(NotificationLevel Level, string Title, string Message);

/// <summary>Where notifications go: the tray balloon in the app, a recording fake in tests and smoke modes (rule 7).</summary>
public interface INotificationSink
{
    void Show(Notification notification);
}

/// <summary>Records instead of showing.</summary>
public sealed class RecordingNotificationSink : INotificationSink
{
    private readonly List<Notification> _shown = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<Notification> Shown
    {
        get
        {
            lock (_gate)
            {
                return _shown.ToArray();
            }
        }
    }

    public void Show(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            _shown.Add(notification);
        }
    }
}

/// <summary>
/// Turns engine and scheduler events into notifications and applies quiet mode (spec §14, ADR-037): with quiet mode
/// on, only Error-level notifications (a failed run, a run that needs attention) get through.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly IHistoryStore _settings;
    private readonly INotificationSink _sink;
    private readonly WorkflowEngine _engine;
    private readonly Scheduler? _scheduler;
    private readonly Lock _gate = new();
    private int _suppressed;
    private bool _disposed;

    public NotificationService(IHistoryStore settings, INotificationSink sink, WorkflowEngine engine, Scheduler? scheduler)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(engine);

        _settings = settings;
        _sink = sink;
        _engine = engine;
        _scheduler = scheduler;
        _engine.ExecutionChanged += OnExecutionChanged;
        if (_scheduler is not null)
        {
            _scheduler.Fired += OnSchedulerFired;
        }
    }

    /// <summary>Persisted quiet-mode switch.</summary>
    public bool QuietMode
    {
        get => string.Equals(_settings.GetSetting(SettingKeys.QuietMode), "true", StringComparison.OrdinalIgnoreCase);
        set => _settings.SetSetting(SettingKeys.QuietMode, value ? "true" : "false");
    }

    /// <summary>How many notifications quiet mode swallowed since start.</summary>
    public int SuppressedCount => Volatile.Read(ref _suppressed);

    /// <summary>Shows <paramref name="notification"/> unless quiet mode suppresses it. Returns whether it was shown.</summary>
    public bool Notify(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Level < NotificationLevel.Error && QuietMode)
        {
            Interlocked.Increment(ref _suppressed);
            return false;
        }

        lock (_gate)
        {
            _sink.Show(notification);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.ExecutionChanged -= OnExecutionChanged;
        if (_scheduler is not null)
        {
            _scheduler.Fired -= OnSchedulerFired;
        }
    }

    private void OnExecutionChanged(object? sender, ExecutionSnapshot snapshot)
    {
        if (snapshot.IsActive || snapshot.Status == ExecutionStatus.Interrupted)
        {
            return;
        }

        var notification = snapshot.Status switch
        {
            ExecutionStatus.Completed => new Notification(NotificationLevel.Info, snapshot.WorkflowName, $"Completed: {snapshot.CompletedStepCount} of {snapshot.Steps.Count} steps."),
            ExecutionStatus.Cancelled => new Notification(NotificationLevel.Warning, snapshot.WorkflowName, "Cancelled."),
            ExecutionStatus.NeedsAttention => new Notification(NotificationLevel.Error, $"{snapshot.WorkflowName} needs attention", snapshot.Error ?? "Open the dashboard to resume or dismiss it."),
            _ => new Notification(NotificationLevel.Error, $"{snapshot.WorkflowName} failed", snapshot.Error ?? "See History for details."),
        };
        Notify(notification);
    }

    private void OnSchedulerFired(object? sender, SchedulerEvent fired)
    {
        var level = fired.Kind switch
        {
            SchedulerEventKinds.Refused => NotificationLevel.Error,
            SchedulerEventKinds.Suppressed or SchedulerEventKinds.Skipped or SchedulerEventKinds.Paused => NotificationLevel.Warning,
            _ => NotificationLevel.Info,
        };
        Notify(new Notification(level, "Schedule", fired.Message));
    }
}
