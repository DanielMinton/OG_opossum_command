using System.Text.Json;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Profiles;

/// <summary>
/// The Work and Personal profiles. Each may carry an optional <c>profile.json</c> in its folder with an
/// <c>allowedActions</c> array; without one, the profile may run every built-in action.
/// </summary>
public sealed class ProfileCatalog
{
    /// <summary>Optional per-profile policy file name inside <c>profiles\&lt;Name&gt;</c>.</summary>
    public const string PolicyFileName = "profile.json";

    private ProfileCatalog(IReadOnlyList<Profile> profiles, IReadOnlyList<WorkflowValidationError> errors)
    {
        Profiles = profiles;
        Errors = errors;
    }

    /// <summary>Both profiles, Work first.</summary>
    public IReadOnlyList<Profile> Profiles { get; }

    /// <summary>Problems in policy files. A profile with a bad policy file falls back to the built-in default allowlist.</summary>
    public IReadOnlyList<WorkflowValidationError> Errors { get; }

    public Profile Get(ProfileId id) => Profiles.First(p => p.Id == id);

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
            var allowed = File.Exists(policyPath) ? ReadAllowlist(policyPath, registry, errors) : null;
            profiles.Add(new Profile(id, paths.GetProfileWorkflowsDirectory(id), allowed ?? registry.Ids));
        }

        return new ProfileCatalog(profiles, errors);
    }

    private static IReadOnlyList<string>? ReadAllowlist(string policyPath, ActionRegistry registry, List<WorkflowValidationError> errors)
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
                root.RejectUnknownProperties("allowedActions");
                var allowed = root.OptionalStringArray("allowedActions");
                if (allowed is not null)
                {
                    foreach (var id in allowed.Where(id => !registry.Contains(id)))
                    {
                        collector.Add("allowedActions", $"Unknown action '{id}'. Known actions: {string.Join(", ", registry.Ids)}.");
                    }
                }

                if (!collector.HasErrors)
                {
                    return allowed ?? registry.Ids;
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

        errors.AddRange(collector.Items);
        return null;
    }
}
