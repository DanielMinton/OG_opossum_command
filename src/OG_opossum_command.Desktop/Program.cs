using OG_opossum_command.Core.Instance;
using OG_opossum_command.Desktop.Smoke;

namespace OG_opossum_command.Desktop;

/// <summary>
/// Single entry point (csproj StartupObject). Diagnostic modes are handled here, before the WPF
/// <see cref="App"/> exists for <c>--smoke-test</c>, and before the single-instance check for both
/// smoke modes, so they never show a window, take the lock, or touch anything but the data root they
/// were given (ADR-016, ADR-026, ADR-036).
/// </summary>
internal static class Program
{
    /// <summary>Headless check used by the build script: paths, DB, profiles, workflows, one engine run with fakes; prints <c>SMOKE OK</c>, exits 0.</summary>
    internal const string SmokeTestSwitch = "--smoke-test";

    /// <summary>UI check: real App startup, off-screen MainWindow, tray icon created and disposed; prints <c>SMOKE-UI OK</c>, exits 0.</summary>
    internal const string SmokeUiSwitch = "--smoke-ui";

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

        // One instance per Windows session (ADR-028/036): the second launch asks the first to show its dashboard and leaves.
        using var instance = SingleInstanceLock.Acquire();
        if (!instance.IsOwner)
        {
            SingleInstanceLock.SignalExisting();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        instance.ShowRequested += () =>
        {
            if (!app.Dispatcher.HasShutdownStarted)
            {
                _ = app.Dispatcher.BeginInvoke(app.ShowDashboard);
            }
        };
        return app.Run();
    }
}
