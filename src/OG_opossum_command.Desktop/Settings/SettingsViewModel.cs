using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Notifications;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Desktop.CommandPalette;

namespace OG_opossum_command.Desktop.Settings;

/// <summary>One schedule row shown read-only in Settings.</summary>
public sealed record ScheduleRow(string Workflow, string Profile, string Schedule, string NextDue, string LastOutcome)
{
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

/// <summary>Hotkey, quiet mode, schedule pause, and a read-only view of the schedules (spec §14).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly CoreHost _host;
    private readonly NotificationService _notifications;
    private readonly Func<HotkeyGesture, string> _applyHotkey;
    private readonly bool _ready;

    /// <param name="applyHotkey">Re-registers the hotkey and returns a one-line result for the user.</param>
    public SettingsViewModel(CoreHost host, NotificationService notifications, Func<HotkeyGesture, string> applyHotkey)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(applyHotkey);

        _host = host;
        _notifications = notifications;
        _applyHotkey = applyHotkey;
        HotkeyText = host.History.GetSetting(SettingKeys.Hotkey) ?? HotkeyGesture.DefaultText;
        QuietMode = notifications.QuietMode;
        SchedulesPaused = host.Scheduler.IsPaused;
        DataRoot = host.Paths.DataRoot;
        HotkeyStatus = string.Empty;
        Refresh();
        _ready = true;
    }

    /// <summary>Raised after a setting changed so the shell can refresh its status line.</summary>
    public event Action? Changed;

    public ObservableCollection<ScheduleRow> Schedules { get; } = [];

    [ObservableProperty]
    public partial string HotkeyText { get; set; }

    [ObservableProperty]
    public partial string HotkeyStatus { get; set; }

    [ObservableProperty]
    public partial bool QuietMode { get; set; }

    [ObservableProperty]
    public partial bool SchedulesPaused { get; set; }

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
    }

    /// <summary>Sets the status line shown next to the hotkey box (used by the shell on startup).</summary>
    public void ReportHotkey(string status) => HotkeyStatus = status;

    partial void OnQuietModeChanged(bool value)
    {
        if (_ready)
        {
            _notifications.QuietMode = value;
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
