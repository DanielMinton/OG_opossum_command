using System.Runtime.InteropServices;

namespace OG_opossum_command.Core.Maid;

/// <summary>Sends a file or folder to the Recycle Bin. Injected so tests never touch the real one.</summary>
public interface IRecycler
{
    /// <summary>Recycles <paramref name="path"/>; throws <see cref="IOException"/> when Windows refuses.</summary>
    void Recycle(string path);
}

/// <summary>
/// The ONLY place in the maid that removes anything (ADR-067), and a test fails if another maid file tries.
/// Two things live here: removing a folder the maid itself created during a run once it is empty again (undo), and
/// <c>purge</c>, which sends trash older than the hold to the Recycle Bin, never a permanent delete.
/// </summary>
public static class MaidDeletion
{
    /// <summary>Removes <paramref name="folder"/> only if it exists and is completely empty. Never recursive.</summary>
    public static bool RemoveEmptyFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return false;
            }

            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The Windows Recycle Bin through <c>SHFileOperation</c> with undo allowed, no UI, no confirmation (the CLI already demanded one).</summary>
public sealed class WindowsRecycleBin : IRecycler
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    /// <inheritdoc />
    public void Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException("Nothing to recycle.", full);
        }

        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = full + "\0\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted != 0)
        {
            throw new IOException($"Windows did not recycle {full} (code {result}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    // DllImport rather than LibraryImport: the latter needs AllowUnsafeBlocks for the whole project (as ADR-038).
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);
}
