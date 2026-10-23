using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Paths;

/// <summary>
/// The single source of every per-user path the app reads or writes (rule 6).
/// Nothing else in the code base may compute a data path or use the current directory.
/// </summary>
public interface IAppPaths
{
    /// <summary>Root of all per-user data: <c>OGOC_DATA_DIR</c> if set, else <c>%LOCALAPPDATA%\OG_opossum_command</c>.</summary>
    string DataRoot { get; }

    /// <summary>Whether <see cref="DataRoot"/> came from the <c>OGOC_DATA_DIR</c> environment variable.</summary>
    bool IsOverridden { get; }

    /// <summary>SQLite database holding runs, history, and settings (ADR-008): <c>&lt;DataRoot&gt;\data.db</c>.</summary>
    string DatabasePath { get; }

    /// <summary>Parent of the per-profile folders: <c>&lt;DataRoot&gt;\profiles</c>.</summary>
    string ProfilesDirectory { get; }

    /// <summary>Rolling text logs: <c>&lt;DataRoot&gt;\logs</c>.</summary>
    string LogsDirectory { get; }

    /// <summary>
    /// Root of the signed-in OneDrive for Business folder (<c>%OneDriveCommercial%</c>), or <c>null</c> when none is
    /// signed in. The app only ever <em>reads</em> one file under it (ADR-039) and never creates anything there.
    /// </summary>
    string? OneDriveCommercialRoot { get; }

    /// <summary>Root of the signed-in personal OneDrive folder (<c>%OneDriveConsumer%</c>), or <c>null</c>. Read-only to the app.</summary>
    string? OneDriveConsumerRoot { get; }

    /// <summary>A profile's own folder: <c>&lt;DataRoot&gt;\profiles\&lt;Work|Personal&gt;</c>.</summary>
    string GetProfileDirectory(ProfileId profile);

    /// <summary>A profile's hand-editable workflow files (JSON, ADR-008): <c>&lt;DataRoot&gt;\profiles\&lt;Work|Personal&gt;\workflows</c>.</summary>
    string GetProfileWorkflowsDirectory(ProfileId profile);

    /// <summary>A profile's trusted scripts, the only place <c>windows.script.run</c> may read from (ADR-031): <c>&lt;DataRoot&gt;\profiles\&lt;Work|Personal&gt;\scripts</c>.</summary>
    string GetProfileScriptsDirectory(ProfileId profile);

    /// <summary>
    /// Where the profile's calendar file lives unless its <c>profile.json</c> says otherwise (ADR-011, ADR-039):
    /// Work → <c>&lt;OneDriveCommercial&gt;\OG_opossum_command\calendar.json</c> (<c>null</c> when no work OneDrive is
    /// signed in); Personal → <c>null</c> (no calendar unless configured).
    /// </summary>
    string? GetDefaultCalendarFile(ProfileId profile);
}
