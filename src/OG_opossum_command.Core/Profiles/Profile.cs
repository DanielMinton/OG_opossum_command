namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// A context (Work or Personal) with its own workflow folder, history scope, and action allowlist.
/// The engine refuses any step whose action is not in <see cref="AllowedActionIds"/> (ADR-012).
/// </summary>
public sealed class Profile
{
    public Profile(ProfileId id, string workflowsDirectory, IEnumerable<string> allowedActionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowsDirectory);
        ArgumentNullException.ThrowIfNull(allowedActionIds);

        Id = id;
        WorkflowsDirectory = workflowsDirectory;
        AllowedActionIds = new HashSet<string>(allowedActionIds, StringComparer.Ordinal);
    }

    /// <summary>Which profile this is.</summary>
    public ProfileId Id { get; }

    /// <summary>Name shown in the UI (<c>Work</c> / <c>Personal</c>).</summary>
    public string DisplayName => Id.DisplayName();

    /// <summary>Folder holding this profile's workflow JSON files.</summary>
    public string WorkflowsDirectory { get; }

    /// <summary>Stable action IDs this profile may run. Anything else is refused by the engine.</summary>
    public IReadOnlySet<string> AllowedActionIds { get; }

    /// <summary>Whether <paramref name="actionId"/> may run under this profile.</summary>
    public bool IsActionAllowed(string actionId) => AllowedActionIds.Contains(actionId);
}
