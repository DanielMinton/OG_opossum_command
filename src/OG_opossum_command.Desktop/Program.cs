using OG_opossum_command.Desktop.Smoke;

namespace OG_opossum_command.Desktop;

/// <summary>
/// Single entry point (csproj StartupObject). Diagnostic modes are handled here, before the WPF
/// <see cref="App"/> exists for <c>--smoke-test</c>, and before the single-instance check for both
/// smoke modes, so they never show a window, take the lock, or touch anything but the data root they
/// were given (ADR-016, ADR-026).
/// </summary>
internal static class Program
{
    /// <summary>Headless check used by the build script: paths, DB, profiles, workflows, one engine run with fakes; prints <c>SMOKE OK</c>, exits 0.</summary>
    internal const string SmokeTestSwitch = "--smoke-test";

    /// <summary>UI check: real App startup, off-screen MainWindow, tray icon created and disposed; prints <c>SMOKE-UI OK</c>, exits 0.</summary>
    internal const string SmokeUiSwitch = "--smoke-ui";

    // Session-local kernel objects (ADR-028): no registry, no network, no elevation. Held for the life of the process.
    private const string InstanceMutexName = @"Local\OG_opossum_command.instance";
    private const string ShowDashboardEventName = @"Local\OG_opossum_command.show-dashboard";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showDashboardEvent;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains(SmokeTestSwitch, StringComparer.OrdinalIgnoreCase))
        {
            return SmokeRunner.RunSmokeTest();
        }

        if (args.Contains(SmokeUiSwitch, StringComparer.OrdinalIgnoreCase))
        {
            return SmokeRunner.RunSmokeUi();
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Another instance owns this Windows session: ask it to show its dashboard, then leave quietly.
            SignalExistingInstance();
            return 0;
        }

        _showDashboardEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowDashboardEventName);

        var app = new App();
        app.InitializeComponent();
        StartShowDashboardListener(app, _showDashboardEvent);
        return app.Run();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ShowDashboardEventName);
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting up or already quitting; nothing to show.
        }
        catch (UnauthorizedAccessException)
        {
            // Not ours to signal.
        }
    }

    private static void StartShowDashboardListener(App app, EventWaitHandle showDashboard)
    {
        var listener = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    showDashboard.WaitOne();
                    if (app.Dispatcher.HasShutdownStarted)
                    {
                        return;
                    }

                    _ = app.Dispatcher.BeginInvoke(app.ShowDashboard);
                }
            }
            catch (ObjectDisposedException)
            {
                // Process is exiting.
            }
        })
        {
            IsBackground = true,
            Name = "OG_opossum_command single-instance listener",
        };
        listener.Start();
    }
}
