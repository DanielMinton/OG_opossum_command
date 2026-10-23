using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Paths;

/// <summary>
/// The Work/Personal OneDrive boundary (ADR-012) shared by every feature that touches a OneDrive path: the calendar
/// reader, the exports, the alert outbox, and the carryovers reader. Work never touches the personal OneDrive and
/// Personal never touches the work OneDrive, whatever a policy file says.
/// </summary>
public static class OneDriveBoundary
{
    // FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (winnt.h): the OneDrive
    // client would fetch the content when such a file is opened. The app must not cause that (rule 3).
    private const int OnlineOnlyAttributes = 0x1000 | 0x40000 | 0x400000;

    /// <summary>The other context's OneDrive root (which <paramref name="profile"/> may never touch) and a word naming its owner; the root is <c>null</c> when that OneDrive is not signed in.</summary>
    public static (string? Root, string Owner) ForbiddenRoot(ProfileId profile, IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return profile == ProfileId.Work
            ? (paths.OneDriveConsumerRoot, "personal")
            : (paths.OneDriveCommercialRoot, "work");
    }

    /// <summary>
    /// The one normalisation of a configured folder (exports folder, policy checks): refuses a NUL, expands <c>%VAR%</c>
    /// references, requires a fully qualified result, then applies <see cref="Path.GetFullPath(string)"/> and trims a
    /// trailing separator. Returns <c>false</c> with <paramref name="invalid"/> = <c>null</c> when the expanded text is
    /// simply not a full path (an unset <c>%OneDriveConsumer%</c>), and with a sentence fragment such as
    /// <c>"contains a NUL character"</c> when the text can never be a valid path.
    /// </summary>
    public static bool TryNormalise(string configured, out string full, out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(configured);
        full = string.Empty;
        invalid = null;
        if (configured.Contains('\0', StringComparison.Ordinal))
        {
            invalid = "contains a NUL character (\\u0000)";
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (!Path.IsPathFullyQualified(expanded))
        {
            return false;
        }

        try
        {
            full = Canonical(expanded);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            invalid = $"'{configured}' is not a valid path ({ex.Message})";
            return false;
        }
    }

    /// <summary>Whether two full paths name the same folder (case-insensitive, after normalising); <c>false</c> when <paramref name="other"/> is <c>null</c>.</summary>
    public static bool IsSamePath(string fullPath, string? other)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return other is not null && string.Equals(Canonical(fullPath), Canonical(other), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="fullPath"/> is <paramref name="root"/> itself or anything below it (case-insensitive, after normalising).</summary>
    public static bool IsInside(string fullPath, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalisedRoot = Canonical(root);
        var normalisedPath = Canonical(fullPath);
        return string.Equals(normalisedPath, normalisedRoot, StringComparison.OrdinalIgnoreCase)
            || normalisedPath.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Why <paramref name="profile"/> may not use <paramref name="fullPath"/>, or <c>null</c> when it may.
    /// <paramref name="what"/> names the thing for the message (<c>"the exports folder"</c>).
    /// </summary>
    public static string? Refusal(ProfileId profile, string fullPath, IAppPaths paths, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        var (forbidden, owner) = ForbiddenRoot(profile, paths);
        if (forbidden is null || !IsInside(fullPath, forbidden))
        {
            return null;
        }

        return $"Refused: {what} of the {profile.DisplayName()} profile ({fullPath}) is inside the {owner} OneDrive ({forbidden}). Work and personal data never cross (ADR-012).";
    }

    /// <summary>
    /// Whether <paramref name="item"/> is a symbolic link or junction. Plain reparse points are not enough to tell: with
    /// Files On-Demand every OneDrive file and folder carries the reparse attribute, so the link target is what counts.
    /// </summary>
    public static bool IsLink(FileSystemInfo item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.Attributes & FileAttributes.ReparsePoint) != 0 && item.LinkTarget is not null;
    }

    /// <summary>Whether the file is a OneDrive online-only placeholder that would be downloaded if opened.</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) => ((int)attributes & OnlineOnlyAttributes) != 0;
}
