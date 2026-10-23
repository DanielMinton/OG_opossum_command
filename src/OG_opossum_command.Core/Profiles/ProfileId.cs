namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// The two contexts the app keeps strictly apart (ADR-012). Workflow files name a profile with the
/// lower-case <see cref="ProfileIdExtensions.ToJsonValue"/> form; folders use <see cref="ProfileIdExtensions.FolderName"/>.
/// </summary>
public enum ProfileId
{
    /// <summary>Work context: employer destinations only.</summary>
    Work,

    /// <summary>Personal context: personal destinations only.</summary>
    Personal,
}

/// <summary>String forms of a <see cref="ProfileId"/>.</summary>
public static class ProfileIdExtensions
{
    /// <summary>All profiles, in display order.</summary>
    public static readonly IReadOnlyList<ProfileId> All = [ProfileId.Work, ProfileId.Personal];

    /// <summary>The value used in workflow JSON (<c>"work"</c> / <c>"personal"</c>) and in the database.</summary>
    public static string ToJsonValue(this ProfileId profile) => profile switch
    {
        ProfileId.Work => "work",
        ProfileId.Personal => "personal",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown profile."),
    };

    /// <summary>The folder name under <c>profiles\</c> and under <c>examples\workflows\</c> (<c>Work</c> / <c>Personal</c>).</summary>
    public static string FolderName(this ProfileId profile) => profile switch
    {
        ProfileId.Work => "Work",
        ProfileId.Personal => "Personal",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown profile."),
    };

    /// <summary>The folder name used for the shipped examples (<c>work</c> / <c>personal</c>).</summary>
    public static string ExamplesFolderName(this ProfileId profile) => profile.ToJsonValue();

    /// <summary>Human-readable name shown in the UI.</summary>
    public static string DisplayName(this ProfileId profile) => profile.FolderName();

    /// <summary>Parses the JSON value; case-insensitive.</summary>
    public static bool TryParseJsonValue(string? value, out ProfileId profile)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToJsonValue(), value, StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                return true;
            }
        }

        profile = default;
        return false;
    }
}
