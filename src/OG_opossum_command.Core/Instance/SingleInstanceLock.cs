namespace OG_opossum_command.Core.Instance;

/// <summary>
/// One authoritative instance per Windows user session (spec §05 B, ADR-028/036): a session-local named mutex
/// plus a session-local auto-reset event the second instance signals so the first shows its dashboard. No registry,
/// no network, no elevation. Smoke modes never take this lock, so they succeed while the app is running.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary>Base name of the production kernel objects.</summary>
    public const string DefaultName = "OG_opossum_command";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly ManualResetEvent? _stop;
    private readonly Thread? _listener;
    private bool _disposed;

    private SingleInstanceLock(string name, Mutex? mutex, EventWaitHandle? showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        IsOwner = mutex is not null;
        if (showEvent is not null)
        {
            _stop = new ManualResetEvent(false);
            _listener = new Thread(Listen) { IsBackground = true, Name = $"{name} single-instance listener" };
            _listener.Start();
        }
    }

    /// <summary>Raised on a background thread when another instance asked this one to show itself.</summary>
    public event Action? ShowRequested;

    /// <summary>True when this process owns the instance lock; false when another instance already holds it.</summary>
    public bool IsOwner { get; }

    /// <summary>Mutex name for <paramref name="name"/>.</summary>
    public static string MutexNameFor(string name) => $@"Local\{name}.instance";

    /// <summary>Show-request event name for <paramref name="name"/>.</summary>
    public static string ShowEventNameFor(string name) => $@"Local\{name}.show-dashboard";

    /// <summary>
    /// Tries to take the lock. Ownership is tested with a zero wait on the mutex, not with <c>createdNew</c>, so a
    /// handle another process still holds cannot make a free lock look taken. Must be disposed on the same thread.
    /// </summary>
    public static SingleInstanceLock Acquire(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: false, MutexNameFor(name));
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing; we hold it now.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return new SingleInstanceLock(name, null, null);
        }

        var showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventNameFor(name));
        return new SingleInstanceLock(name, mutex, showEvent);
    }

    /// <summary>Asks the owning instance to show its dashboard. Returns false when no owner is listening.</summary>
    public static bool SignalExisting(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ShowEventNameFor(name));
            existing.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_stop is not null)
        {
            _stop.Set();
            _listener?.Join(TimeSpan.FromSeconds(2));
            _stop.Dispose();
        }

        _showEvent?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Disposed from a thread other than the one that acquired it; closing the handle still ends ownership when the process exits.
            }

            _mutex.Dispose();
        }
    }

    private void Listen()
    {
        try
        {
            while (true)
            {
                var signalled = WaitHandle.WaitAny([_stop!, _showEvent!]);
                if (signalled == 0)
                {
                    return;
                }

                ShowRequested?.Invoke();
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed while waiting.
        }
    }
}
