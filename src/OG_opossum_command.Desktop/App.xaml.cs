using System.IO;
using System.Windows;
using System.Windows.Threading;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Seeding;
using OG_opossum_command.Desktop.Shell;
using OG_opossum_command.Desktop.Tray;

namespace OG_opossum_command.Desktop;

/// <summary>
/// WPF application: one tray-resident process (ADR-004). Closing the window hides it; Quit in the tray
/// menu ends the process. Constructed by <see cref="Program"/> for interactive runs and by the smoke runner
/// for <c>--smoke-ui</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Application is not IDisposable; the tray icon is disposed in OnExit and in Quit.")]
public partial class App : Application
{
    private TrayIcon? _tray;
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

    /// <summary>Tray Quit: cancel active runs, give them a moment, then exit the process.</summary>
    internal async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _tray?.Dispose();
        _tray = null;
        MainWindow?.Hide();
        if (Host is not null)
        {
            await Host.Engine.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        Shutdown(0);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var paths = PathsOverride ?? AppPaths.FromEnvironment();
        try
        {
            Host = CoreHost.Open(paths, LaunchersOverride ?? LauncherSet.Windows(), FirstRunSeeder.DefaultExamplesRoot);
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

        Shell = new ShellViewModel(Host, Dispatcher, this);
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Host?.Dispose();
        base.OnExit(e);
    }

    private async Task CompleteSmokeUiAsync(MainWindow window)
    {
        try
        {
            await window.LoadedTask;
            await Shell!.Dashboard.FirstBindCompleted;
            _tray?.Dispose();
            _tray = null;
            Console.WriteLine($"smoke-ui: window loaded, dashboard bound ({Shell.Dashboard.Tiles.Count} tiles), tray icon created and disposed");
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
