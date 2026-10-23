using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Desktop.Dashboard;

namespace OG_opossum_command.Desktop.History;

/// <summary>Past runs of the active profile, with per-step detail and the redacted event log of the selected run.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private const int RunLimit = 100;

    private readonly CoreHost _host;
    private readonly Dispatcher _dispatcher;
    private ProfileId _profile = ProfileId.Work;

    public HistoryViewModel(CoreHost host, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _host = host;
        _dispatcher = dispatcher;
        _host.Engine.ExecutionChanged += OnExecutionChanged;
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<RunSummary> Runs { get; } = [];

    /// <summary>Steps of <see cref="SelectedRun"/>.</summary>
    public ObservableCollection<StepRow> Steps { get; } = [];

    /// <summary>Events of <see cref="SelectedRun"/>, oldest first.</summary>
    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty]
    public partial RunSummary? SelectedRun { get; set; }

    [ObservableProperty]
    public partial string DetailTitle { get; set; } = "Select a run to see its steps.";

    [ObservableProperty]
    public partial string ProfileName { get; set; } = ProfileId.Work.DisplayName();

    /// <summary>Loads the runs of <paramref name="profile"/>, keeping the selection when the run is still listed.</summary>
    public void Load(ProfileId profile)
    {
        _profile = profile;
        ProfileName = profile.DisplayName();
        var selected = SelectedRun?.Id;

        Runs.Clear();
        foreach (var execution in _host.History.GetRecentExecutions(profile, RunLimit))
        {
            Runs.Add(RunSummary.From(execution));
        }

        SelectedRun = Runs.FirstOrDefault(r => r.Id == selected);
    }

    [RelayCommand]
    private void Refresh() => Load(_profile);

    partial void OnSelectedRunChanged(RunSummary? value)
    {
        Steps.Clear();
        Events.Clear();
        if (value is null)
        {
            DetailTitle = "Select a run to see its steps.";
            return;
        }

        var execution = _host.History.GetExecution(value.Id);
        if (execution is null)
        {
            DetailTitle = "This run is no longer in the database.";
            return;
        }

        DetailTitle = string.Create(CultureInfo.CurrentCulture, $"{execution.WorkflowName} · {DashboardViewModel.StatusText(execution.Status)} · {value.When}");
        foreach (var step in execution.Steps)
        {
            Steps.Add(StepRow.From(step));
        }

        foreach (var evt in _host.History.GetEvents(value.Id))
        {
            var at = evt.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            var step = evt.StepId is null ? string.Empty : $" [{evt.StepId}]";
            var duration = evt.DurationMs is null ? string.Empty : string.Create(CultureInfo.CurrentCulture, $" ({evt.DurationMs} ms)");
            Events.Add($"{at} {evt.Severity}{step} {evt.EventType}: {evt.Message}{duration}");
        }
    }

    private void OnExecutionChanged(object? sender, ExecutionSnapshot snapshot)
    {
        if (_dispatcher.HasShutdownStarted || snapshot.Profile != _profile || snapshot.IsActive)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (snapshot.Profile == _profile)
            {
                Load(_profile);
            }
        });
    }
}
