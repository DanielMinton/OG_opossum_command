using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Desktop;

/// <summary>
/// Single entry point (csproj StartupObject). Diagnostic modes are handled here, before the WPF
/// <see cref="App"/> exists, so they never show a window or touch anything but the data
/// root they were given.
/// </summary>
internal static class Program
{
    /// <summary>Headless check used by the build script: resolve paths, print <c>SMOKE OK</c>, exit 0.</summary>
    internal const string SmokeTestSwitch = "--smoke-test";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains(SmokeTestSwitch, StringComparer.OrdinalIgnoreCase))
        {
            return RunSmokeTest();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunSmokeTest()
    {
        // Stdout of a WinExe is only visible when the caller redirects it (the build script does);
        // the exit code is the contract (ADR-016).
        try
        {
            var paths = AppPaths.FromEnvironment();
            var source = paths.IsOverridden ? $" (from {AppPaths.DataDirVariable})" : string.Empty;
            Console.WriteLine($"data root: {paths.DataRoot}{source}");
            Console.WriteLine("SMOKE OK");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"SMOKE FAILED: {ex.Message}");
            return 1;
        }
    }
}
