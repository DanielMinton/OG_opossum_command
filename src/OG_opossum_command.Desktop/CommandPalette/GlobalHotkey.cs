using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OG_opossum_command.Desktop.CommandPalette;

/// <summary>
/// A system-wide hotkey registered on the main window's HWND via <c>RegisterHotKey</c> (user32, no elevation).
/// Failure to register (the combination is taken) never throws: see <see cref="IsRegistered"/> and <see cref="FailureReason"/>.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyId = 0x0506;

    private readonly Window _window;
    private readonly Action _pressed;
    private HwndSource? _source;
    private bool _disposed;

    public GlobalHotkey(Window window, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(pressed);
        _window = window;
        _pressed = pressed;
    }

    /// <summary>The gesture currently registered, or the one that failed.</summary>
    public HotkeyGesture? Gesture { get; private set; }

    public bool IsRegistered { get; private set; }

    /// <summary>Why registration failed, in plain words; <c>null</c> when registered.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Registers <paramref name="gesture"/>, replacing any earlier one. Returns whether it succeeded.</summary>
    public bool Register(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister();
        Gesture = gesture;

        var handle = new WindowInteropHelper(_window).EnsureHandle();
        _source ??= HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            FailureReason = "The window has no handle yet, so the hotkey could not be registered.";
            return false;
        }

        _source.AddHook(WndProc);
        var modifiers = ModNoRepeat
            | (gesture.Modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
            | (gesture.Modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
            | (gesture.Modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
            | (gesture.Modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);

        if (RegisterHotKey(handle, HotkeyId, modifiers, virtualKey))
        {
            IsRegistered = true;
            FailureReason = null;
            return true;
        }

        var code = Marshal.GetLastWin32Error();
        IsRegistered = false;
        FailureReason = code == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Hotkey {gesture} is in use by another app; change it in Settings."
            : $"Hotkey {gesture} could not be registered (Windows error {code}); change it in Settings.";
        return false;
    }

    /// <summary>Removes the registration (safe to call repeatedly).</summary>
    public void Unregister()
    {
        if (IsRegistered && _source is not null)
        {
            _ = UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _source?.RemoveHook(WndProc);
        IsRegistered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _pressed();
        }

        return IntPtr.Zero;
    }

    // DllImport rather than LibraryImport: the latter needs AllowUnsafeBlocks for the whole project.
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
