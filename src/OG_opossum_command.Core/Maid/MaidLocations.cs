using System.Text;
using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Maid;

/// <summary>
/// The resolved places the maid works with: the two side roots, the sources, and its own folder. Resolved from
/// <c>rules.json</c> plus an injected environment lookup, so tests never touch a real Downloads or OneDrive folder.
/// Pure: nothing is created.
/// </summary>
public sealed class MaidLocations
{
    /// <summary>Name of the folder junk is moved into, under the source it came from. Never deleted by the maid.</summary>
    public const string TrashFolderName = "_Trash Review";

    private MaidLocations(string maidDirectory, string? workRoot, string? personalRoot, IReadOnlyList<MaidSource> sources, IReadOnlyList<string> problems)
    {
        MaidDirectory = maidDirectory;
        WorkRoot = workRoot;
        PersonalRoot = personalRoot;
        Sources = sources;
        Problems = problems;
    }

    /// <summary>The maid's own folder (<see cref="IAppPaths.MaidDirectory"/>).</summary>
    public string MaidDirectory { get; }

    /// <summary>The work root (the work OneDrive), or <c>null</c> when none is signed in.</summary>
    public string? WorkRoot { get; }

    /// <summary>The personal root, or <c>null</c> when it can't be resolved.</summary>
    public string? PersonalRoot { get; }

    /// <summary>Folders to read, existing or not.</summary>
    public IReadOnlyList<MaidSource> Sources { get; }

    /// <summary>Configured places that could not be used, in words.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Plans folder.</summary>
    public string PlansDirectory => Path.Combine(MaidDirectory, "plans");

    /// <summary>Run journals folder.</summary>
    public string RunsDirectory => Path.Combine(MaidDirectory, "runs");

    /// <summary>Reasoning bundles folder.</summary>
    public string BundlesDirectory => Path.Combine(MaidDirectory, "bundles");

    /// <summary>Tag CSVs and the personal tag index.</summary>
    public string ExportsDirectory => Path.Combine(MaidDirectory, "exports");

    /// <summary>The root of <paramref name="side"/>, or <c>null</c>.</summary>
    public string? RootOf(MaidSide side) => side switch
    {
        MaidSide.Work => WorkRoot,
        MaidSide.Personal => PersonalRoot,
        _ => null,
    };

    /// <summary>Which side a path is on, by where it lives.</summary>
    public MaidSide SideOfPath(string fullPath)
    {
        if (WorkRoot is not null && OneDriveBoundary.IsInside(fullPath, WorkRoot))
        {
            return MaidSide.Work;
        }

        return PersonalRoot is not null && OneDriveBoundary.IsInside(fullPath, PersonalRoot) ? MaidSide.Personal : MaidSide.Unassigned;
    }

    /// <summary>Resolves the places from <paramref name="settings"/>.</summary>
    /// <param name="maidDirectory">The maid folder (<see cref="IAppPaths.MaidDirectory"/>).</param>
    /// <param name="settings">The rules file.</param>
    /// <param name="getEnvironmentVariable">Lookup for <c>%VAR%</c> references (injected so tests stay hermetic).</param>
    /// <param name="sourceOverrides">Folders given on the command line; replace the configured sources when non-empty.</param>
    public static MaidLocations Resolve(string maidDirectory, MaidSettings settings, Func<string, string?> getEnvironmentVariable, IReadOnlyList<string>? sourceOverrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maidDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var problems = new List<string>();
        var work = Full(settings.WorkRoot, getEnvironmentVariable, "workRoot", problems);
        var personal = Full(settings.PersonalRoot, getEnvironmentVariable, "personalRoot", problems);
        if (work is not null && personal is not null && (OneDriveBoundary.IsInside(personal, work) || OneDriveBoundary.IsInside(work, personal)))
        {
            problems.Add("The personal root and the work root overlap, so neither is used (ADR-012).");
            work = null;
            personal = null;
        }

        var sources = new List<MaidSource>();
        var probe = new MaidLocations(maidDirectory, work, personal, [], []);
        if (sourceOverrides is { Count: > 0 })
        {
            foreach (var raw in sourceOverrides)
            {
                if (Full(raw, getEnvironmentVariable, "--source", problems) is { } path)
                {
                    sources.Add(new MaidSource(path, probe.SideOfPath(path), IsInbox(path, work)));
                }
            }
        }
        else
        {
            foreach (var source in settings.Sources)
            {
                if (Full(source.Path, getEnvironmentVariable, "sources", problems) is not { } path)
                {
                    continue;
                }

                var bySide = probe.SideOfPath(path);
                if (bySide != MaidSide.Unassigned && source.Side != MaidSide.Unassigned && bySide != source.Side)
                {
                    problems.Add($"Source {path} is marked {source.Side} but lives on the {bySide} side; skipped (ADR-012).");
                    continue;
                }

                sources.Add(new MaidSource(path, bySide == MaidSide.Unassigned ? source.Side : bySide, IsInbox(path, work)));
            }
        }

        return new MaidLocations(maidDirectory, work, personal, sources, problems);
    }

    /// <summary>Resolves from the real environment.</summary>
    public static MaidLocations FromEnvironment(IAppPaths paths, MaidSettings settings, IReadOnlyList<string>? sourceOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Resolve(paths.MaidDirectory, settings, Environment.GetEnvironmentVariable, sourceOverrides);
    }

    private static bool IsInbox(string path, string? workRoot) =>
        workRoot is not null && OneDriveBoundary.IsSamePath(path, Path.Combine(workRoot, "_Inbox"));

    private static string? Full(string configured, Func<string, string?> env, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var expanded = Expand(configured, env, out var missing);
        if (missing is not null)
        {
            problems.Add($"{what}: '{configured}' needs %{missing}%, which is not set; skipped.");
            return null;
        }

        if (expanded.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(expanded) || expanded.StartsWith(@"\\", StringComparison.Ordinal))
        {
            problems.Add($"{what}: '{configured}' is not a full local path; skipped.");
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    /// <summary>Expands <c>%NAME%</c> with the injected lookup; reports the first unset name.</summary>
    internal static string Expand(string text, Func<string, string?> env, out string? missing)
    {
        missing = null;
        var result = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            var end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (start < 0 || end < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }

            result.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            var value = env(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                missing ??= name;
            }
            else
            {
                result.Append(value.Trim());
            }

            i = end + 1;
        }

        return result.ToString();
    }
}
