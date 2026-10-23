using System.Diagnostics;
using System.Text;

namespace OG_opossum_command.Core.Actions.Launchers;

/// <summary>Starts a process from an executable path and an argument array (no shell).</summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    /// <summary>Per-stream cap on captured output; anything beyond is dropped with a marker.</summary>
    public const int CaptureLimitChars = 8 * 1024;

    /// <summary>How long output may keep draining after the process exits before the wait returns anyway.</summary>
    public static readonly TimeSpan DrainAfterExit = TimeSpan.FromSeconds(2);

    /// <summary>Stands in for a stream whose pipe a child process still held open when the wait returned.</summary>
    public const string StillOpenMarker = "[output still open: a child process kept the pipe]";

    public ILaunchedProcess Start(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo(request.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = request.CaptureOutput,
            RedirectStandardError = request.CaptureOutput,
            CreateNoWindow = request.CaptureOutput,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{request.ExecutablePath}'.");
        return new LaunchedProcess(process, request.CaptureOutput);
    }

    private sealed class LaunchedProcess : ILaunchedProcess
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly StringBuilder _stdoutSoFar = new();
        private readonly StringBuilder _stderrSoFar = new();

        public LaunchedProcess(Process process, bool captureOutput)
        {
            _process = process;
            // Drain both pipes from the start so the child can never block on a full pipe.
            _stdout = captureOutput ? Observed(ReadBoundedAsync(process.StandardOutput, _stdoutSoFar)) : Task.FromResult(string.Empty);
            _stderr = captureOutput ? Observed(ReadBoundedAsync(process.StandardError, _stderrSoFar)) : Task.FromResult(string.Empty);
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public async Task<ProcessExit> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A grandchild that inherited the pipe (e.g. `start` in a script) keeps it open after the process
                // exits; EOF may never come, so the drain is bounded and still honours cancellation.
                await Task.WhenAll(_stdout, _stderr).WaitAsync(DrainAfterExit, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Return what was captured; the marker says why the rest is missing.
            }

            return new ProcessExit(_process.ExitCode, Captured(_stdout, _stdoutSoFar), Captured(_stderr, _stderrSoFar));
        }

        public void KillTree()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Exited between the check and the kill, or access denied to a descendant: nothing more we can do.
            }
            catch (AggregateException)
            {
                // "Not all processes in the process tree could be terminated" (e.g. an elevated descendant). Swallowed
                // so the caller's cancellation or timeout is still reported as such, not as an action error.
            }
            catch (NotSupportedException)
            {
                // Not a local process; nothing to kill.
            }
        }

        public void Dispose() => _process.Dispose();

        /// <summary>The text a read task captured, or what it had so far plus a marker when the pipe was still open.</summary>
        private static string Captured(Task<string> read, StringBuilder soFar)
        {
            if (read.IsCompletedSuccessfully)
            {
                return read.Result;
            }

            lock (soFar)
            {
                return soFar.Length == 0 ? StillOpenMarker : $"{soFar}{Environment.NewLine}{StillOpenMarker}";
            }
        }

        /// <summary>Observes a fault (e.g. the reader disposed under it) so it never surfaces as an unobserved task exception.</summary>
        private static Task<string> Observed(Task<string> read)
        {
            _ = read.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return read;
        }

        /// <summary>Reads to EOF into <paramref name="buffer"/> (locked per append, so a bounded wait can take what arrived so far).</summary>
        private static async Task<string> ReadBoundedAsync(StreamReader reader, StringBuilder buffer)
        {
            var chunk = new char[1024];
            var dropped = 0;
            int read;
            while ((read = await reader.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
            {
                lock (buffer)
                {
                    var room = CaptureLimitChars - buffer.Length;
                    if (room >= read)
                    {
                        buffer.Append(chunk, 0, read);
                    }
                    else
                    {
                        if (room > 0)
                        {
                            buffer.Append(chunk, 0, room);
                        }

                        dropped += read - Math.Max(room, 0);
                    }
                }
            }

            lock (buffer)
            {
                if (dropped > 0)
                {
                    buffer.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}[output truncated: {dropped} more characters]");
                }

                return buffer.ToString();
            }
        }
    }
}

/// <summary>Hands an http/https URL to the user's default browser via the shell. Nothing else is accepted.</summary>
public sealed class ShellUrlOpener : IUrlOpener
{
    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute http/https URLs may be opened.", nameof(url));
        }

        using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>Opens an existing folder with the shell's default file manager (Explorer unless the user changed it).</summary>
public sealed class ShellFolderOpener : IFolderOpener
{
    public void Open(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Path.IsPathFullyQualified(folderPath) || !Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: '{folderPath}'.");
        }

        using var process = Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true, Verb = "open" });
    }
}
