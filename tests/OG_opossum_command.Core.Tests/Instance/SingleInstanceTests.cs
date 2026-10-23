using System.Diagnostics;
using System.Reflection;
using OG_opossum_command.Core.Instance;
using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Tests.Instance;

/// <summary>
/// Lock behaviour uses unique per-test names so a running app (or a parallel test) can never interfere. A Windows
/// mutex is re-entrant for the thread that owns it, so the "second instance" is always acquired on another thread,
/// as a second process would be.
/// </summary>
public sealed class SingleInstanceTests
{
    private static string UniqueName() => $"OG_opossum_command-test-{Guid.NewGuid():N}";

    /// <summary>Acquires on a different thread, like a second process would; only used where the result is expected not to own the lock.</summary>
    private static SingleInstanceLock AcquireElsewhere(string name)
    {
        SingleInstanceLock? result = null;
        var thread = new Thread(() => result = SingleInstanceLock.Acquire(name));
        thread.Start();
        thread.Join();
        return result!;
    }

    [Fact]
    public void The_first_acquirer_owns_the_lock_and_the_second_does_not()
    {
        var name = UniqueName();
        using var first = SingleInstanceLock.Acquire(name);
        using var second = AcquireElsewhere(name);

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
    }

    [Fact]
    public void A_second_launch_asks_the_first_to_show_its_dashboard()
    {
        var name = UniqueName();
        using var first = SingleInstanceLock.Acquire(name);
        using var shown = new ManualResetEventSlim(false);
        first.ShowRequested += () => shown.Set();

        using var second = AcquireElsewhere(name);
        Assert.False(second.IsOwner);
        Assert.True(SingleInstanceLock.SignalExisting(name));

        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the owner was not told to show itself");
    }

    [Fact]
    public void Signalling_when_nobody_owns_the_lock_is_harmless()
    {
        Assert.False(SingleInstanceLock.SignalExisting(UniqueName()));
    }

    [Fact]
    public void Disposing_the_owner_frees_the_lock_for_the_next_launch()
    {
        var name = UniqueName();
        var first = SingleInstanceLock.Acquire(name);
        using (var second = AcquireElsewhere(name))
        {
            Assert.False(second.IsOwner);
        }

        first.Dispose();
        using var third = SingleInstanceLock.Acquire(name);

        Assert.True(third.IsOwner);
    }

    /// <summary>
    /// ADR-036: smoke modes are exempt from the single-instance rule. This test holds the production lock (or finds it
    /// already held by a running app, which is just as good) and starts the real Desktop exe in smoke-test mode with
    /// its data root pointed at a throwaway folder. The exe uses recording launchers, so nothing opens.
    /// </summary>
    [Fact]
    public async Task Smoke_test_mode_succeeds_while_another_instance_holds_the_production_lock()
    {
        var exe = typeof(SingleInstanceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DesktopExePath").Value!;
        Assert.True(File.Exists(exe), $"Desktop exe not found at {exe}; build the solution first.");

        var dataRoot = Path.Combine(Path.GetTempPath(), "OG_opossum_command-tests", $"smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        using var held = SingleInstanceLock.Acquire(); // production name; IsOwner may be false if the real app is running, which still means "held"
        try
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--smoke-test");
            startInfo.Environment[AppPaths.DataDirVariable] = dataRoot; // never the test runner's inherited scratch folder, never the real data root

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("smoke-test did not finish within 60 s");
            }

            var output = await stdout;
            Assert.True(process.ExitCode == 0, $"smoke-test exited {process.ExitCode}: {output} {await stderr}");
            Assert.Contains("SMOKE OK", output, StringComparison.Ordinal);
            Assert.Contains(dataRoot, output, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(dataRoot, AppPaths.DatabaseFileName)));
        }
        finally
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
