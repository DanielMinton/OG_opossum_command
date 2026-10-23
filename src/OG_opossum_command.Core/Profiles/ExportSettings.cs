namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// What a profile exports through its OneDrive app folder (ADR-050..052): <c>status.json</c>, the alert outbox, and
/// reading <c>tomorrow.json</c>. Comes from the optional <c>exports</c> object of <c>profile.json</c>; Work defaults
/// to everything on in <c>%OneDriveCommercial%\OG_opossum_command</c>, Personal to everything off, and a broken
/// <c>profile.json</c> turns everything off (fail closed).
/// </summary>
/// <param name="Folder">The folder, possibly with <c>%VAR%</c> references expanded at use time; <c>null</c> when there is none.</param>
/// <param name="Status">Write <c>status.json</c>.</param>
/// <param name="Alerts">Write alert files into <c>outbox\</c>.</param>
/// <param name="Carryovers">Read <c>tomorrow.json</c>.</param>
/// <param name="DisabledReason">Why everything is off when it is (for Settings), else <c>null</c>.</param>
public sealed record ExportSettings(string? Folder, bool Status, bool Alerts, bool Carryovers, string? DisabledReason = null)
{
    /// <summary>Everything off, with the reason shown in Settings.</summary>
    public static ExportSettings Off(string reason) => new(null, false, false, false, reason);

    /// <summary>The built-in default for a profile with no <c>exports</c> object: Work on (when its folder resolves), Personal off.</summary>
    public static ExportSettings Default(ProfileId profile, string? defaultFolder) =>
        profile == ProfileId.Work
            ? defaultFolder is null
                ? Off("No work OneDrive is signed in on this PC (%OneDriveCommercial% is not set).")
                : new ExportSettings(defaultFolder, true, true, true)
            : Off("Off by default for the Personal profile; enable it in profiles\\Personal\\profile.json (\"exports\").");
}
