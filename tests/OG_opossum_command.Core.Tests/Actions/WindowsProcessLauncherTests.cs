using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Actions;

/// <summary>Test classes that start real throwaway processes run one at a time (ADR-030).</summary>
[CollectionDefinition(Name)]
public sealed class RealProcesses
{
    public const string Name = "real processes";
}

/// <summary>The one real launcher, exercised only with throwaway processes (rule 7).</summary>
[Collection(RealProcesses.Name)]
public sealed class WindowsProcessLauncherTests
{
    [Fact]
    public async Task Starts_from_a_path_and_argument_array_and_captures_bounded_output()
    {
        var launcher = new WindowsProcessLauncher();
        // Separate array elements, no shell string to escape; cmd's echo prints them joined by spaces.
        var request = new ProcessLaunchRequest(TestSupport.CmdPath, ["/c", "echo", "hello", "world"], null, CaptureOutput: true);

        using var process = launcher.Start(request);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exit = await process.WaitForExitAsync(cts.Token);

        Assert.Equal(0, exit.ExitCode);
        Assert.Contains("hello world", exit.StandardOutput, StringComparison.Ordinal);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Reports_a_non_zero_exit_code()
    {
        var launcher = new WindowsProcessLauncher();

        using var process = launcher.Start(new ProcessLaunchRequest(TestSupport.CmdPath, ["/c", "exit", "7"], null, CaptureOutput: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exit = await process.WaitForExitAsync(cts.Token);

        Assert.Equal(7, exit.ExitCode);
    }

    [Fact]
    public async Task KillTree_ends_the_process_and_its_children()
    {
        var launcher = new WindowsProcessLauncher();
        var pingsBefore = TestSupport.PingPids();

        using var process = launcher.Start(new ProcessLaunchRequest(TestSupport.CmdPath, ["/c", "ping", "-n", "30", "127.0.0.1"], null, CaptureOutput: true));
        var pingPid = await TestSupport.WaitForNewPingAsync(pingsBefore, TimeSpan.FromSeconds(20));

        process.KillTree();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cts.Token);
        Assert.True(process.HasExited);
        Assert.True(await TestSupport.WaitForPingGoneAsync(pingPid, TimeSpan.FromSeconds(5)), "the ping child survived KillTree");
        process.KillTree(); // safe after exit
    }

    [Fact]
    public async Task Cancelling_the_wait_does_not_kill_the_process_by_itself()
    {
        var launcher = new WindowsProcessLauncher();
        using var process = launcher.Start(new ProcessLaunchRequest(TestSupport.CmdPath, ["/c", "ping", "-n", "30", "127.0.0.1"], null, CaptureOutput: true));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitForExitAsync(cts.Token));

            // Spec §06: a cancelled token is not process termination; the action must call KillTree explicitly.
            Assert.False(process.HasExited);
        }
        finally
        {
            process.KillTree();
        }
    }
}
