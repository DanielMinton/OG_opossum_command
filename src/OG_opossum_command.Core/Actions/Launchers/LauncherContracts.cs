using OG_opossum_command.Core.Profiles;

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

/// <summary>
/// Brings the app's own dashboard forward with the Today panel for a profile (<c>dashboard.schedule.show</c>).
/// The desktop app implements it on the dispatcher; tests and smoke modes record the call instead.
/// </summary>
public interface IDashboardPresenter
{
    /// <summary>Show the dashboard for <paramref name="profile"/> with its Today panel refreshed from the latest calendar read. May be called from any thread.</summary>
    void ShowSchedule(ProfileId profile);
}

/// <summary>A presenter that does nothing, for hosts without a dashboard (plain unit tests).</summary>
public sealed class NoOpDashboardPresenter : IDashboardPresenter
{
    public static NoOpDashboardPresenter Instance { get; } = new();

    public void ShowSchedule(ProfileId profile)
    {
    }
}

/// <summary>The launchers an action registry is wired to: every side effect an action can have goes through one of these.</summary>
/// <param name="Dashboard">Optional; <c>null</c> means <see cref="NoOpDashboardPresenter"/>.</param>
public sealed record LauncherSet(IProcessLauncher Processes, IUrlOpener Urls, IFolderOpener Folders, IDashboardPresenter? Dashboard = null)
{
    /// <summary>The presenter to use: the configured one, or a no-op.</summary>
    public IDashboardPresenter DashboardOrNoOp => Dashboard ?? NoOpDashboardPresenter.Instance;

    /// <summary>Real Windows launchers plus the app's own dashboard presenter: used only by the interactive app.</summary>
    public static LauncherSet Windows(IDashboardPresenter dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        return new(new WindowsProcessLauncher(), new ShellUrlOpener(), new ShellFolderOpener(), dashboard);
    }
}
