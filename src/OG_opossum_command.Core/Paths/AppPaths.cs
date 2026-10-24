using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Paths;

/// <summary>
/// Resolves every per-user path from one root (rule 6). Pure: resolving never creates anything on disk.
/// </summary>
/// <remarks>
/// Resolution order for <see cref="DataRoot"/>:
/// <list type="number">
///   <item><c>OGOC_DATA_DIR</c>, when set to a non-blank, absolute path.</item>
///   <item><c>%LOCALAPPDATA%\OG_opossum_command</c> otherwise.</item>
/// </list>
/// A relative <c>OGOC_DATA_DIR</c> is rejected rather than resolved against the current directory,
/// which the app must never depend on. The OneDrive roots come from the same injected environment lookup
/// (<c>%OneDriveCommercial%</c>, <c>%OneDriveConsumer%</c>) and are <c>null</c> unless set to an absolute path,
/// so tests and the smoke modes never resolve a real OneDrive path by accident (ADR-039).
/// </remarks>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Environment variable that overrides the data root (used by tests and by the build script).</summary>
    public const string DataDirVariable = "OGOC_DATA_DIR";

    /// <summary>Environment variable Windows sets to the signed-in OneDrive for Business folder.</summary>
    public const string OneDriveCommercialVariable = "OneDriveCommercial";

    /// <summary>Environment variable Windows sets to the signed-in personal OneDrive folder.</summary>
    public const string OneDriveConsumerVariable = "OneDriveConsumer";

    /// <summary>Folder name under <c>%LOCALAPPDATA%</c> when no override is set (ADR-000); also the folder name under OneDrive for the calendar file.</summary>
    public const string DefaultFolderName = "OG_opossum_command";

    /// <summary>File name of the SQLite database under the data root.</summary>
    public const string DatabaseFileName = "data.db";

    /// <summary>File name the Power Automate flow writes under <c>&lt;OneDrive&gt;\OG_opossum_command</c> (ADR-011).</summary>
    public const string CalendarFileName = "calendar.json";

    /// <summary>File name of the status export the app writes into the profile's exports folder (ADR-050).</summary>
    public const string StatusFileName = "status.json";

    /// <summary>File name of the carryovers file the Power Automate flow writes into the profile's exports folder (ADR-051).</summary>
    public const string CarryoversFileName = "tomorrow.json";

    /// <summary>Subfolder of the exports folder that holds alert files for the flow (ADR-052).</summary>
    public const string OutboxFolderName = "outbox";

    /// <summary>Folder under the data root that holds Opossum Maid's taxonomy, rules, learned rules, plans and run journals (ADR-058).</summary>
    public const string MaidFolderName = "maid";

    private AppPaths(string dataRoot, bool isOverridden, string? oneDriveCommercialRoot, string? oneDriveConsumerRoot)
    {
        DataRoot = dataRoot;
        IsOverridden = isOverridden;
        DatabasePath = Path.Combine(dataRoot, DatabaseFileName);
        ProfilesDirectory = Path.Combine(dataRoot, "profiles");
        LogsDirectory = Path.Combine(dataRoot, "logs");
        MaidDirectory = Path.Combine(dataRoot, MaidFolderName);
        OneDriveCommercialRoot = oneDriveCommercialRoot;
        OneDriveConsumerRoot = oneDriveConsumerRoot;
    }

    /// <inheritdoc />
    public string DataRoot { get; }

    /// <inheritdoc />
    public bool IsOverridden { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public string ProfilesDirectory { get; }

    /// <inheritdoc />
    public string LogsDirectory { get; }

    /// <inheritdoc />
    public string MaidDirectory { get; }

    /// <inheritdoc />
    public string? OneDriveCommercialRoot { get; }

    /// <inheritdoc />
    public string? OneDriveConsumerRoot { get; }

    /// <inheritdoc />
    public string GetProfileDirectory(ProfileId profile) => Path.Combine(ProfilesDirectory, profile.FolderName());

    /// <inheritdoc />
    public string GetProfileWorkflowsDirectory(ProfileId profile) => Path.Combine(GetProfileDirectory(profile), "workflows");

    /// <inheritdoc />
    public string GetProfileScriptsDirectory(ProfileId profile) => Path.Combine(GetProfileDirectory(profile), "scripts");

    /// <inheritdoc />
    public string? GetDefaultCalendarFile(ProfileId profile) =>
        GetDefaultExportsFolder(profile) is { } folder ? Path.Combine(folder, CalendarFileName) : null;

    /// <inheritdoc />
    public string? GetDefaultExportsFolder(ProfileId profile) =>
        profile == ProfileId.Work && OneDriveCommercialRoot is not null
            ? Path.Combine(OneDriveCommercialRoot, DefaultFolderName)
            : null;

    /// <summary>Resolves paths from the real process environment.</summary>
    public static AppPaths FromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    /// <summary>
    /// Resolves paths rooted at an explicit absolute folder (smoke modes and tests). No OneDrive root is known, so no
    /// profile has a default calendar file.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="dataRoot"/> is not an absolute path.</exception>
    public static AppPaths ForRoot(string dataRoot) => Resolve(name => name == DataDirVariable ? dataRoot : null, localAppDataPath: string.Empty);

    /// <summary>
    /// Resolves paths from injected inputs so tests never have to mutate the process environment.
    /// </summary>
    /// <param name="getEnvironmentVariable">Environment lookup; receives <see cref="DataDirVariable"/>, <see cref="OneDriveCommercialVariable"/>, and <see cref="OneDriveConsumerVariable"/>.</param>
    /// <param name="localAppDataPath">The per-user <c>%LOCALAPPDATA%</c> folder.</param>
    /// <exception cref="InvalidOperationException">
    /// <c>OGOC_DATA_DIR</c> is set but not an absolute path, or <paramref name="localAppDataPath"/> is blank
    /// while no override is set.
    /// </exception>
    public static AppPaths Resolve(Func<string, string?> getEnvironmentVariable, string localAppDataPath)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var commercial = AbsoluteOrNull(getEnvironmentVariable(OneDriveCommercialVariable));
        var consumer = AbsoluteOrNull(getEnvironmentVariable(OneDriveConsumerVariable));

        var overrideValue = getEnvironmentVariable(DataDirVariable)?.Trim();
        if (!string.IsNullOrEmpty(overrideValue))
        {
            if (!Path.IsPathFullyQualified(overrideValue))
            {
                throw new InvalidOperationException(
                    $"{DataDirVariable} must be an absolute path, but was '{overrideValue}'. " +
                    "The app never resolves data paths against the current directory.");
            }

            return new AppPaths(Path.TrimEndingDirectorySeparator(Path.GetFullPath(overrideValue)), isOverridden: true, commercial, consumer);
        }

        if (string.IsNullOrWhiteSpace(localAppDataPath))
        {
            throw new InvalidOperationException(
                $"Cannot locate the data folder: {DataDirVariable} is not set and %LOCALAPPDATA% is empty.");
        }

        return new AppPaths(Path.Combine(localAppDataPath, DefaultFolderName), isOverridden: false, commercial, consumer);
    }

    /// <summary>A trimmed absolute folder path, or <c>null</c> for unset, blank, or relative values (never an error: OneDrive is optional).</summary>
    private static string? AbsoluteOrNull(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }
}
