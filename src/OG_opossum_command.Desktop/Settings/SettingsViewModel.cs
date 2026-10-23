using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.Feedback;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Notifications;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Desktop.CommandPalette;
using OG_opossum_command.Desktop.Feedback;

namespace OG_opossum_command.Desktop.Settings;

/// <summary>One schedule row shown read-only in Settings.</summary>
public sealed record ScheduleRow(string Workflow, string Profile, string Schedule, string NextDue, string LastOutcome)
{
    /// <summary>Screen-reader name.</summary>
    public string AutomationName => $"{Workflow} ({Profile}), next {NextDue}, last outcome {LastOutcome}";

    public override string ToString() => AutomationName;

    public static ScheduleRow From(TriggerRecord trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return new ScheduleRow(
            trigger.WorkflowId,
            trigger.Profile.ToString(),
            trigger.ConfigJson,
            trigger.NextDueAt is null ? "—" : trigger.NextDueAt.Value.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture),
            trigger.LastOutcome ?? string.Empty);
    }
}

/// <summary>
/// One profile's exports, shown read-only in Settings (ADR-050..052): <c>status.json</c>, the alert outbox, and the
/// carryovers file. Never shows file content.
/// </summary>
public sealed record ExportsRow(string Profile, string Folder, string Status, string Alerts, string Carryovers)
{
    /// <summary>Screen-reader name.</summary>
    public string AutomationName => $"{Profile} exports. {Status} {Alerts} {Carryovers}";

    public override string ToString() => AutomationName;

    public static ExportsRow From(ExportsStatus exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var settings = exports.Settings;
        var folder = settings.Folder is null ? "Folder: none." : $"Folder: {settings.Folder}";
        return new ExportsRow(
            exports.Profile.DisplayName(),
            folder,
            "status.json: " + Describe(settings.Status, settings, exports.Status.Outcome, exports.Status.Folder, exports.Status.Message, exports.Status.LastWrittenAt, "last written", "on; not written yet."),
            "Alert outbox: " + Describe(settings.Alerts, settings, exports.Alerts.Outcome, exports.Alerts.Folder, exports.Alerts.Message, exports.Alerts.LastAlertAt, exports.Alerts.LastEvent is null ? "last alert" : $"last alert ({exports.Alerts.LastEvent})", "on; no alert written yet."),
            "Carryovers: " + DescribeCarryovers(settings, exports.Carryovers));
    }

    private static string Describe(bool enabled, ExportSettings settings, ExportOutcome outcome, ExportFolderState folder, string message, DateTimeOffset? lastAt, string lastLabel, string notYet)
    {
        if (!enabled)
        {
            return $"off. {settings.DisabledReason ?? "Switched off in profile.json."}";
        }

        var last = lastAt is { } at ? $" ({lastLabel} {When(at)})" : string.Empty;
        return outcome switch
        {
            ExportOutcome.NotYet => notYet,
            ExportOutcome.Written when lastAt is { } written => $"{lastLabel} {When(written)}.",
            ExportOutcome.Disabled => $"disabled by policy. {message}",
            ExportOutcome.Skipped when folder == ExportFolderState.FolderMissing => $"folder missing. {message}{last}",
            ExportOutcome.Skipped => $"refused. {message}{last}",
            _ => $"{message}{last}",
        };
    }

    private static string DescribeCarryovers(ExportSettings settings, CarryoverReadResult? result)
    {
        if (!settings.Carryovers)
        {
            return $"off. {settings.DisabledReason ?? "Switched off in profile.json."}";
        }

        return result is null ? "on; not read yet (open the dashboard or press Refresh)." : $"{result.Message} (read {When(result.ReadAt)})";
    }

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>Hotkey, quiet mode, schedule pause, and a read-only view of the schedules and exports (spec §14).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly CoreHost _host;
    private readonly NotificationService _notifications;
    private readonly Func<HotkeyGesture, string> _applyHotkey;
    private readonly Dispatcher _dispatcher;
    private readonly bool _ready;

    /// <param name="applyHotkey">Re-registers the hotkey and returns a one-line result for the user.</param>
    /// <param name="dispatcher">Export state changes arrive on worker threads and are marshalled here.</param>
    public SettingsViewModel(CoreHost host, NotificationService notifications, Func<HotkeyGesture, string> applyHotkey, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(applyHotkey);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _host = host;
        _notifications = notifications;
        _applyHotkey = applyHotkey;
        _dispatcher = dispatcher;
        HotkeyText = host.History.GetSetting(SettingKeys.Hotkey) ?? HotkeyGesture.DefaultText;
        QuietMode = notifications.QuietMode;
        Sounds = FeedbackPolicy.IsOn(host.History.GetSetting(SettingKeys.Sounds));
        Animations = FeedbackPolicy.IsOn(host.History.GetSetting(SettingKeys.Motion));
        SchedulesPaused = host.Scheduler.IsPaused;
        DataRoot = host.Paths.DataRoot;
        HotkeyStatus = string.Empty;
        Refresh();
        host.Exports.Changed += OnExportsChanged;
        Accessibility.Motion.EnabledChanged += () => OnPropertyChanged(nameof(AnimationsNote));
        _ready = true;
    }

    /// <summary>Raised after a setting changed so the shell can refresh its status line.</summary>
    public event Action? Changed;

    public ObservableCollection<ScheduleRow> Schedules { get; } = [];

    /// <summary>One row per profile: status.json, outbox, carryovers (read-only).</summary>
    public ObservableCollection<ExportsRow> Exports { get; } = [];

    [ObservableProperty]
    public partial string HotkeyText { get; set; }

    [ObservableProperty]
    public partial string HotkeyStatus { get; set; }

    [ObservableProperty]
    public partial bool QuietMode { get; set; }

    [ObservableProperty]
    public partial bool SchedulesPaused { get; set; }

    /// <summary>Click and run-result sounds (ADR-057). In quiet mode only failures are heard.</summary>
    [ObservableProperty]
    public partial bool Sounds { get; set; }

    /// <summary>Animations (ADR-057). Windows "Animation effects" off or high contrast on still turns them off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationsNote))]
    public partial bool Animations { get; set; }

    /// <summary>Explains when Windows is holding animations off even though the setting is on.</summary>
    public string AnimationsNote => Animations && !Accessibility.Motion.IsEnabled && !Accessibility.Motion.ForceDisabled
        ? "Windows has animation effects off or high contrast on, so nothing moves. That setting wins."
        : string.Empty;

    partial void OnSoundsChanged(bool value)
    {
        if (_ready)
        {
            var setting = value ? "true" : "false";
            _host.History.SetSetting(SettingKeys.Sounds, setting);
            UiFeedback.UpdateSettings(setting, QuietMode);
            if (value)
            {
                // A preview even in quiet mode, so turning sounds on is never met with silence.
                UiFeedback.Preview(SoundCue.Success);
            }
        }
    }

    partial void OnAnimationsChanged(bool value)
    {
        if (_ready)
        {
            var setting = value ? "true" : "false";
            _host.History.SetSetting(SettingKeys.Motion, setting);
            Accessibility.Motion.ApplySetting(setting);
        }
    }

    [ObservableProperty]
    public partial string DataRoot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSchedules))]
    public partial bool HasSchedules { get; set; }

    public bool HasNoSchedules => !HasSchedules;

    /// <summary>Re-reads the schedule rows.</summary>
    public void Refresh()
    {
        Schedules.Clear();
        foreach (var trigger in _host.Scheduler.Triggers)
        {
            Schedules.Add(ScheduleRow.From(trigger));
        }

        HasSchedules = Schedules.Count > 0;
        RefreshExports();
    }

    /// <summary>Re-reads the export state of both profiles.</summary>
    public void RefreshExports()
    {
        Exports.Clear();
        foreach (var profile in new[] { ProfileId.Work, ProfileId.Personal })
        {
            Exports.Add(ExportsRow.From(_host.Exports.GetStatus(profile)));
        }
    }

    private void OnExportsChanged(object? sender, ProfileId profile)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshExports);
    }

    /// <summary>Sets the status line shown next to the hotkey box (used by the shell on startup).</summary>
    public void ReportHotkey(string status) => HotkeyStatus = status;

    partial void OnQuietModeChanged(bool value)
    {
        if (_ready)
        {
            _notifications.QuietMode = value;
            UiFeedback.UpdateSettings(Sounds ? "true" : "false", value);
            Changed?.Invoke();
        }
    }

    partial void OnSchedulesPausedChanged(bool value)
    {
        if (_ready)
        {
            _host.Scheduler.IsPaused = value;
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    private void ApplyHotkey()
    {
        if (!HotkeyGesture.TryParse(HotkeyText, out var gesture, out var error))
        {
            HotkeyStatus = error;
            return;
        }

        HotkeyText = gesture.ToString();
        _host.History.SetSetting(SettingKeys.Hotkey, HotkeyText);
        HotkeyStatus = _applyHotkey(gesture);
        Changed?.Invoke();
    }
}
