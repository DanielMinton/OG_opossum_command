namespace OG_opossum_command.Core.Maid;

/// <summary>Keep, junk (to <c>_Trash Review</c>, never deleted) or review (you look).</summary>
public enum JunkVerdict
{
    /// <summary>Needed: file it.</summary>
    Keep,

    /// <summary>Not needed: move to <c>_Trash Review</c> for 30 days.</summary>
    Junk,

    /// <summary>Might be junk: left for you to look at.</summary>
    Review,
}

/// <summary>The junk decision for one file.</summary>
public sealed record JunkDecision(JunkVerdict Verdict, IReadOnlyList<string> Reasons)
{
    /// <summary>Keep, no reasons.</summary>
    public static readonly JunkDecision Keep = new(JunkVerdict.Keep, []);
}

/// <summary>
/// Separates garbage from files that are needed (ADR-062): exact duplicates, unfinished downloads, orphaned saved-page
/// folders, installers, empty files, superseded versions, stale screenshots, archives already extracted.
/// Official, financial, legal, certificate-looking or signed files are always kept: at worst "review".
/// </summary>
public sealed class JunkDetector
{
    private static readonly string[] PageExtensions = [".html", ".htm", ".mhtml", ".xhtml"];

    private readonly MaidSettings _settings;
    private readonly DateTimeOffset _now;

    /// <summary>Creates a detector. <paramref name="now"/> is injected so age rules are testable.</summary>
    public JunkDetector(MaidSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _now = now;
    }

    /// <summary>Decides every file; files not in the result are keep.</summary>
    public IReadOnlyDictionary<ScannedFile, JunkDecision> Evaluate(IReadOnlyList<(ScannedFile File, Classification Class)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var found = new Dictionary<ScannedFile, List<(JunkVerdict Verdict, string Reason)>>();
        void Flag(ScannedFile file, JunkVerdict verdict, string reason)
        {
            if (!found.TryGetValue(file, out var list))
            {
                found[file] = list = [];
            }

            list.Add((verdict, reason));
        }

        var candidates = items.Where(i => !i.Class.IsSecurity).ToList();
        foreach (var (file, c) in candidates)
        {
            var age = _now - file.Modified;
            if (!file.IsFolder && file.Size == 0)
            {
                Flag(file, JunkVerdict.Junk, "It's empty (0 bytes).");
            }

            if (c.DocType == "partial-download")
            {
                Flag(file, age < TimeSpan.FromHours(1) ? JunkVerdict.Review : JunkVerdict.Junk,
                    age < TimeSpan.FromHours(1) ? "An unfinished download that may still be running." : "An unfinished download that never completed.");
            }

            if (c.DocType == "installer")
            {
                Flag(file, age < TimeSpan.FromDays(7) ? JunkVerdict.Review : JunkVerdict.Junk,
                    "An installer; once it's installed you can download it again.");
            }

            if (c.DocType == "screenshot" && age > TimeSpan.FromDays(_settings.StaleScreenshotDays) && (file.Modified - file.Created).Duration() < TimeSpan.FromMinutes(2))
            {
                Flag(file, JunkVerdict.Junk, $"A screenshot over {_settings.StaleScreenshotDays} days old that was never edited.");
            }
        }

        DuplicateRules(candidates, Flag);
        SavedPageRules(candidates, Flag);
        VersionRules(candidates, Flag);
        ExtractedArchiveRules(candidates, Flag);

        var result = new Dictionary<ScannedFile, JunkDecision>();
        foreach (var (file, c) in items)
        {
            var flags = found.GetValueOrDefault(file) ?? [];
            if (c.LearnedVerdict is "keep")
            {
                result[file] = new JunkDecision(JunkVerdict.Keep, ["A learned rule says keep files like this."]);
                continue;
            }

            if (c.LearnedVerdict is "junk")
            {
                flags.Add((JunkVerdict.Junk, "A learned rule says files like this are junk."));
            }

            if (flags.Count == 0)
            {
                continue;
            }

            var verdict = flags.Any(f => f.Verdict == JunkVerdict.Junk) ? JunkVerdict.Junk : JunkVerdict.Review;
            var reasons = flags.Select(f => f.Reason).Distinct(StringComparer.Ordinal).ToList();
            if (verdict == JunkVerdict.Junk && c.IsProtected)
            {
                verdict = JunkVerdict.Review;
                reasons.Add("But it looks official, financial or legal, so it is never trashed automatically.");
            }

            result[file] = new JunkDecision(verdict, reasons);
        }

        return result;
    }

    /// <summary>Same bytes on the same side: keep the best-named, oldest copy. Same text, different bytes: review.</summary>
    private static void DuplicateRules(List<(ScannedFile File, Classification Class)> items, Action<ScannedFile, JunkVerdict, string> flag)
    {
        foreach (var group in items.Where(i => i.File.Sha256 is not null && i.File.Size > 0).GroupBy(i => (i.File.Source.Side, i.File.Sha256)))
        {
            var ordered = group.OrderBy(i => CopyPenalty(i.File.Name)).ThenBy(i => i.File.Created).ThenBy(i => i.File.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var extra in ordered.Skip(1))
            {
                flag(extra.File, JunkVerdict.Junk, $"An exact duplicate of \"{ordered[0].File.Name}\".");
            }
        }

        foreach (var group in items.Where(i => i.File.ContentFingerprint is not null).GroupBy(i => (i.File.Source.Side, i.File.ContentFingerprint)))
        {
            var distinct = group.DistinctBy(i => i.File.Sha256).ToList();
            if (distinct.Count < 2)
            {
                continue;
            }

            var keep = distinct.OrderByDescending(i => i.File.Modified).First();
            foreach (var other in distinct.Where(i => i.File != keep.File))
            {
                flag(other.File, JunkVerdict.Review, $"Same text as \"{keep.File.Name}\" (newer); probably a re-saved copy.");
            }
        }
    }

    private static int CopyPenalty(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return string.Equals(NameCleaner.Strip(stem), stem.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    /// <summary>A <c>X_files</c> folder needs its <c>X.htm(l)</c>; a saved page without its folder is broken.</summary>
    private static void SavedPageRules(List<(ScannedFile File, Classification Class)> items, Action<ScannedFile, JunkVerdict, string> flag)
    {
        foreach (var (file, c) in items)
        {
            var dir = Path.GetDirectoryName(file.FullPath)!;
            if (file.IsFolder)
            {
                var stem = file.Name[..^"_files".Length];
                var hasPage = PageExtensions.Any(ext => File.Exists(Path.Combine(dir, stem + ext)));
                if (!hasPage)
                {
                    flag(file, JunkVerdict.Junk, "A saved page's assets folder whose page is gone.");
                }
            }
            else if (c.DocType == "saved-web-page" && file.Extraction?.IsSavedWebPage == true && file.Extension is ".html" or ".htm"
                && !Directory.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(file.Name) + "_files")))
            {
                flag(file, JunkVerdict.Review, "A saved web page without its _files folder; it may not display properly.");
            }
        }
    }

    /// <summary>v1/v2 when a final exists: the older ones are superseded.</summary>
    private static void VersionRules(List<(ScannedFile File, Classification Class)> items, Action<ScannedFile, JunkVerdict, string> flag)
    {
        var groups = items
            .Where(i => !i.File.IsFolder)
            .GroupBy(i => (Path.GetDirectoryName(i.File.FullPath)!.ToUpperInvariant(), i.File.Extension, NameCleaner.BaseOf(Path.GetFileNameWithoutExtension(i.File.Name))))
            .Where(g => g.Key.Item3.Length >= 3 && g.Count() >= 2);
        foreach (var group in groups)
        {
            var list = group.ToList();
            var final = list.Where(i => NameCleaner.SaysFinal(Path.GetFileNameWithoutExtension(i.File.Name))).OrderByDescending(i => i.File.Modified).FirstOrDefault();
            var newestVersion = list.Select(i => (i, v: NameCleaner.VersionOf(Path.GetFileNameWithoutExtension(i.File.Name)))).Where(x => x.v is not null).OrderByDescending(x => x.v).FirstOrDefault();
            var keeper = final.File ?? newestVersion.i.File;
            if (keeper is null)
            {
                continue;
            }

            foreach (var (file, _) in list.Where(i => i.File != keeper && i.File.Sha256 != keeper.Sha256))
            {
                var versioned = NameCleaner.VersionOf(Path.GetFileNameWithoutExtension(file.Name)) is not null;
                flag(file, final.File is not null && versioned ? JunkVerdict.Junk : JunkVerdict.Review, $"Superseded by \"{keeper.Name}\".");
            }
        }
    }

    /// <summary>An archive next to a folder of the same name was already extracted.</summary>
    private static void ExtractedArchiveRules(List<(ScannedFile File, Classification Class)> items, Action<ScannedFile, JunkVerdict, string> flag)
    {
        foreach (var (file, c) in items.Where(i => i.Class.DocType == "archive"))
        {
            var folder = Path.Combine(Path.GetDirectoryName(file.FullPath)!, Path.GetFileNameWithoutExtension(file.Name));
            if (Directory.Exists(folder))
            {
                flag(file, JunkVerdict.Junk, $"Already extracted to the folder \"{Path.GetFileName(folder)}\".");
            }
        }
    }
}
