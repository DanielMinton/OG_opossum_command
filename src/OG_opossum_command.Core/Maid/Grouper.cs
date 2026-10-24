namespace OG_opossum_command.Core.Maid;

/// <summary>A set of files that belong together and move together.</summary>
/// <param name="Id">Stable id within the plan (<c>g01</c>).</param>
/// <param name="Label">What ties them together, in words.</param>
/// <param name="FromOriginFolder">The files already shared a folder; that folder name may become a subfolder.</param>
/// <param name="Members">The files.</param>
public sealed record FileGroup(string Id, string Label, bool FromOriginFolder, IReadOnlyList<ScannedFile> Members);

/// <summary>
/// Finds files that belong together (ADR-064): the same folder of origin, versions of one name, a saved page and its
/// assets, the same named entity, or the same project words. Union-find, so chains join into one group.
/// </summary>
public static class Grouper
{
    /// <summary>Groups of two or more files.</summary>
    public static IReadOnlyList<FileGroup> Group(IReadOnlyList<(ScannedFile File, Classification Class)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var parent = Enumerable.Range(0, items.Count).ToArray();
        var why = new Dictionary<int, (string Label, int Strength, bool Origin)>();

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Union(int a, int b, string label, int strength, bool origin)
        {
            var ra = Find(a);
            var rb = Find(b);
            var best = new[] { why.GetValueOrDefault(ra), why.GetValueOrDefault(rb), (label, strength, origin) }.MaxBy(w => w.Item2);
            if (ra != rb)
            {
                parent[rb] = ra;
            }

            why[ra] = best;
        }

        void JoinBy(Func<int, string?> key, Func<string, string> label, int strength, bool origin)
        {
            foreach (var bucket in Enumerable.Range(0, items.Count)
                .Where(i => !items[i].Class.IsSecurity)
                .Select(i => (i, k: key(i)))
                .Where(x => x.k is not null)
                .GroupBy(x => x.k!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2))
            {
                var first = bucket.First().i;
                foreach (var (i, _) in bucket.Skip(1))
                {
                    Union(first, i, label(bucket.Key[(bucket.Key.IndexOf('|', StringComparison.Ordinal) + 1)..]), strength, origin);
                }
            }
        }

        string Scope(int i) => items[i].File.Source.Path.ToUpperInvariant();

        // Strongest first: the label of the strongest reason wins.
        JoinBy(i => items[i].File.OriginFolder is { Length: > 0 } folder ? $"{Scope(i)}|{folder.Split(Path.DirectorySeparatorChar)[0]}" : null, k => k, 4, true);
        JoinBy(i => SavedPageKey(items[i].File) is { } page ? $"{Scope(i)}|{page}" : null, k => Path.GetFileName(k), 3, false);
        JoinBy(i => NameCleaner.BaseOf(Path.GetFileNameWithoutExtension(items[i].File.Name)) is { Length: >= 4 } b ? $"{Scope(i)}|{b}" : null, NameCleaner.TitleCase, 2, false);
        foreach (var entity in items.SelectMany(x => x.Class.Entities.Where(e => !e.Contains('.', StringComparison.Ordinal))).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            JoinBy(i => items[i].Class.Entities.Contains(entity, StringComparer.OrdinalIgnoreCase) ? $"{SideKey(items[i].Class)}|{entity}" : null, k => k, 1, false);
        }

        return Enumerable.Range(0, items.Count)
            .GroupBy(Find)
            .Where(g => g.Count() >= 2)
            .Select((g, n) =>
            {
                var (label, _, origin) = why.GetValueOrDefault(g.Key);
                return new FileGroup($"g{n + 1:00}", label ?? "related", origin, g.Select(i => items[i].File).ToList());
            })
            .ToList();
    }

    private static string SideKey(Classification c) => (c.Side == MaidSide.Unassigned ? c.LikelySide : c.Side).ToString();

    /// <summary>A saved page and its <c>_files</c> folder share the page's stem.</summary>
    private static string? SavedPageKey(ScannedFile file)
    {
        if (file.IsFolder)
        {
            return Path.Combine(Path.GetDirectoryName(file.FullPath)!, file.Name[..^"_files".Length]);
        }

        return file.Extension is ".html" or ".htm" or ".mhtml" or ".xhtml"
            ? Path.Combine(Path.GetDirectoryName(file.FullPath)!, Path.GetFileNameWithoutExtension(file.Name))
            : null;
    }
}
