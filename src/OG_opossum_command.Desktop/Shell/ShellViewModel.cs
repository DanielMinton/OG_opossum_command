using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Notifications;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Desktop.CommandPalette;
using OG_opossum_command.Desktop.Dashboard;
using OG_opossum_command.Desktop.History;
using OG_opossum_command.Desktop.Settings;

namespace OG_opossum_command.Desktop.Shell;

/// <summary>Root view model: active profile, which view is showing, host status, and the tray commands.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly CoreHost _host;
    private readonly App _app;
    private readonly bool _ready;
    private string? _hotkeyProblem;

    public ShellViewModel(CoreHost host, Dispatcher dispatcher, App app, NotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(notifications);

        _host = host;
        _app = app;
        Profiles = ProfileIdExtensions.All;
        Dashboard = new DashboardViewModel(host, dispatcher);
        History = new HistoryViewModel(host, dispatcher);
        Settings = new SettingsViewModel(host, notifications, app.ApplyHotkey, dispatcher);
        Settings.Changed += RefreshHostStatus;

        var saved = host.History.GetSetting(SettingKeys.ActiveProfile);
        ActiveProfile = ProfileIdExtensions.TryParseJsonValue(saved, out var profile) ? profile : ProfileId.Work;
        Dashboard.Load(ActiveProfile);
        History.Load(ActiveProfile);
        CurrentView = Dashboard;
        HostStatus = string.Empty;
        RefreshHostStatus();
        _ready = true;
    }

    /// <summary>Both profiles, for the picker.</summary>
    public IReadOnlyList<ProfileId> Profiles { get; }

    public DashboardViewModel Dashboard { get; }

    public HistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial string HostStatus { get; set; }

    [ObservableProperty]
    public partial object CurrentView { get; set; }

    [ObservableProperty]
    public partial ProfileId ActiveProfile { get; set; }

    /// <summary>Records the hotkey registration outcome (null when fine) and refreshes the status line.</summary>
    public void ReportHotkey(string? problem, string statusForSettings)
    {
        _hotkeyProblem = problem;
        Settings.ReportHotkey(statusForSettings);
        RefreshHostStatus();
    }

    /// <summary>Rebuilds the one-line host status: running, interrupted count, schedules paused, hotkey problem.</summary>
    public void RefreshHostStatus()
    {
        var parts = new List<string> { "Host: ● Running" };
        if (_host.InterruptedOnStartup > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{_host.InterruptedOnStartup} run(s) interrupted last time"));
        }

        if (_host.Scheduler.IsPaused)
        {
            parts.Add("schedules paused");
        }

        if (_hotkeyProblem is not null)
        {
            parts.Add(_hotkeyProblem);
        }

        HostStatus = string.Join(" · ", parts);
    }

    partial void OnActiveProfileChanged(ProfileId value)
    {
        if (!_ready)
        {
            // Constructor sets the initial value and loads both views itself.
            return;
        }

        _host.History.SetSetting(SettingKeys.ActiveProfile, value.ToJsonValue());
        Dashboard.Load(value);
        History.Load(value);
    }

    [RelayCommand]
    private void ShowDashboard()
    {
        Dashboard.RefreshRecentRuns();
        Dashboard.RefreshAttention();
        CurrentView = Dashboard;
    }

    [RelayCommand]
    private void ShowHistory()
    {
        History.Load(ActiveProfile);
        CurrentView = History;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        Settings.Refresh();
        CurrentView = Settings;
    }

    [RelayCommand]
    private void OpenPalette() => _app.ShowCommandPalette();

    [RelayCommand]
    private void SwitchProfile(ProfileId profile) => ActiveProfile = profile;

    [RelayCommand]
    private void Hide() => _app.MainWindow?.Hide();

    [RelayCommand]
    private void Quit() => _app.Quit();
}
