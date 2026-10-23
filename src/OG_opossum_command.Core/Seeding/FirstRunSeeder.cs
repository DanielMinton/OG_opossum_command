using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Seeding;

/// <summary>What seeding did for one profile.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="CopiedFiles">Example files copied in (empty when nothing was copied).</param>
/// <param name="Reason">Why nothing was copied, or <c>null</c> when files were copied.</param>
public sealed record ProfileSeedingResult(ProfileId Profile, IReadOnlyList<string> CopiedFiles, string? Reason);

/// <summary>
/// First-run seeding: when a profile's workflows folder is missing or has no <c>*.json</c> files, copy that
/// profile's shipped examples in. User files are never overwritten.
/// </summary>
public static class FirstRunSeeder
{
    /// <summary>Folder next to the executable that holds the shipped examples (<c>examples\workflows\&lt;work|personal&gt;</c>).</summary>
    public static string DefaultExamplesRoot => Path.Combine(AppContext.BaseDirectory, "examples", "workflows");

    /// <summary>Seeds every profile. <paramref name="examplesRoot"/> may be missing, in which case nothing is copied.</summary>
    public static IReadOnlyList<ProfileSeedingResult> Seed(IEnumerable<Profile> profiles, string examplesRoot)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(examplesRoot);

        return profiles.Select(profile => SeedProfile(profile, examplesRoot)).ToList();
    }

    /// <summary>Seeds one profile.</summary>
    public static ProfileSeedingResult SeedProfile(Profile profile, string examplesRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(examplesRoot);

        var target = profile.WorkflowsDirectory;
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.json", SearchOption.TopDirectoryOnly).Any())
        {
            return new ProfileSeedingResult(profile.Id, [], "The profile already has workflow files; nothing was changed.");
        }

        var source = Path.Combine(examplesRoot, profile.Id.ExamplesFolderName());
        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            return new ProfileSeedingResult(profile.Id, [], $"No examples found at {source}; the folder was created empty.");
        }

        Directory.CreateDirectory(target);
        var copied = new List<string>();
        foreach (var file in Directory.GetFiles(source, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                // Second guard: never overwrite, even if a file appeared between the check and the copy.
                continue;
            }

            File.Copy(file, destination, overwrite: false);
            copied.Add(destination);
        }

        return new ProfileSeedingResult(profile.Id, copied, copied.Count == 0 ? $"No example files found under {source}." : null);
    }
}
