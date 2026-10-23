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

    /// <summary>SQLite database holding runs, history, and settings (ADR-008).</summary>
    string DatabasePath { get; }

    /// <summary>Hand-editable workflow definition files (JSON, ADR-008).</summary>
    string WorkflowsDirectory { get; }

    /// <summary>Profile definitions (work / personal) with their policies.</summary>
    string ProfilesDirectory { get; }

    /// <summary>Rolling text logs.</summary>
    string LogsDirectory { get; }
}
