// Feasibility spike for OG Opossum Command Central (throwaway; see docs/FEASIBILITY.md).
//
//   Spike --check   headless: child-process checks (b) and SQLite check (c); one line per
//                   check; exit 0 only if all pass; always exits within 60 seconds.
//   Spike --tray    WPF Application + H.NotifyIcon tray icon for ~3 seconds, then clean exit 0.
//
// Writes only under %LOCALAPPDATA%\OG_opossum_command-spike and deletes it before exiting.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Data.Sqlite;

namespace Spike;

internal static class Program
{
    private const string SpikeFolderName = "OG_opossum_command-spike";
    private static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan TrayDuration = TimeSpan.FromSeconds(3);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            return RunChecks();
        }

        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            return RunTray();
        }

        Console.WriteLine("usage: Spike --check | --tray");
        return 2;
    }

    // ---------------------------------------------------------------- --check

    private static int RunChecks()
    {
        // Hard watchdog: the brief requires exit within 60 s no matter what hangs.
        using var watchdog = new System.Threading.Timer(
            _ =>
            {
                Console.WriteLine("WATCHDOG: checks exceeded the time budget; exiting 1");
                Environment.Exit(1);
            },
            null,
            CheckBudget,
            Timeout.InfiniteTimeSpan);

        var allPassed = true;
        allPassed &= Report("process.echo", CheckProcessEcho);
        allPassed &= Report("process.cancel-tree", CheckProcessCancelTree);
        allPassed &= Report("sqlite.crud", CheckSqlite);

        Console.WriteLine(allPassed ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");
        return allPassed ? 0 : 1;
    }

    private static bool Report(string name, Func<string> check)
    {
        try
        {
            var evidence = check();
            Console.WriteLine($"PASS {name}: {evidence}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // (b1) argument array, stdout capture, exit code
    private static string CheckProcessEcho()
    {
        var psi = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("echo");
        psi.ArgumentList.Add("hi");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("cmd /c echo hi did not exit within 10 s");
        }

        var trimmed = stdout.Trim();
        if (trimmed != "hi")
        {
            throw new InvalidOperationException($"expected stdout 'hi', got '{trimmed}'");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"expected exit code 0, got {process.ExitCode}");
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        return $"stdout='{trimmed}' stderr-length={stderr.Length} exit={process.ExitCode}";
    }

    // (b2) cancel a long-running child by killing its whole process tree.
    // We launch through cmd so there is a real tree (cmd -> PING) to kill.
    private static string CheckProcessCancelTree()
    {
        var baselinePing = CountProcesses("PING");

        var psi = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("127.0.0.1");

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");

        // Drain stdout in the background so the child never blocks on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync();

        Thread.Sleep(1500);
        if (process.HasExited)
        {
            throw new InvalidOperationException($"ping tree exited on its own after {stopwatch.Elapsed.TotalSeconds:F1} s (exit {process.ExitCode})");
        }

        var duringPing = CountProcesses("PING");
        if (duringPing <= baselinePing)
        {
            throw new InvalidOperationException($"expected a child PING process (baseline {baselinePing}, during {duringPing})");
        }

        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(10_000))
        {
            throw new TimeoutException("cmd did not exit within 10 s after Kill(entireProcessTree: true)");
        }

        // Give the OS a moment to reap the child, then confirm the tree is gone.
        var afterPing = duringPing;
        for (var i = 0; i < 20 && afterPing > baselinePing; i++)
        {
            Thread.Sleep(250);
            afterPing = CountProcesses("PING");
        }

        stopwatch.Stop();
        if (afterPing > baselinePing)
        {
            throw new InvalidOperationException($"orphaned PING process remains (baseline {baselinePing}, after {afterPing})");
        }

        if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
        {
            throw new TimeoutException($"cancellation took {stopwatch.Elapsed.TotalSeconds:F1} s");
        }

        return $"killed tree in {stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s; PING count {baselinePing}->{duringPing}->{afterPing}; exit={process.ExitCode}";
    }

    private static int CountProcesses(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    // (c) SQLite create / write / read / delete under %LOCALAPPDATA%\OG_opossum_command-spike
    private static string CheckSqlite()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SpikeFolderName);
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, "spike.db");

        string sqliteVersion;
        long rowCount;
        string readBack;

        // Pooling=False so the file handle is released on Dispose and the file can be deleted.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT sqlite_version();";
                sqliteVersion = (string)(cmd.ExecuteScalar() ?? "?");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE runs (id INTEGER PRIMARY KEY, note TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO runs (note) VALUES ($note);";
                cmd.Parameters.AddWithValue("$note", "feasibility");
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM runs;";
                rowCount = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT note FROM runs WHERE id = 1;";
                readBack = (string)(cmd.ExecuteScalar() ?? "");
            }
        }

        if (rowCount != 1 || readBack != "feasibility")
        {
            throw new InvalidOperationException($"read-back mismatch: count={rowCount} note='{readBack}'");
        }

        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        Directory.Delete(folder, recursive: true);

        if (Directory.Exists(folder))
        {
            throw new InvalidOperationException($"spike folder still exists: {folder}");
        }

        return $"sqlite {sqliteVersion}; wrote+read 1 row; deleted {folder}";
    }

    // ----------------------------------------------------------------- --tray

    private static int RunTray()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;

        app.Startup += (_, _) =>
        {
            TaskbarIcon? icon = null;
            try
            {
                // UNVERIFIED BUILD NOTE: SystemIcons needs System.Drawing.Common, which the
                // WPF shared framework normally provides. If this line fails to compile, use
                // H.NotifyIcon's GeneratedIconSource (e.g. new GeneratedIconSource { Text = "O" })
                // assigned to IconSource instead, or embed a .ico resource and set IconSource.
                icon = new TaskbarIcon
                {
                    ToolTipText = "OG Opossum Command Central (feasibility spike)",
                    Icon = System.Drawing.SystemIcons.Application,
                };
                icon.ForceCreate();
                Console.WriteLine("TRAY: icon created");

                var timer = new DispatcherTimer { Interval = TrayDuration };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    icon.Dispose();
                    Console.WriteLine("TRAY: icon disposed, shutting down");
                    exitCode = 0;
                    app.Shutdown(0);
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TRAY FAIL: {ex.GetType().Name}: {ex.Message}");
                icon?.Dispose();
                app.Shutdown(1);
            }
        };

        // Safety net so a stuck message loop cannot outlive the check budget.
        using var watchdog = new System.Threading.Timer(
            _ =>
            {
                Console.WriteLine("WATCHDOG: tray run exceeded the time budget; exiting 1");
                Environment.Exit(1);
            },
            null,
            CheckBudget,
            Timeout.InfiniteTimeSpan);

        var runResult = app.Run();
        Console.WriteLine(exitCode == 0 ? "TRAY OK" : "TRAY FAILED");
        return exitCode == 0 ? runResult : exitCode;
    }
}
