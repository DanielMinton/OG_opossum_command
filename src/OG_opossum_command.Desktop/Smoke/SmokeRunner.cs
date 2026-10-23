using System.Globalization;
using System.IO;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Seeding;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Desktop.Smoke;

/// <summary>
/// The two headless self-checks the build script runs against the Debug exe. Both use recording launchers
/// (no browser, folder, or program is ever opened), exit on their own within 30 seconds, and never touch the
/// real data root: when <c>OGOC_DATA_DIR</c> is not set they use a throwaway folder under %TEMP% (ADR-026).
/// Stdout of a WinExe only reaches a caller that redirects it; the exit code is the contract (ADR-016).
/// </summary>
internal static class SmokeRunner
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(25);

    /// <summary>Workflow run through the engine with fakes: one URL step, so the recording opener must see exactly one call.</summary>
    private const string SmokeWorkflowJson = """
        {
          "schemaVersion": 1,
          "id": "smoke-noop",
          "name": "Smoke no-op",
          "profileId": "personal",
          "trigger": { "type": "manual" },
          "steps": [
            { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.invalid/smoke" }, "timeoutSeconds": 10 }
          ]
        }
        """;

    public static int RunSmokeTest()
    {
        using var watchdog = StartWatchdog("SMOKE");
        using var data = SmokeDataRoot.Acquire();
        try
        {
            var paths = data.Paths;
            Console.WriteLine($"data root: {paths.DataRoot}{(paths.IsOverridden ? $" (from {AppPaths.DataDirVariable})" : " (temporary)")}");

            var launchers = new RecordingLaunchers();
            using var host = CoreHost.Open(paths, launchers.Set, FirstRunSeeder.DefaultExamplesRoot);
            Console.WriteLine($"database: {host.History.Path} (schema {host.History.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, {host.InterruptedOnStartup.ToString(CultureInfo.InvariantCulture)} interrupted)");

            foreach (var seeded in host.Seeding)
            {
                Console.WriteLine($"seeding {seeded.Profile}: {(seeded.CopiedFiles.Count > 0 ? $"copied {seeded.CopiedFiles.Count.ToString(CultureInfo.InvariantCulture)} example(s)" : seeded.Reason)}");
            }

            foreach (var error in host.Profiles.Errors)
            {
                Console.WriteLine($"profile error: {error}");
            }

            var failed = host.Profiles.Errors.Count > 0;
            foreach (var profile in host.Workflows)
            {
                Console.WriteLine($"profile {profile.Profile.DisplayName}: {profile.Workflows.Count.ToString(CultureInfo.InvariantCulture)} workflow(s), {profile.Errors.Count.ToString(CultureInfo.InvariantCulture)} error(s) in {profile.Profile.WorkflowsDirectory}");
                foreach (var error in profile.Errors)
                {
                    Console.WriteLine($"  {error}");
                    failed = true;
                }
            }

            if (failed)
            {
                Console.Error.WriteLine("SMOKE FAILED: profiles or workflows did not validate.");
                return 1;
            }

            var loaded = new WorkflowLoader(host.Actions).LoadJson(SmokeWorkflowJson, host.Profiles.Get(Core.Profiles.ProfileId.Personal), "<smoke>");
            if (loaded.Workflow is null)
            {
                Console.Error.WriteLine("SMOKE FAILED: built-in smoke workflow did not validate: " + string.Join("; ", loaded.Errors));
                return 1;
            }

            var started = host.Engine.Start(loaded.Workflow, host.Profiles.Get(Core.Profiles.ProfileId.Personal));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = host.Engine.WaitForCompletionAsync(started.Execution.Id, timeout.Token).GetAwaiter().GetResult();
            var persisted = host.History.GetExecution(result.Id);
            Console.WriteLine($"engine run {result.Id}: {result.Status}, {launchers.Urls.Opened.Count.ToString(CultureInfo.InvariantCulture)} fake URL open(s), persisted status {persisted?.Status.ToString() ?? "missing"}");

            if (result.Status != ExecutionStatus.Completed || launchers.Urls.Opened.Count != 1 || persisted?.Status != ExecutionStatus.Completed)
            {
                Console.Error.WriteLine("SMOKE FAILED: engine run did not complete as expected.");
                return 1;
            }

            Console.WriteLine("SMOKE OK");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"SMOKE FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static int RunSmokeUi()
    {
        using var watchdog = StartWatchdog("SMOKE-UI");
        using var data = SmokeDataRoot.Acquire();
        var exitCode = 1;
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"SMOKE-UI FAILED: unhandled {e.ExceptionObject}");
                Environment.Exit(1);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Console.Error.WriteLine($"SMOKE-UI FAILED: unobserved task exception {e.Exception}");
                Environment.Exit(1);
            };

            var app = new App
            {
                SmokeUi = true,
                PathsOverride = data.Paths,
                LaunchersOverride = new RecordingLaunchers().Set,
            };
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"SMOKE-UI FAILED: {e.Exception}");
                Environment.Exit(1);
            };

            exitCode = app.Run();
            if (exitCode == 0)
            {
                Console.WriteLine("SMOKE-UI OK");
            }
            else
            {
                Console.Error.WriteLine($"SMOKE-UI FAILED: app exited {exitCode.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"SMOKE-UI FAILED: {ex.GetType().Name}: {ex.Message}");
            exitCode = 1;
        }

        return exitCode;
    }

    private static Timer StartWatchdog(string label) =>
        new(
            _ =>
            {
                Console.Error.WriteLine($"{label} FAILED: exceeded {Watchdog.TotalSeconds:0} s; exiting 1");
                Environment.Exit(1);
            },
            null,
            Watchdog,
            Timeout.InfiniteTimeSpan);

    /// <summary>The data root a smoke run uses: <c>OGOC_DATA_DIR</c> when set, else a %TEMP% folder deleted on dispose.</summary>
    private sealed class SmokeDataRoot : IDisposable
    {
        private readonly string? _temporaryFolder;

        private SmokeDataRoot(AppPaths paths, string? temporaryFolder)
        {
            Paths = paths;
            _temporaryFolder = temporaryFolder;
        }

        public AppPaths Paths { get; }

        public static SmokeDataRoot Acquire()
        {
            var fromEnvironment = AppPaths.FromEnvironment();
            if (fromEnvironment.IsOverridden)
            {
                // ForRoot, not FromEnvironment: no OneDrive root is resolved, so no profile has a calendar file and a
                // smoke run never looks inside the user's real OneDrive (rule 5, ADR-039).
                return new SmokeDataRoot(AppPaths.ForRoot(fromEnvironment.DataRoot), null);
            }

            var folder = Path.Combine(Path.GetTempPath(), $"OG_opossum_command-smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            return new SmokeDataRoot(AppPaths.ForRoot(folder), folder);
        }

        public void Dispose()
        {
            if (_temporaryFolder is null)
            {
                return;
            }

            try
            {
                Directory.Delete(_temporaryFolder, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; a leftover temp folder is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
