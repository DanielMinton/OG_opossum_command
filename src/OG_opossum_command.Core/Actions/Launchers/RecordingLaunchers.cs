namespace OG_opossum_command.Core.Actions.Launchers;

/// <summary>
/// Fakes that only record calls (rule 7). Used by tests and by the smoke modes so no browser, folder
/// window, or real application is ever opened during the build.
/// </summary>
public sealed class RecordingLaunchers
{
    public RecordingLaunchers()
    {
        Processes = new RecordingProcessLauncher();
        Urls = new RecordingUrlOpener();
        Folders = new RecordingFolderOpener();
        Set = new LauncherSet(Processes, Urls, Folders);
    }

    public RecordingProcessLauncher Processes { get; }

    public RecordingUrlOpener Urls { get; }

    public RecordingFolderOpener Folders { get; }

    /// <summary>The set to hand to <see cref="ActionRegistry.CreateDefault"/>.</summary>
    public LauncherSet Set { get; }

    /// <summary>Total recorded side effects across all three launchers.</summary>
    public int TotalCalls => Processes.Requests.Count + Urls.Opened.Count + Folders.Opened.Count;
}

/// <summary>Records launch requests; each returns a fake process that exits immediately with code 0 unless <see cref="Behavior"/> says otherwise.</summary>
public sealed class RecordingProcessLauncher : IProcessLauncher
{
    private readonly List<ProcessLaunchRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Overrides how each fake process behaves. Default: exit 0 at once with no output.</summary>
    public Func<ProcessLaunchRequest, ILaunchedProcess>? Behavior { get; set; }

    public IReadOnlyList<ProcessLaunchRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public ILaunchedProcess Start(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Behavior?.Invoke(request) ?? new FakeProcess(exitCode: 0);
    }
}

/// <summary>
/// A pretend process. With <see cref="FakeProcess(int)"/> it has already exited; with <see cref="FakeProcess()"/>
/// it lingers until <see cref="KillTree"/> is called, which is how tests observe cancellation reaching the child.
/// </summary>
public sealed class FakeProcess : ILaunchedProcess
{
    private static int _nextId = 50_000;
    private readonly TaskCompletionSource<ProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A process that lingers until killed.</summary>
    public FakeProcess()
    {
        ProcessId = Interlocked.Increment(ref _nextId);
    }

    /// <summary>A process that has already exited with <paramref name="exitCode"/>.</summary>
    public FakeProcess(int exitCode, string standardOutput = "", string standardError = "")
        : this()
    {
        _exit.SetResult(new ProcessExit(exitCode, standardOutput, standardError));
    }

    public int ProcessId { get; }

    public bool HasExited => _exit.Task.IsCompleted;

    public bool WasKilled { get; private set; }

    public async Task<ProcessExit> WaitForExitAsync(CancellationToken cancellationToken) =>
        await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public void KillTree()
    {
        WasKilled = true;
        _exit.TrySetResult(new ProcessExit(-1, string.Empty, string.Empty));
    }

    public void Dispose()
    {
    }
}

/// <summary>Records URLs instead of opening them.</summary>
public sealed class RecordingUrlOpener : IUrlOpener
{
    private readonly List<Uri> _opened = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<Uri> Opened
    {
        get
        {
            lock (_gate)
            {
                return _opened.ToArray();
            }
        }
    }

    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        lock (_gate)
        {
            _opened.Add(url);
        }
    }
}

/// <summary>Records folder paths instead of opening them.</summary>
public sealed class RecordingFolderOpener : IFolderOpener
{
    private readonly List<string> _opened = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_gate)
            {
                return _opened.ToArray();
            }
        }
    }

    public void Open(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        lock (_gate)
        {
            _opened.Add(folderPath);
        }
    }
}
