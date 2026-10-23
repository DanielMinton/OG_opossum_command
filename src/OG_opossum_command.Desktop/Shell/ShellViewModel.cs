using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Desktop.Dashboard;
using OG_opossum_command.Desktop.History;

namespace OG_opossum_command.Desktop.Shell;

/// <summary>Root view model: active profile, which view is showing, host status, and the tray commands.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private const string ActiveProfileSetting = "ui.activeProfile";

    private readonly CoreHost _host;
    private readonly App _app;
    private readonly bool _ready;

    public ShellViewModel(CoreHost host, Dispatcher dispatcher, App app)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(app);

        _host = host;
        _app = app;
        Profiles = ProfileIdExtensions.All;
        Dashboard = new DashboardViewModel(host, dispatcher);
        History = new HistoryViewModel(host, dispatcher);

        var saved = host.History.GetSetting(ActiveProfileSetting);
        ActiveProfile = ProfileIdExtensions.TryParseJsonValue(saved, out var profile) ? profile : ProfileId.Work;
        Dashboard.Load(ActiveProfile);
        History.Load(ActiveProfile);
        CurrentView = Dashboard;
        HostStatus = host.InterruptedOnStartup > 0
            ? $"Host: ● Running ({host.InterruptedOnStartup} run(s) interrupted last time)"
            : "Host: ● Running";
        _ready = true;
    }

    /// <summary>Both profiles, for the picker.</summary>
    public IReadOnlyList<ProfileId> Profiles { get; }

    public DashboardViewModel Dashboard { get; }

    public HistoryViewModel History { get; }

    [ObservableProperty]
    public partial string HostStatus { get; set; }

    [ObservableProperty]
    public partial object CurrentView { get; set; }

    [ObservableProperty]
    public partial ProfileId ActiveProfile { get; set; }

    public bool IsWorkActive => ActiveProfile == ProfileId.Work;

    public bool IsPersonalActive => ActiveProfile == ProfileId.Personal;

    partial void OnActiveProfileChanged(ProfileId value)
    {
        if (!_ready)
        {
            // Constructor sets the initial value and loads both views itself.
            return;
        }

        _host.History.SetSetting(ActiveProfileSetting, value.ToJsonValue());
        Dashboard.Load(value);
        History.Load(value);
        OnPropertyChanged(nameof(IsWorkActive));
        OnPropertyChanged(nameof(IsPersonalActive));
    }

    [RelayCommand]
    private void ShowDashboard()
    {
        Dashboard.RefreshRecentRuns();
        CurrentView = Dashboard;
    }

    [RelayCommand]
    private void ShowHistory()
    {
        History.Load(ActiveProfile);
        CurrentView = History;
    }

    [RelayCommand]
    private void SwitchProfile(ProfileId profile) => ActiveProfile = profile;

    [RelayCommand]
    private void Hide() => _app.MainWindow?.Hide();

    [RelayCommand]
    private void Open() => _app.ShowDashboard();

    [RelayCommand]
    private void Quit() => _app.Quit();
}
