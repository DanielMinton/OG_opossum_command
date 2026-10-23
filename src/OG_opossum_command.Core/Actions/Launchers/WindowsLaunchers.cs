using System.Diagnostics;
using System.Text;

namespace OG_opossum_command.Core.Actions.Launchers;

/// <summary>Starts a process from an executable path and an argument array (no shell).</summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    /// <summary>Per-stream cap on captured output; anything beyond is dropped with a marker.</summary>
    public const int CaptureLimitChars = 8 * 1024;

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

        public LaunchedProcess(Process process, bool captureOutput)
        {
            _process = process;
            // Drain both pipes from the start so the child can never block on a full pipe.
            _stdout = captureOutput ? ReadBoundedAsync(process.StandardOutput) : Task.FromResult(string.Empty);
            _stderr = captureOutput ? ReadBoundedAsync(process.StandardError) : Task.FromResult(string.Empty);
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public async Task<ProcessExit> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await _stdout.ConfigureAwait(false);
            var stderr = await _stderr.ConfigureAwait(false);
            return new ProcessExit(_process.ExitCode, stdout, stderr);
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
        }

        public void Dispose() => _process.Dispose();

        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var buffer = new StringBuilder();
            var chunk = new char[1024];
            var dropped = 0;
            int read;
            while ((read = await reader.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
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

            if (dropped > 0)
            {
                buffer.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}[output truncated: {dropped} more characters]");
            }

            return buffer.ToString();
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
