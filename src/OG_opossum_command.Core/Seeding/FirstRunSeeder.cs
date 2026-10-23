using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Seeding;

/// <summary>What seeding did for one profile.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="CopiedFiles">Example workflow files copied in (empty when nothing was copied).</param>
/// <param name="Reason">Why no workflow was copied, or <c>null</c> when files were copied.</param>
/// <param name="CopiedScripts">Example scripts copied into the profile's trusted scripts folder.</param>
public sealed record ProfileSeedingResult(ProfileId Profile, IReadOnlyList<string> CopiedFiles, string? Reason, IReadOnlyList<string> CopiedScripts);

/// <summary>
/// First-run seeding: when a profile's workflows folder is missing or has no <c>*.json</c> files, copy that
/// profile's shipped examples in; likewise for the trusted scripts folder and the shipped example scripts.
/// User files are never overwritten.
/// </summary>
public static class FirstRunSeeder
{
    /// <summary>Folder next to the executable that holds the shipped example workflows (<c>examples\workflows\&lt;work|personal&gt;</c>).</summary>
    public static string DefaultExamplesRoot => Path.Combine(AppContext.BaseDirectory, "examples", "workflows");

    /// <summary>The shipped example scripts: the <c>scripts</c> folder beside <paramref name="examplesRoot"/>.</summary>
    public static string ScriptsRootBeside(string examplesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examplesRoot);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(examplesRoot)) ?? examplesRoot;
        return Path.Combine(parent, "scripts");
    }

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

        var scripts = CopyIfEmpty(profile.ScriptsDirectory, Path.Combine(ScriptsRootBeside(examplesRoot), profile.Id.ExamplesFolderName()), "*", out _);

        var target = profile.WorkflowsDirectory;
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.json", SearchOption.TopDirectoryOnly).Any())
        {
            return new ProfileSeedingResult(profile.Id, [], "The profile already has workflow files; nothing was changed.", scripts);
        }

        var source = Path.Combine(examplesRoot, profile.Id.ExamplesFolderName());
        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            return new ProfileSeedingResult(profile.Id, [], $"No examples found at {source}; the folder was created empty.", scripts);
        }

        var copied = CopyIfEmpty(target, source, "*.json", out _);
        return new ProfileSeedingResult(profile.Id, copied, copied.Count == 0 ? $"No example files found under {source}." : null, scripts);
    }

    /// <summary>Creates <paramref name="target"/> and, only when it holds no matching file yet, copies <paramref name="pattern"/> files from <paramref name="source"/>. Never overwrites.</summary>
    private static List<string> CopyIfEmpty(string target, string source, string pattern, out bool alreadyHadFiles)
    {
        Directory.CreateDirectory(target);
        alreadyHadFiles = Directory.EnumerateFiles(target, pattern, SearchOption.TopDirectoryOnly).Any();
        var copied = new List<string>();
        if (alreadyHadFiles || !Directory.Exists(source))
        {
            return copied;
        }

        foreach (var file in Directory.GetFiles(source, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
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

        return copied;
    }
}
