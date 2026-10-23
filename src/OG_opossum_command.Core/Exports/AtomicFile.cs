using System.Text;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Exports;

/// <summary>
/// Writes a whole file so a reader (OneDrive, the Power Automate flow) never sees half of it: the text goes to a
/// temporary file on the same volume, which is then moved over the target (ADR-050). The move is retried briefly
/// because the OneDrive client or antivirus often holds the previous file for a moment (ADR-054).
/// </summary>
public static partial class AtomicFile
{
    /// <summary>Suffix of the temporary files this class creates; they never match the flow's <c>*.json</c> filters.</summary>
    public const string TempSuffix = ".ogoc-tmp";

    /// <summary>Temporary files younger than this are never swept: a write may be in progress.</summary>
    public static readonly TimeSpan OrphanMinAge = TimeSpan.FromMinutes(10);

    /// <summary>Pauses between move attempts when the target is briefly locked.</summary>
    private static readonly TimeSpan[] MoveRetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically. The temporary file is created in
    /// <paramref name="tempDirectory"/> (default: the target's folder; it must be on the same volume) and is always
    /// removed, also on failure.
    /// </summary>
    /// <param name="overwrite">Replace an existing target; when <c>false</c> an existing target is an <see cref="IOException"/>.</param>
    public static void WriteAllText(string path, string contents, bool overwrite = true, string? tempDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var folder = tempDirectory ?? Path.GetDirectoryName(path) ?? throw new ArgumentException("The path has no folder.", nameof(path));
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            MoveWithRetry(temp, path, overwrite);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Deletes this class's leftover temporary files (a crash or kill between create and move) directly in
    /// <paramref name="folder"/>: only names matching <c>.&lt;name&gt;.&lt;32 hex&gt;.ogoc-tmp</c>, only files last
    /// written at least <see cref="OrphanMinAge"/> before <paramref name="now"/>, never links, never recursing.
    /// Returns how many were deleted; never throws for I/O problems.
    /// </summary>
    public static int DeleteOrphans(string folder, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists || OneDriveBoundary.IsLink(directory))
            {
                return 0;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Directory,
                MatchType = MatchType.Simple,
            };

            var deleted = 0;
            foreach (var file in directory.EnumerateFiles("*" + TempSuffix, options))
            {
                if (!OrphanPattern().IsMatch(file.Name) || OneDriveBoundary.IsLink(file) || now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < OrphanMinAge)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Swept again at the next start.
                }
            }

            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    private static void MoveWithRetry(string temp, string path, bool overwrite)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && attempt < MoveRetryDelays.Length
                && (overwrite || !File.Exists(path)))
            {
                // Sharing violation or access denied while OneDrive uploads or antivirus scans the old file: wait and try again.
                Thread.Sleep(MoveRetryDelays[attempt]);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file has a name no reader picks up, and DeleteOrphans sweeps it at the next start.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [GeneratedRegex(@"^\..+\.[0-9a-f]{32}\.ogoc-tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex OrphanPattern();
}
