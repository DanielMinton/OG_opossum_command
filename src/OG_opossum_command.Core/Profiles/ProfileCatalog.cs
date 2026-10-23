using System.Text.Json;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// The Work and Personal profiles. Each may carry an optional <c>profile.json</c> in its folder with an
/// <c>allowedActions</c> array and a <c>calendarFile</c> path. Without one, Work may run every built-in action
/// and Personal every action except the <c>m365.*</c> family (ADR-040); a broken policy file falls back to the
/// built-in actions <em>minus</em> <c>m365.*</c> for either profile (fail closed, ADR-025 as revised).
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
        foreach (var id in ProfileIdExtensions.All)
        {
            var policyPath = Path.Combine(paths.GetProfileDirectory(id), PolicyFileName);
            var policy = File.Exists(policyPath) ? ReadPolicy(policyPath, registry, errors) : ProfilePolicy.Empty;
            var allowed = policy is null
                ? FailClosedAllowlist(registry)
                : policy.AllowedActions ?? DefaultAllowlist(id, registry);
            var calendarFile = policy?.CalendarFile ?? paths.GetDefaultCalendarFile(id);
            profiles.Add(new Profile(id, paths.GetProfileWorkflowsDirectory(id), paths.GetProfileScriptsDirectory(id), allowed, calendarFile));
        }

        return new ProfileCatalog(profiles, errors);
    }

    /// <summary>The validated content of a policy file; <c>null</c> members mean "use the default".</summary>
    private sealed record ProfilePolicy(IReadOnlyList<string>? AllowedActions, string? CalendarFile)
    {
        public static ProfilePolicy Empty { get; } = new(null, null);
    }

    private static ProfilePolicy? ReadPolicy(string policyPath, ActionRegistry registry, List<WorkflowValidationError> errors)
    {
        var collector = new ValidationErrors(policyPath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(policyPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                collector.Add("$", "The policy file must contain a JSON object.");
            }
            else
            {
                var root = new JsonObjectReader(document.RootElement, "$", collector);
                root.RejectUnknownProperties("allowedActions", "calendarFile");
                var allowed = root.OptionalStringArray("allowedActions");
                if (allowed is not null)
                {
                    foreach (var id in allowed.Where(id => !registry.Contains(id)))
                    {
                        collector.Add("allowedActions", $"Unknown action '{id}'. Known actions: {string.Join(", ", registry.Ids)}.");
                    }
                }

                var calendarFile = root.OptionalString("calendarFile");
                if (calendarFile is not null && !LooksLikeFullPath(calendarFile))
                {
                    collector.Add("calendarFile", $"'calendarFile' must be a full path to the calendar JSON file (%VAR% references such as %OneDriveCommercial% are allowed), got '{calendarFile}'.");
                }

                if (!collector.HasErrors)
                {
                    return new ProfilePolicy(allowed, calendarFile);
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
        path.StartsWith('%') || Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(path));
}
