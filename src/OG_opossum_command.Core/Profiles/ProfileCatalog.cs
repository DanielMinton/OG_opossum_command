using System.Text.Json;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// The Work and Personal profiles. Each may carry an optional <c>profile.json</c> in its folder with an
/// <c>allowedActions</c> array and a <c>calendarFile</c> path. Without one, Work may run every built-in action
/// and Personal every action except the <c>m365.*</c> family (ADR-040); a broken policy file falls back to the
/// built-in actions <em>minus</em> <c>m365.*</c> for either profile (fail closed, ADR-025 as revised). The optional
/// <c>exports</c> object controls <c>status.json</c>, the alert outbox, and carryovers (ADR-050); a broken policy file
/// turns all three off.
/// </summary>
public sealed class ProfileCatalog
{
    /// <summary>Optional per-profile policy file name inside <c>profiles\&lt;Name&gt;</c>.</summary>
    public const string PolicyFileName = "profile.json";

    /// <summary>Prefix of the actions that read Microsoft 365 data. Work-only by default and never allowed by a broken policy file.</summary>
    public const string M365ActionPrefix = "m365.";

    private ProfileCatalog(IReadOnlyList<Profile> profiles, IReadOnlyList<WorkflowValidationError> errors)
    {
        Profiles = profiles;
        Errors = errors;
    }

    /// <summary>Both profiles, Work first.</summary>
    public IReadOnlyList<Profile> Profiles { get; }

    /// <summary>Problems in policy files. A profile with a bad policy file falls back to <see cref="FailClosedAllowlist"/>.</summary>
    public IReadOnlyList<WorkflowValidationError> Errors { get; }

    public Profile Get(ProfileId id) => Profiles.First(p => p.Id == id);

    /// <summary>Whether <paramref name="actionId"/> belongs to the Microsoft 365 family.</summary>
    public static bool IsM365Action(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        return actionId.StartsWith(M365ActionPrefix, StringComparison.Ordinal);
    }

    /// <summary>What a profile may run when it has no policy file: Work everything, Personal everything except <c>m365.*</c>.</summary>
    public static IReadOnlyList<string> DefaultAllowlist(ProfileId profile, ActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return profile == ProfileId.Work ? registry.Ids : FailClosedAllowlist(registry);
    }

    /// <summary>What a profile may run when its policy file cannot be trusted: the built-in actions minus the <c>m365.*</c> family.</summary>
    public static IReadOnlyList<string> FailClosedAllowlist(ActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Ids.Where(id => !IsM365Action(id)).ToList();
    }

    /// <summary>Loads both profiles from the data root; pure apart from reading the optional policy files.</summary>
    public static ProfileCatalog Load(IAppPaths paths, ActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registry);

        var profiles = new List<Profile>();
        var errors = new List<WorkflowValidationError>();
        string? workExportsFolder = null;
        foreach (var id in ProfileIdExtensions.All)
        {
            var policyPath = Path.Combine(paths.GetProfileDirectory(id), PolicyFileName);
            var policy = File.Exists(policyPath) ? ReadPolicy(policyPath, id, paths, registry, errors) : ProfilePolicy.Empty;
            var allowed = policy is null
                ? FailClosedAllowlist(registry)
                : policy.AllowedActions ?? DefaultAllowlist(id, registry);
            var calendarFile = policy?.CalendarFile ?? paths.GetDefaultCalendarFile(id);
            var exports = ResolveExports(id, policy, paths);
            if (id == ProfileId.Work)
            {
                workExportsFolder = NormalisedOrNull(exports.Folder);
            }
            else if (workExportsFolder is not null && string.Equals(NormalisedOrNull(exports.Folder), workExportsFolder, StringComparison.OrdinalIgnoreCase))
            {
                // One status.json and one outbox per context: sharing the Work folder would mix the two (ADR-012).
                exports = ExportSettings.Off($"\"exports.folder\" of the {id.DisplayName()} profile is the same folder as the Work profile's, so exports are off for {id.DisplayName()}.");
            }

            profiles.Add(new Profile(id, paths.GetProfileWorkflowsDirectory(id), paths.GetProfileScriptsDirectory(id), allowed, calendarFile, exports));
        }

        return new ProfileCatalog(profiles, errors);
    }

    private static string? NormalisedOrNull(string? folder) =>
        folder is not null && OneDriveBoundary.TryNormalise(folder, out var full, out _) ? full : null;

    /// <summary>The validated content of a policy file; <c>null</c> members mean "use the default".</summary>
    private sealed record ProfilePolicy(IReadOnlyList<string>? AllowedActions, string? CalendarFile, ExportsPolicy? Exports = null)
    {
        public static ProfilePolicy Empty { get; } = new(null, null);
    }

    /// <summary>The <c>exports</c> object as written; <c>null</c> members mean "use the profile's default".</summary>
    private sealed record ExportsPolicy(string? Folder, bool? Status, bool? Alerts, bool? Carryovers);

    /// <summary>
    /// Exports for one profile (ADR-050): a broken policy file turns them off (fail closed); no <c>exports</c> object
    /// means the built-in default (Work on in its OneDrive app folder, Personal off); an <c>exports</c> object overrides
    /// the folder and each switch, with unset switches defaulting to on for Work and off for Personal.
    /// </summary>
    private static ExportSettings ResolveExports(ProfileId id, ProfilePolicy? policy, IAppPaths paths)
    {
        var defaultFolder = paths.GetDefaultExportsFolder(id);
        if (policy is null)
        {
            return ExportSettings.Off($"profiles\\{id.FolderName()}\\{PolicyFileName} could not be read, so exports and alerts are off for the {id.DisplayName()} profile (fail closed). Fix the file and restart.");
        }

        if (policy.Exports is null)
        {
            return ExportSettings.Default(id, defaultFolder);
        }

        var onByDefault = id == ProfileId.Work;
        var exports = policy.Exports;
        var status = exports.Status ?? onByDefault;
        var alerts = exports.Alerts ?? onByDefault;
        var carryovers = exports.Carryovers ?? onByDefault;
        if (!status && !alerts && !carryovers)
        {
            return ExportSettings.Off($"Exports are switched off in profiles\\{id.FolderName()}\\{PolicyFileName}.");
        }

        var folder = exports.Folder ?? defaultFolder;
        if (folder is null)
        {
            return ExportSettings.Off(id == ProfileId.Work
                ? "No work OneDrive is signed in on this PC (%OneDriveCommercial% is not set) and \"exports.folder\" is not set."
                : $"\"exports\" is switched on but \"exports.folder\" is not set in profiles\\{id.FolderName()}\\{PolicyFileName}.");
        }

        return new ExportSettings(folder, status, alerts, carryovers);
    }

    /// <summary>
    /// The optional <c>exports</c> object: <c>{ "folder": "...", "status": true, "alerts": true, "carryovers": true }</c>,
    /// every member optional, nothing else allowed. The folder must be a full local path (no UNC or device path: the
    /// app never writes over the network) and may not be inside the other context's OneDrive.
    /// </summary>
    private static ExportsPolicy? ReadExports(JsonObjectReader root, ProfileId profile, IAppPaths paths)
    {
        var exports = root.OptionalObject("exports");
        if (exports is null)
        {
            return null;
        }

        exports.RejectUnknownProperties("folder", "status", "alerts", "carryovers");
        var status = exports.OptionalBool("status");
        var alerts = exports.OptionalBool("alerts");
        var carryovers = exports.OptionalBool("carryovers");
        var folder = exports.OptionalString("folder");
        if (folder is not null)
        {
            folder = folder.Trim();
            if (folder.Contains('\0', StringComparison.Ordinal))
            {
                exports.AddError("folder", "'folder' contains a NUL character (\\u0000); remove it.");
            }
            else if (!LooksLikeFullPath(folder))
            {
                exports.AddError("folder", $"'folder' must be a full path to a folder (%VAR% references such as %OneDriveConsumer% are allowed), got '{folder}'.");
            }
            else if (ExportFolderProblem(profile, folder, paths) is { } problem)
            {
                exports.AddError("folder", problem);
            }
        }

        return new ExportsPolicy(folder, status, alerts, carryovers);
    }

    /// <summary>
    /// Why a configured or default exports folder may not be used by <paramref name="profile"/>, or <c>null</c>. Checked
    /// when the policy is read and again whenever the folder is used (a <c>%VAR%</c> may expand differently later).
    /// </summary>
    public static string? ExportFolderProblem(ProfileId profile, string folder, IAppPaths paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(paths);
        if (!OneDriveBoundary.TryNormalise(folder, out var full, out var invalid))
        {
            // invalid is null for an unset %VAR% (OneDrive not signed in right now): not an error in the file; the folder is simply unavailable.
            return invalid is null ? null : $"The exports folder {invalid}.";
        }

        // GetFullPath turns // into \\, so this one check covers UNC (\\server), device (\\?\, \\.\) and forward-slash forms.
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return $"The exports folder must be on this PC, not a network or device path (got '{folder}'). The app never writes over the network.";
        }

        return OneDriveBoundary.Refusal(profile, full, paths, "the exports folder");
    }

    private static ProfilePolicy? ReadPolicy(string policyPath, ProfileId profile, IAppPaths paths, ActionRegistry registry, List<WorkflowValidationError> errors)
    {
        var collector = new ValidationErrors(policyPath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(policyPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                collector.Add("$", "The policy file must contain a JSON object.");
            }
            else
            {
                var root = new JsonObjectReader(document.RootElement, "$", collector);
                root.RejectUnknownProperties("allowedActions", "calendarFile", "exports");
                var allowed = root.OptionalStringArray("allowedActions");
                if (allowed is not null)
                {
                    foreach (var id in allowed.Where(id => !registry.Contains(id)))
                    {
                        collector.Add("allowedActions", $"Unknown action '{id}'. Known actions: {string.Join(", ", registry.Ids)}.");
                    }
                }

                var calendarFile = root.OptionalString("calendarFile");
                if (calendarFile is not null && calendarFile.Contains('\0', StringComparison.Ordinal))
                {
                    collector.Add("calendarFile", "'calendarFile' contains a NUL character (\\u0000); remove it.");
                }
                else if (calendarFile is not null && !LooksLikeFullPath(calendarFile))
                {
                    collector.Add("calendarFile", $"'calendarFile' must be a full path to the calendar JSON file (%VAR% references such as %OneDriveCommercial% are allowed), got '{calendarFile}'.");
                }

                var exports = ReadExports(root, profile, paths);

                if (!collector.HasErrors)
                {
                    return new ProfilePolicy(allowed, calendarFile, exports);
                }
            }
        }
        catch (JsonException ex)
        {
            collector.Add("$", $"Not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            collector.Add("$", $"Could not read the file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            collector.Add("$", $"Could not read the file: {ex.Message}");
        }

        errors.AddRange(collector.Items);
        return null;
    }

    /// <summary>A configured path must be absolute once expanded, or start with a <c>%VAR%</c> that may be unset on this PC right now (OneDrive not signed in).</summary>
    private static bool LooksLikeFullPath(string path) =>
        !path.Contains('\0', StringComparison.Ordinal) && (path.StartsWith('%') || OneDriveBoundary.TryNormalise(path, out _, out _));
}
