namespace OG_opossum_command.Core.Actions.Launchers;

/// <summary>
/// What to start. The executable is a full path and the arguments are an array; there is never a shell
/// string to escape (spec §07, §09).
/// </summary>
public sealed record ProcessLaunchRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    bool CaptureOutput);

/// <summary>How a launched process ended, with bounded captured output (empty when not captured).</summary>
public sealed record ProcessExit(int ExitCode, string StandardOutput, string StandardError);

/// <summary>A process the launcher started. Disposing never kills it; <see cref="KillTree"/> does.</summary>
public interface ILaunchedProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    /// <summary>Waits for the process to exit. Cancelling the token does not kill the process; call <see cref="KillTree"/>.</summary>
    Task<ProcessExit> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Terminates the process and every descendant. Safe to call after exit.</summary>
    void KillTree();
}

/// <summary>Starts processes. The real implementation is the only place a process is created.</summary>
public interface IProcessLauncher
{
    ILaunchedProcess Start(ProcessLaunchRequest request);
}

/// <summary>Hands an http/https URL to the default browser. The real implementation is the only place that happens.</summary>
public interface IUrlOpener
{
    void Open(Uri url);
}

/// <summary>Opens a folder in the file manager. The real implementation is the only place that happens.</summary>
public interface IFolderOpener
{
    void Open(string folderPath);
}

/// <summary>The three launchers an action registry is wired to.</summary>
public sealed record LauncherSet(IProcessLauncher Processes, IUrlOpener Urls, IFolderOpener Folders)
{
    /// <summary>Real Windows launchers: used only by the interactive app.</summary>
    public static LauncherSet Windows() => new(new WindowsProcessLauncher(), new ShellUrlOpener(), new ShellFolderOpener());
}
