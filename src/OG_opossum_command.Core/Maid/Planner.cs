using System.Globalization;

namespace OG_opossum_command.Core.Maid;

/// <summary>
/// Turns a scan into a plan (ADR-065). Read-only: nothing on disk changes except the plan file itself.
/// </summary>
public sealed class Planner
{
    private readonly MaidConfig _config;
    private readonly MaidLocations _locations;
    private readonly TimeProvider _clock;

    /// <summary>Creates a planner.</summary>
    public Planner(MaidConfig config, MaidLocations locations, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(locations);
        _config = config;
        _locations = locations;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Scans the sources and builds the plan (not saved).</summary>
    public MaidPlan Plan()
    {
        var scan = new Scanner(_config.Settings).Scan(_locations.Sources);
        return Build(scan.Files, scan.Notes.Concat(_locations.Problems).ToList());
    }

    /// <summary>Builds a plan from already-scanned files.</summary>
    public MaidPlan Build(IReadOnlyList<ScannedFile> files, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(notes);
        var now = _clock.GetUtcNow();
        var classifier = new Classifier(_config);
        var classified = files.Select(f => (File: f, Class: classifier.Classify(f))).ToList();
        var junk = new JunkDetector(_config.Settings, now).Evaluate(classified);
        var groups = Grouper.Group(classified);
        var groupOf = groups.SelectMany(g => g.Members.Select(m => (m, g))).ToDictionary(x => x.m, x => x.g);

        var items = new List<PlanItem>();
        var number = 0;
        foreach (var (file, c) in classified)
        {
            var item = ToItem(file, c, junk.GetValueOrDefault(file) ?? JunkDecision.Keep);
            item.Id = $"i{++number:0000}";
            item.Group = groupOf.TryGetValue(file, out var group) ? group.Id : null;
            items.Add(item);
        }

        var byFile = classified.Select((x, i) => (x.File, Item: items[i])).ToDictionary(x => x.File, x => x.Item);
        foreach (var group in groups)
        {
            MoveTogether(group, group.Members.Select(m => byFile[m]).ToList());
        }

        foreach (var item in items)
        {
            item.Destination = DestinationOf(item, _locations, runId: "<run>");
            if (item.Action == MaidAction.Move && item.Destination is not null && string.Equals(Path.GetFullPath(item.Destination), Path.GetFullPath(item.Source), StringComparison.OrdinalIgnoreCase))
            {
                item.Action = MaidAction.Leave;
                item.Approved = false;
                item.Destination = null;
                item.Reasons = [.. item.Reasons, "Already filed where it belongs."];
            }

            item.Proposed = new ProposedSnapshot { Action = item.Action, Side = item.Side, Category = item.Category, NewName = item.NewName };
        }

        var id = MaidPlan.NewId(now);
        for (var n = 2; File.Exists(PlanStore.PathOf(_locations, id)); n++)
        {
            id = $"{MaidPlan.NewId(now)}-{n}";
        }

        return new MaidPlan
        {
            Id = id,
            CreatedAt = now,
            WorkRoot = _locations.WorkRoot,
            PersonalRoot = _locations.PersonalRoot,
            Sources = _locations.Sources.Select(s => s.Path).ToList(),
            Notes = notes,
            Items = items,
        };
    }

    private PlanItem ToItem(ScannedFile file, Classification c, JunkDecision junk)
    {
        var settings = _config.Settings;
        var reasons = c.Reasons.Concat(junk.Reasons).ToList();
        var item = new PlanItem
        {
            Source = file.FullPath,
            SourceRoot = file.Source.Path,
            Origin = file.Source.Side,
            IsFolder = file.IsFolder,
            Size = file.Size,
            Modified = file.Modified,
            Sha256 = file.Sha256,
            Side = c.Side,
            LikelySide = c.LikelySide,
            Category = c.Category,
            Subfolder = c.Subfolder,
            NewName = c.ShortName,
            DocType = c.DocType,
            Tags = c.Tags,
            Keywords = c.Keywords,
            Entities = c.Entities,
            Confidence = c.Confidence,
            Verdict = junk.Verdict,
            Protected = c.IsProtected,
            DecidedBy = c.UsedLearnedRule ? "learned" : "rules",
        };

        if (c.IsSecurity)
        {
            item.Action = MaidAction.Security;
            item.Verdict = JunkVerdict.Keep;
        }
        else if (file.IsOnlineOnly)
        {
            item.Action = MaidAction.Leave;
            reasons.Add("Left in place: online-only, and reading it would download it.");
        }
        else if (junk.Verdict == JunkVerdict.Junk)
        {
            item.Action = MaidAction.Trash;
            item.Approved = true;
            reasons.Add($"Junk: to {MaidLocations.TrashFolderName} for {settings.TrashHoldDays} days, then only `purge` (with a confirmation flag) removes it.");
        }
        else if (junk.Verdict == JunkVerdict.Review)
        {
            item.Action = MaidAction.Leave;
            reasons.Add("Left in place for you to look at.");
        }
        else if (file.Source.Side == MaidSide.Work && !file.Source.IsWorkInbox && !settings.ReorganiseFiledWorkFiles)
        {
            item.Action = MaidAction.Leave;
            reasons.Add("Left in place: it's already in a work folder and may be shared or linked (set reorganiseFiledWorkFiles to opt in).");
        }
        else if (c.CrossSideSuspect || c.Side == MaidSide.Unassigned)
        {
            item.Action = MaidAction.Decide;
        }
        else if (c.Side == MaidSide.Work && string.Equals(c.Category, _config.Taxonomy.Work.Fallback, StringComparison.OrdinalIgnoreCase))
        {
            item.Action = MaidAction.Leave;
        }
        else if (_locations.RootOf(c.Side) is null)
        {
            item.Action = MaidAction.Leave;
            reasons.Add($"Left in place: no {c.Side} root is set up on this PC.");
        }
        else
        {
            item.Action = MaidAction.Move;
            item.Approved = c.Confidence >= settings.AutoApproveConfidence;
            if (!item.Approved)
            {
                reasons.Add($"Not ticked: confidence {c.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} is under {settings.AutoApproveConfidence.ToString("0.00", CultureInfo.InvariantCulture)}.");
            }
        }

        item.Reasons = reasons;
        return item;
    }

    /// <summary>Members of a group share the group's category (by confidence-weighted vote), per side; a saved page's folder follows its page.</summary>
    private void MoveTogether(FileGroup group, List<PlanItem> members)
    {
        foreach (var sideMembers in members.Where(m => m.Action == MaidAction.Move).GroupBy(m => m.Side))
        {
            var list = sideMembers.ToList();
            var winner = list.GroupBy(m => m.Category, StringComparer.OrdinalIgnoreCase).MaxBy(g => g.Sum(m => m.Confidence))!;
            var category = winner.Key;
            var subfolder = winner.Select(m => m.Subfolder).FirstOrDefault(s => s is not null);
            var taxonomy = _config.Taxonomy.For(sideMembers.Key);
            if (subfolder is null && group.FromOriginFolder && list.Count >= 3 && taxonomy.Find(category) is not null)
            {
                var label = NameCleaner.Sanitise(NameCleaner.TitleCase(NameCleaner.Strip(group.Label)));
                subfolder = NameCleaner.IsSafeFolderName(label) && label != "File" ? label : null;
            }

            foreach (var member in list)
            {
                if (!string.Equals(member.Category, category, StringComparison.OrdinalIgnoreCase) || member.Subfolder != subfolder)
                {
                    member.Reasons = [.. member.Reasons, $"Moves with its group \"{group.Label}\" ({list.Count} files) to {category}{(subfolder is null ? string.Empty : "\\" + subfolder)}."];
                }

                member.Category = category;
                member.Subfolder = subfolder;
                if (!group.FromOriginFolder)
                {
                    member.Tags = Classifier.NormaliseTags([.. member.Tags, group.Label]);
                }
            }
        }

        // A saved page's _files folder goes wherever its page goes, and is junk when its page is junk.
        foreach (var folder in members.Where(m => m.IsFolder))
        {
            var stem = Path.GetFileName(folder.Source)[..^"_files".Length];
            var page = members.FirstOrDefault(m => !m.IsFolder && string.Equals(Path.GetFileNameWithoutExtension(m.Source), stem, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(m.Source), Path.GetDirectoryName(folder.Source), StringComparison.OrdinalIgnoreCase));
            if (page is null)
            {
                continue;
            }

            folder.Action = page.Action;
            folder.Approved = page.Approved;
            folder.Side = page.Side;
            folder.LikelySide = page.LikelySide;
            folder.Category = page.Category;
            folder.Subfolder = page.Subfolder;
            folder.Verdict = page.Verdict;
            folder.Confidence = page.Confidence;
            folder.Reasons = [.. folder.Reasons, $"Goes with its page \"{Path.GetFileName(page.Source)}\" ({page.Id})."];
        }
    }

    /// <summary>
    /// Where an item lands, or <c>null</c> when it doesn't move. Trash keeps the path relative to its source, under
    /// <c>&lt;source&gt;\_Trash Review\&lt;run&gt;\</c>, so it never crosses sides and undo is exact.
    /// </summary>
    public static string? DestinationOf(PlanItem item, MaidLocations locations, string runId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(locations);
        switch (item.Action)
        {
            case MaidAction.Trash:
                return Path.Combine(item.SourceRoot, MaidLocations.TrashFolderName, runId, Path.GetRelativePath(item.SourceRoot, item.Source));
            case MaidAction.Move when locations.RootOf(item.Side) is { } root:
                var folder = item.Subfolder is null ? Path.Combine(root, item.Category) : Path.Combine(root, item.Category, item.Subfolder);
                var name = item.IsFolder || item.DocType == "saved-web-page" ? Path.GetFileName(item.Source) : NameCleaner.Sanitise(item.NewName) + Path.GetExtension(item.Source);
                return Path.Combine(folder, name);
            default:
                return null;
        }
    }
}
