using System.Diagnostics;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Actions;

/// <summary>A grandchild that inherits the redirected pipe must never hang the wait after the process itself exits.</summary>
[Collection(RealProcesses.Name)]
public sealed class InheritedPipeTests
{
    [Fact]
    public async Task WaitForExit_returns_promptly_when_a_background_grandchild_keeps_the_pipe_open()
    {
        var launcher = new WindowsProcessLauncher();
        var pingsBefore = TestSupport.PingPids();
        int? pingPid = null;

        // `start /b` leaves ping running in the background with the inherited pipe handles; cmd itself exits at once.
        using var process = launcher.Start(new ProcessLaunchRequest(TestSupport.CmdPath, ["/c", "start", "/b", "ping", "-n", "30", "127.0.0.1", ">nul", "&", "echo", "done"], null, CaptureOutput: true));
        try
        {
            pingPid = await TestSupport.WaitForNewPingAsync(pingsBefore, TimeSpan.FromSeconds(20));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var clock = Stopwatch.StartNew();

            var exit = await process.WaitForExitAsync(cts.Token);

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the wait took {clock.Elapsed}");
            Assert.Equal(0, exit.ExitCode);
            Assert.Contains("done", exit.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(WindowsProcessLauncher.StillOpenMarker, exit.StandardOutput + exit.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            process.KillTree();
            KillPing(pingPid);
        }
    }

    [Fact]
    public async Task A_script_that_starts_a_background_program_completes_its_step_instead_of_hanging()
    {
        using var root = new TempDataRoot();
        root.WriteScript(ProfileId.Personal, "background.cmd", "@start /b ping -n 30 127.0.0.1 >nul\r\n@echo started\r\n");
        var recording = new RecordingLaunchers();
        var registry = ActionRegistry.CreateDefault(new LauncherSet(new WindowsProcessLauncher(), recording.Urls, recording.Folders, recording.Dashboard));
        var profile = root.Profile(ProfileId.Personal, registry);
        var workflow = TestSupport.LoadValid(registry, profile, """
            {
              "schemaVersion": 2, "id": "wf", "name": "WF", "profileId": "personal", "trigger": { "type": "manual" },
              "steps": [ { "id": "run", "action": "windows.script.run", "inputs": { "interpreter": "cmd", "script": "background.cmd" }, "timeoutSeconds": 15 } ]
            }
            """);
        var pingsBefore = TestSupport.PingPids();
        int? pingPid = null;
        using var history = SqliteHistoryStore.Open(root.Paths.DatabasePath);
        using var engine = new WorkflowEngine(registry, history);
        try
        {
            var clock = Stopwatch.StartNew();
            var started = engine.Start(workflow, profile);
            pingPid = await TestSupport.WaitForNewPingAsync(pingsBefore, TimeSpan.FromSeconds(20));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), $"the step took {clock.Elapsed}");
            Assert.Equal(ExecutionStatus.Completed, result.Status);
            Assert.Contains("started", result.Steps[0].Output, StringComparison.Ordinal);
        }
        finally
        {
            KillPing(pingPid);
        }
    }

    private static void KillPing(int? pid)
    {
        if (pid is null)
        {
            return;
        }

        try
        {
            using var ping = Process.GetProcessById(pid.Value);
            ping.Kill();
            ping.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Exited meanwhile.
        }
    }
}
