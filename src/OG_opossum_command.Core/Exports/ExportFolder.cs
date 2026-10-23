using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Exports;

/// <summary>Whether a profile's exports folder can be used right now.</summary>
public enum ExportFolderState
{
    /// <summary>The folder exists (or was just created inside an existing parent) and may be used.</summary>
    Ready,

    /// <summary>Exports are off for the profile (default, policy, or a broken <c>profile.json</c>).</summary>
    Disabled,

    /// <summary>The folder does not exist and is not created (its parent, such as the OneDrive root, is missing), or a <c>%VAR%</c> is unset.</summary>
    FolderMissing,

    /// <summary>The folder is inside the other context's OneDrive, or is a network or device path. Never used.</summary>
    Refused,
}

/// <summary>Outcome of <see cref="ExportFolder.Resolve"/>.</summary>
/// <param name="FullPath">The normalised folder when <see cref="State"/> is <see cref="ExportFolderState.Ready"/>, else <c>null</c>.</param>
/// <param name="Message">One readable line for Settings.</param>
public sealed record ExportFolderResult(ExportFolderState State, string? FullPath, string Message)
{
    public bool IsReady => State == ExportFolderState.Ready && FullPath is not null;
}

/// <summary>
/// Resolves a profile's exports folder (ADR-050) at the moment it is used: expands <c>%VAR%</c> references, applies the
/// cross-OneDrive guard in both directions, refuses network and device paths, and creates only the last folder level
/// (e.g. <c>OG_opossum_command</c> inside an existing OneDrive root), never a OneDrive root itself.
/// </summary>
public static class ExportFolder
{
    /// <param name="profile">The profile whose <see cref="Profile.Exports"/> folder is resolved.</param>
    /// <param name="paths">Supplies the OneDrive roots for the guard.</param>
    /// <param name="createIfParentExists">Create the folder when only its last level is missing (writers); readers pass <c>false</c>.</param>
    public static ExportFolderResult Resolve(Profile profile, IAppPaths paths, bool createIfParentExists)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(paths);

        var configured = profile.Exports.Folder;
        if (configured is null)
        {
            return new ExportFolderResult(ExportFolderState.Disabled, null, profile.Exports.DisabledReason ?? $"Exports are off for the {profile.DisplayName} profile.");
        }

        var problem = ProfileCatalog.ExportFolderProblem(profile.Id, configured, paths);
        if (problem is not null)
        {
            return new ExportFolderResult(ExportFolderState.Refused, null, problem);
        }

        if (!OneDriveBoundary.TryNormalise(configured, out var full, out var invalid))
        {
            return invalid is null
                ? new ExportFolderResult(ExportFolderState.FolderMissing, null, $"The exports folder '{configured}' is not a full path on this PC right now (is OneDrive signed in?).")
                : new ExportFolderResult(ExportFolderState.Refused, null, $"The exports folder {invalid}.");
        }

        try
        {
            if (Directory.Exists(full))
            {
                return new ExportFolderResult(ExportFolderState.Ready, full, $"Using {full}.");
            }

            var parent = Path.GetDirectoryName(full);
            var isOneDriveRoot = OneDriveBoundary.IsSamePath(full, paths.OneDriveCommercialRoot) || OneDriveBoundary.IsSamePath(full, paths.OneDriveConsumerRoot);
            if (!createIfParentExists || isOneDriveRoot || parent is null || Path.GetPathRoot(full) == full || !Directory.Exists(parent))
            {
                return new ExportFolderResult(ExportFolderState.FolderMissing, null, $"The exports folder {full} does not exist (is OneDrive signed in and syncing?). Nothing is written until it does.");
            }

            Directory.CreateDirectory(full);
            return new ExportFolderResult(ExportFolderState.Ready, full, $"Created {full}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExportFolderResult(ExportFolderState.FolderMissing, null, $"The exports folder {full} cannot be used ({ex.Message}).");
        }
    }
}
