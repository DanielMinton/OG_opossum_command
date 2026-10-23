using System.IO;
using System.Windows;
using System.Windows.Threading;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Notifications;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Seeding;
using OG_opossum_command.Desktop.Accessibility;
using OG_opossum_command.Desktop.CommandPalette;
using OG_opossum_command.Desktop.Feedback;
using OG_opossum_command.Desktop.Shell;
using OG_opossum_command.Desktop.Smoke;
using OG_opossum_command.Desktop.Tray;

namespace OG_opossum_command.Desktop;

/// <summary>
/// WPF application: one tray-resident process (ADR-004). Closing the window hides it; Quit in the tray
/// menu ends the process. Constructed by <see cref="Program"/> for interactive runs and by the smoke runner
/// for <c>--smoke-ui</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Application is not IDisposable; the tray icon, hotkey, and notifications are disposed in OnExit and in Quit.")]
public partial class App : Application, IDashboardPresenter
{
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private NotificationService? _notifications;
    private SoundCuePlayer? _sounds;
    private CommandPaletteWindow? _palette;
    private bool _quitting;

    /// <summary>Set by the smoke runner: build everything off-screen, create and dispose the tray icon, then exit.</summary>
    public bool SmokeUi { get; init; }

    /// <summary>Data root override for smoke runs; otherwise <see cref="AppPaths.FromEnvironment"/>.</summary>
    public IAppPaths? PathsOverride { get; init; }

    /// <summary>Launchers override for smoke runs; otherwise the real Windows launchers.</summary>
    public LauncherSet? LaunchersOverride { get; init; }

    /// <summary>Core services, available after startup.</summary>
    internal CoreHost? Host { get; private set; }

    /// <summary>Root view model, available after startup.</summary>
    internal ShellViewModel? Shell { get; private set; }

    /// <summary>Shows and activates the dashboard window (tray Open, double-click).</summary>
    internal void ShowDashboard()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
    }

    /// <summary>
    /// <c>dashboard.schedule.show</c> (ADR-039): switch to the run's profile, show the dashboard with its Today panel,
    /// and bring the window forward. Engine worker threads call this, so it marshals to the dispatcher. Smoke runs
    /// never get here: they wire a recording presenter instead (rule 7).
    /// </summary>
    void IDashboardPresenter.ShowSchedule(ProfileId profile)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (Shell is null || _quitting)
            {
                return;
            }

            Shell.ActiveProfile = profile;
            Shell.ShowDashboardCommand.Execute(null);
            ShowDashboard();
        });
    }

    /// <summary>Opens the command palette (hotkey, tray menu, Settings), or brings the open one forward.</summary>
    internal void ShowCommandPalette()
    {
        if (Host is null || _quitting)
        {
            return;
        }

        if (_palette is not null)
        {
            _palette.Activate();
            return;
        }

        _palette = new CommandPaletteWindow(new CommandPaletteViewModel(Host), message => Shell?.Dashboard.ShowMessage(message));
        _palette.Closed += (_, _) => _palette = null;
        _palette.Show();
        _palette.Activate();
    }

    /// <summary>Registers <paramref name="gesture"/> as the palette hotkey (Settings → Apply). Returns a one-line result.</summary>
    internal string ApplyHotkey(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (SmokeUi || MainWindow is null)
        {
            return $"Hotkey {gesture} saved (not registered in smoke mode).";
        }

        _hotkey ??= new GlobalHotkey(MainWindow, ShowCommandPalette);
        var ok = _hotkey.Register(gesture);
        var status = ok ? $"Hotkey {gesture} is active." : _hotkey.FailureReason ?? $"Hotkey {gesture} could not be registered.";
        Shell?.ReportHotkey(ok ? null : _hotkey.FailureReason, status);
        return status;
    }

    /// <summary>Tray Quit: cancel active runs, give them a moment, then exit the process.</summary>
    internal async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _hotkey?.Dispose();
        _sounds?.Dispose();
        _hotkey = null;
        _tray?.Dispose();
        _tray = null;
        _palette?.Close();
        MainWindow?.Hide();
        if (Host is not null)
        {
            // Stop the scheduler first so nothing new starts during the engine's grace period.
            Host.Scheduler.Dispose();
            await Host.Engine.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        Shutdown(0);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FontScale.Track(this);
        AccentBrushes.Track(this);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var paths = PathsOverride ?? AppPaths.FromEnvironment();
        try
        {
            Host = CoreHost.Open(paths, LaunchersOverride ?? LauncherSet.Windows(this), FirstRunSeeder.DefaultExamplesRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFatal($"Could not start: {ex.Message}", ex, paths);
            Shutdown(1);
            return;
        }

        if (!SmokeUi)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        // Balloons go through the injectable sink; the smoke run records instead of showing (rule 7, ADR-037).
        INotificationSink sink = SmokeUi ? new RecordingNotificationSink() : new TrayNotificationSink(Dispatcher, () => _tray?.Icon);
        _notifications = new NotificationService(Host.History, sink, Host.Engine, Host.Scheduler);

        // Motion and sound (ADR-057). Smoke runs snapshot the final frame and stay silent.
        // OGOC_SMOKE_MOTION=1 keeps motion on in a smoke run, to exercise it and capture frames mid-entrance.
        Motion.ForceDisabled = SmokeUi && Environment.GetEnvironmentVariable("OGOC_SMOKE_MOTION") != "1";
        Motion.ApplySetting(Host.History.GetSetting(SettingKeys.Motion));
        if (!SmokeUi)
        {
            _sounds = new SoundCuePlayer();
            var history = Host.History;
            var notifications = _notifications;
            UiFeedback.Install(_sounds, () => history.GetSetting(SettingKeys.Sounds), () => notifications.QuietMode);
            _ = _sounds.LoadAsync();
        }

        Shell = new ShellViewModel(Host, Dispatcher, this, _notifications);
        var window = new MainWindow { DataContext = Shell };
        MainWindow = window;
        window.Closing += (_, args) =>
        {
            if (!_quitting)
            {
                // Closing hides to the tray; workflows keep running (ADR-004).
                args.Cancel = true;
                window.Hide();
            }
        };

        if (SmokeUi)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }

        _tray = new TrayIcon(this, Shell);
        window.Show();

        if (SmokeUi)
        {
            _ = CompleteSmokeUiAsync(window);
        }
        else
        {
            RegisterSavedHotkey();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _sounds?.Dispose();
        _tray?.Dispose();
        _notifications?.Dispose();
        Host?.Dispose();
        base.OnExit(e);
    }

    private void RegisterSavedHotkey()
    {
        var saved = Host!.History.GetSetting(SettingKeys.Hotkey);
        if (!HotkeyGesture.TryParse(saved, out var gesture, out _))
        {
            gesture = HotkeyGesture.Default;
        }

        ApplyHotkey(gesture);
    }

    private async Task CompleteSmokeUiAsync(MainWindow window)
    {
        try
        {
            await window.LoadedTask;
            await Shell!.Dashboard.FirstBindCompleted;

            if (Motion.IsEnabled)
            {
                // Frames from the entrance, then the settled dashboard.
                await Task.Delay(140);
                SmokeSnapshot.Save(window, "dashboard-enter-1");
                await Task.Delay(220);
                SmokeSnapshot.Save(window, "dashboard-enter-2");
                await Task.Delay(900);
            }

            SmokeSnapshot.Save(window, "dashboard");

            // Instantiate the other surfaces off-screen so XAML resource errors fail the gate, not the user's first keypress.
            Shell.ShowHistoryCommand.Execute(null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            SmokeSnapshot.Save(window, "history");
            Shell.ShowSettingsCommand.Execute(null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            SmokeSnapshot.Save(window, "settings");
            var palette = new CommandPaletteWindow(new CommandPaletteViewModel(Host!), null)
            {
                SuppressCloseOnDeactivate = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                Topmost = false,
            };
            palette.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var paletteItems = palette.Results.Items.Count;
            SmokeSnapshot.Save(palette, "palette");
            palette.Close();
            Shell.ShowDashboardCommand.Execute(null);
            await Shell.Dashboard.RefreshCarryoversAsync();

            _tray?.Dispose();
            _tray = null;
            Console.WriteLine($"smoke-ui: window loaded, dashboard bound ({Shell.Dashboard.Tiles.Count} tiles), tray icon created and disposed");
            Console.WriteLine($"smoke-ui: today panel bound ({Shell.Dashboard.Today.Count} event(s)): {Shell.Dashboard.TodayStatus}");
            Console.WriteLine($"smoke-ui: today panel carryovers bound ({Shell.Dashboard.Carryovers.Count}): {Shell.Dashboard.CarryoverNote}");
            Console.WriteLine($"smoke-ui: settings view bound ({Shell.Settings.Schedules.Count} schedule(s), exports for {Shell.Settings.Exports.Count} profile(s)), command palette shown off-screen with {paletteItems} item(s) and closed, hotkey not registered");
            _quitting = true;
            Shutdown(0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"SMOKE-UI FAILED: {ex}");
            _quitting = true;
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatal($"Something went wrong: {e.Exception.Message}", e.Exception, Host?.Paths);
        // Not handled: WPF terminates the process. A half-broken tray app is worse than a visible crash.
    }

    private void ReportFatal(string message, Exception exception, IAppPaths? paths)
    {
        try
        {
            if (paths is not null)
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                File.AppendAllText(
                    Path.Combine(paths.LogsDirectory, "crash.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Nothing else to do.
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing else to do.
        }

        if (SmokeUi)
        {
            Console.Error.WriteLine($"SMOKE-UI FAILED: {message}");
            return;
        }

        MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}Details were written to {paths?.LogsDirectory ?? "the log folder"}.",
            "OG Opossum Command Central",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
