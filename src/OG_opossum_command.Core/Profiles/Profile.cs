namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// A context (Work or Personal) with its own workflow folder, trusted scripts folder, history scope, action
/// allowlist, and optional calendar file. The engine refuses any step whose action is not in
/// <see cref="AllowedActionIds"/> (ADR-012).
/// </summary>
public sealed class Profile
{
    public Profile(ProfileId id, string workflowsDirectory, string scriptsDirectory, IEnumerable<string> allowedActionIds, string? calendarFilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        ArgumentNullException.ThrowIfNull(allowedActionIds);

        Id = id;
        WorkflowsDirectory = workflowsDirectory;
        ScriptsDirectory = scriptsDirectory;
        AllowedActionIds = new HashSet<string>(allowedActionIds, StringComparer.Ordinal);
        CalendarFilePath = string.IsNullOrWhiteSpace(calendarFilePath) ? null : calendarFilePath.Trim();
    }

    /// <summary>Which profile this is.</summary>
    public ProfileId Id { get; }

    /// <summary>Name shown in the UI (<c>Work</c> / <c>Personal</c>).</summary>
    public string DisplayName => Id.DisplayName();

    /// <summary>Folder holding this profile's workflow JSON files.</summary>
    public string WorkflowsDirectory { get; }

    /// <summary>Folder holding this profile's trusted scripts; <c>windows.script.run</c> never reads anywhere else (ADR-031).</summary>
    public string ScriptsDirectory { get; }

    /// <summary>Stable action IDs this profile may run. Anything else is refused by the engine.</summary>
    public IReadOnlySet<string> AllowedActionIds { get; }

    /// <summary>
    /// The JSON schedule file <c>m365.calendar.readFile</c> reads for this profile (ADR-011, ADR-039), possibly with
    /// <c>%VAR%</c> references that are expanded at read time; <c>null</c> when the profile has no calendar.
    /// </summary>
    public string? CalendarFilePath { get; }

    /// <summary>Whether <paramref name="actionId"/> may run under this profile.</summary>
    public bool IsActionAllowed(string actionId) => AllowedActionIds.Contains(actionId);
}
