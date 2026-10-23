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
/// which the app must never depend on.
/// </remarks>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Environment variable that overrides the data root (used by tests and by the build script).</summary>
    public const string DataDirVariable = "OGOC_DATA_DIR";

    /// <summary>Folder name under <c>%LOCALAPPDATA%</c> when no override is set (ADR-000).</summary>
    public const string DefaultFolderName = "OG_opossum_command";

    private AppPaths(string dataRoot, bool isOverridden)
    {
        DataRoot = dataRoot;
        IsOverridden = isOverridden;
        DatabasePath = Path.Combine(dataRoot, "OG_opossum_command.db");
        WorkflowsDirectory = Path.Combine(dataRoot, "workflows");
        ProfilesDirectory = Path.Combine(dataRoot, "profiles");
        LogsDirectory = Path.Combine(dataRoot, "logs");
    }

    /// <inheritdoc />
    public string DataRoot { get; }

    /// <inheritdoc />
    public bool IsOverridden { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public string WorkflowsDirectory { get; }

    /// <inheritdoc />
    public string ProfilesDirectory { get; }

    /// <inheritdoc />
    public string LogsDirectory { get; }

    /// <summary>Resolves paths from the real process environment.</summary>
    public static AppPaths FromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    /// <summary>
    /// Resolves paths from injected inputs so tests never have to mutate the process environment.
    /// </summary>
    /// <param name="getEnvironmentVariable">Environment lookup; receives <see cref="DataDirVariable"/>.</param>
    /// <param name="localAppDataPath">The per-user <c>%LOCALAPPDATA%</c> folder.</param>
    /// <exception cref="InvalidOperationException">
    /// <c>OGOC_DATA_DIR</c> is set but not an absolute path, or <paramref name="localAppDataPath"/> is blank
    /// while no override is set.
    /// </exception>
    public static AppPaths Resolve(Func<string, string?> getEnvironmentVariable, string localAppDataPath)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var overrideValue = getEnvironmentVariable(DataDirVariable)?.Trim();
        if (!string.IsNullOrEmpty(overrideValue))
        {
            if (!Path.IsPathFullyQualified(overrideValue))
            {
                throw new InvalidOperationException(
                    $"{DataDirVariable} must be an absolute path, but was '{overrideValue}'. " +
                    "The app never resolves data paths against the current directory.");
            }

            return new AppPaths(Path.TrimEndingDirectorySeparator(Path.GetFullPath(overrideValue)), isOverridden: true);
        }

        if (string.IsNullOrWhiteSpace(localAppDataPath))
        {
            throw new InvalidOperationException(
                $"Cannot locate the data folder: {DataDirVariable} is not set and %LOCALAPPDATA% is empty.");
        }

        return new AppPaths(Path.Combine(localAppDataPath, DefaultFolderName), isOverridden: false);
    }
}
