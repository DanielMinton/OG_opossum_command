using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Maid;

/// <summary>What an apply, undo or purge did, line by line.</summary>
public sealed class RunReport
{
    /// <summary>The run id.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Files moved (or moved back, or recycled).</summary>
    public int Done { get; set; }

    /// <summary>Items skipped, with why.</summary>
    public int Skipped { get; set; }

    /// <summary>Items that failed.</summary>
    public int Failed { get; set; }

    /// <summary>One line per item.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Extra files written (journal, tag CSV).</summary>
    public List<string> Outputs { get; } = [];
}

/// <summary>
/// Executes an approved plan and reverses it (ADR-066). Moves only, never a delete, never an overwrite: a name that
/// is taken gets " (2)". Each item is checked again at apply time (still there, unchanged, still on its side), so a
/// stale or hand-edited plan can't do anything the planner wouldn't have.
/// </summary>
public sealed class Applier
{
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    private readonly MaidConfig _config;
    private readonly MaidLocations _locations;
    private readonly TimeProvider _clock;

    /// <summary>Creates an applier.</summary>
    public Applier(MaidConfig config, MaidLocations locations, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(locations);
        _config = config;
        _locations = locations;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Applies the approved items of an approved plan.</summary>
    /// <exception cref="MaidConfigException">The plan is not approved.</exception>
    public RunReport Apply(MaidPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Approved)
        {
            throw new MaidConfigException($"Plan {plan.Id} is not approved. Read the report, then run `approve {plan.Id}` (or set \"approved\": true in the plan file).");
        }

        var now = _clock.GetUtcNow();
        var runId = RunJournal.NewRunId(now);
        for (var n = 2; File.Exists(RunJournal.PathOf(_locations, runId)); n++)
        {
            runId = $"{RunJournal.NewRunId(now)}-{n}";
        }

        var report = new RunReport { RunId = runId };
        var previous = PreviouslyApplied(plan.Id);
        var workMoves = new List<JournalEntry>();
        var personalTags = new List<(string, TaggedFile?)>();
        using (var journal = RunJournal.Open(RunJournal.PathOf(_locations, runId)))
        {
            journal.Write(new JournalEntry { Op = "start", PlanId = plan.Id, State = "done", At = now });
            report.Outputs.Add(journal.Path);
            foreach (var item in plan.Items.Where(i => i.Approved))
            {
                var outcome = ApplyOne(item, runId, previous, journal);
                report.Lines.Add($"{item.Id}: {outcome.Line}");
                switch (outcome.Result)
                {
                    case Outcome.Done:
                        report.Done++;
                        if (outcome.Entry is { Action: MaidAction.Move, Side: MaidSide.Work } work)
                        {
                            workMoves.Add(work);
                        }
                        else if (outcome.Entry is { Action: MaidAction.Move, Side: MaidSide.Personal, IsFolder: false } personal)
                        {
                            personalTags.Add((personal.To!, new TaggedFile { Tags = item.Tags, Category = item.Category, DocType = item.DocType, Run = runId }));
                        }

                        break;
                    case Outcome.Failed:
                        report.Failed++;
                        break;
                    default:
                        report.Skipped++;
                        break;
                }
            }

            journal.Write(new JournalEntry { Op = "end", State = "done", At = _clock.GetUtcNow(), Note = $"{report.Done} done, {report.Skipped} skipped, {report.Failed} failed" });
        }

        TagIndex.UpdatePersonal(_locations, personalTags);
        if (TagIndex.WriteWorkCsv(_locations, runId, workMoves) is { } csv)
        {
            report.Outputs.Add(csv);
        }

        return report;
    }

    private enum Outcome
    {
        Done,
        Skipped,
        Failed,
    }

    private (Outcome Result, string Line, JournalEntry? Entry) ApplyOne(PlanItem item, string runId, Dictionary<string, string> previous, RunJournal journal)
    {
        if (item.Action is not (MaidAction.Move or MaidAction.Trash))
        {
            return (Outcome.Skipped, item.Action switch
            {
                MaidAction.Decide => "not applied: needs your decision (use `learn` to choose a side and category).",
                MaidAction.Security => "not applied: SECURITY items are never touched.",
                _ => "nothing to do (left in place).",
            }, null);
        }

        if (Refusal(item) is { } refusal)
        {
            return (Outcome.Skipped, "refused: " + refusal, null);
        }

        var destination = Planner.DestinationOf(item, _locations, runId)!;
        var exists = item.IsFolder ? Directory.Exists(item.Source) : File.Exists(item.Source);
        if (!exists)
        {
            if (previous.TryGetValue(item.Id, out var earlier) && (File.Exists(earlier) || Directory.Exists(earlier)))
            {
                return (Outcome.Skipped, $"already applied (now at {earlier}).", null);
            }

            return (Outcome.Skipped, "skipped: the file is no longer there.", null);
        }

        if (!item.IsFolder && Changed(item) is { } change)
        {
            return (Outcome.Skipped, $"skipped: {change} since the plan was made; plan again.", null);
        }

        try
        {
            var root = item.Action == MaidAction.Trash ? item.SourceRoot : _locations.RootOf(item.Side)!;
            if (!Directory.Exists(root))
            {
                if (item.Action == MaidAction.Move && item.Side == MaidSide.Personal && Directory.Exists(Path.GetDirectoryName(root)))
                {
                    Directory.CreateDirectory(root);
                    journal.Write(new JournalEntry { Op = "mkdir", To = root, State = "done", At = _clock.GetUtcNow() });
                }
                else
                {
                    return (Outcome.Skipped, $"skipped: {root} does not exist (a OneDrive root is never created).", null);
                }
            }

            CreateFolders(root, Path.GetDirectoryName(destination)!, journal);
            var target = FreeName(destination, item.IsFolder, out var renamed);
            var entry = new JournalEntry
            {
                Op = "move",
                Item = item.Id,
                From = item.Source,
                To = target,
                Sha256 = item.Sha256,
                IsFolder = item.IsFolder,
                Action = item.Action,
                Side = item.Action == MaidAction.Move ? item.Side : item.Origin,
                State = "pending",
                At = _clock.GetUtcNow(),
                Tags = item.Tags,
                Category = item.Category,
                DocType = item.DocType,
            };
            journal.Write(entry);
            if (item.IsFolder)
            {
                Directory.Move(item.Source, target);
            }
            else
            {
                File.Move(item.Source, target, overwrite: false);
            }

            entry.State = "done";
            entry.Note = renamed ? $"name taken, saved as \"{Path.GetFileName(target)}\"" : null;
            entry.At = _clock.GetUtcNow();
            journal.Write(entry);
            var verb = item.Action == MaidAction.Trash ? "trashed (reversible) to" : "moved to";
            return (Outcome.Done, $"{verb} {target}{(renamed ? $" (\"{Path.GetFileName(destination)}\" was taken, so it got a number)" : string.Empty)}", entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal.Write(new JournalEntry { Op = "move", Item = item.Id, From = item.Source, To = destination, State = "failed", Note = ex.Message, At = _clock.GetUtcNow() });
            return (Outcome.Failed, $"failed: {ex.Message}", null);
        }
    }

    /// <summary>The checks that make a hand-edited plan harmless: sides, roots, names, and where the source lives.</summary>
    private string? Refusal(PlanItem item)
    {
        if (!Path.IsPathFullyQualified(item.Source) || !Path.IsPathFullyQualified(item.SourceRoot) || !OneDriveBoundary.IsInside(item.Source, item.SourceRoot)
            || !_locations.Sources.Any(s => OneDriveBoundary.IsSamePath(s.Path, item.SourceRoot)))
        {
            return "the file is not inside one of this PC's maid sources.";
        }

        var sourceSide = _locations.SideOfPath(item.Source);
        if (sourceSide != MaidSide.Unassigned && sourceSide != item.Origin)
        {
            return "the plan's origin side does not match where the file lives.";
        }

        if (item.Action == MaidAction.Trash)
        {
            return null;
        }

        if (item.Side is not (MaidSide.Work or MaidSide.Personal) || _locations.RootOf(item.Side) is null)
        {
            return "no side chosen, or that side has no root on this PC.";
        }

        if (item.Origin != MaidSide.Unassigned && item.Side != item.Origin && !item.CrossSideConfirmed)
        {
            return $"a cross-side move ({item.Origin} → {item.Side}) needs your explicit decision (`learn <plan> {item.Id} --side {item.Side.ToString().ToLowerInvariant()}`); never automatic (ADR-012).";
        }

        var taxonomy = _config.Taxonomy.For(item.Side);
        if (taxonomy.Find(item.Category) is null && !string.Equals(item.Category, taxonomy.Fallback, StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{item.Category}\" is not a {item.Side} category in taxonomy.json.";
        }

        if (item.Subfolder is not null && !NameCleaner.IsSafeFolderName(item.Subfolder))
        {
            return "the subfolder must be one plain folder name (two levels at most).";
        }

        var destination = Planner.DestinationOf(item, _locations, "check")!;
        var root = _locations.RootOf(item.Side)!;
        var other = _locations.RootOf(item.Side == MaidSide.Work ? MaidSide.Personal : MaidSide.Work);
        if (!OneDriveBoundary.IsInside(destination, root) || (other is not null && OneDriveBoundary.IsInside(destination, other)))
        {
            return "the destination is outside its side's root.";
        }

        return null;
    }

    private static string? Changed(PlanItem item)
    {
        var info = new FileInfo(item.Source);
        if (info.Length != item.Size)
        {
            return "its size changed";
        }

        return (info.LastWriteTimeUtc - item.Modified.UtcDateTime).Duration() > MtimeTolerance ? "it was edited" : null;
    }

    /// <summary>Creates the missing folders between <paramref name="root"/> and <paramref name="folder"/>, journalling each so undo can remove it if it ends up empty.</summary>
    private void CreateFolders(string root, string folder, RunJournal journal)
    {
        var missing = new Stack<string>();
        for (var current = folder; !Directory.Exists(current) && OneDriveBoundary.IsInside(current, root); current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }

        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            journal.Write(new JournalEntry { Op = "mkdir", To = path, State = "done", At = _clock.GetUtcNow() });
        }
    }

    /// <summary>The destination, or "name (2)", "name (3)", ... when it is taken. Never overwrites.</summary>
    internal static string FreeName(string destination, bool isFolder, out bool renamed)
    {
        renamed = false;
        bool Taken(string p) => File.Exists(p) || Directory.Exists(p);
        if (!Taken(destination))
        {
            return destination;
        }

        var folder = Path.GetDirectoryName(destination)!;
        var stem = isFolder ? Path.GetFileName(destination) : Path.GetFileNameWithoutExtension(destination);
        var extension = isFolder ? string.Empty : Path.GetExtension(destination);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!Taken(candidate))
            {
                renamed = true;
                return candidate;
            }
        }

        throw new IOException($"Too many files named like {destination}.");
    }

    /// <summary>Item id → where an earlier, not-undone run of the same plan put it.</summary>
    private Dictionary<string, string> PreviouslyApplied(string planId)
    {
        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in RunJournal.RunIds(_locations))
        {
            var entries = RunJournal.Read(RunJournal.PathOf(_locations, run));
            if (entries.FirstOrDefault(e => e.Op == "start")?.PlanId != planId)
            {
                continue;
            }

            var restored = RunJournal.Read(RunJournal.UndoPathOf(_locations, run)).Where(e => e.Op == "restore" && e.State == "done").Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var move in entries.Where(e => e.Op == "move" && e.State == "done" && e.Item is not null && !restored.Contains(e.Item)))
            {
                placed[move.Item!] = move.To!;
            }
        }

        return placed;
    }

    /// <summary>Reverses a run exactly: newest move first, back to where it was; folders the run created are removed once empty.</summary>
    public RunReport Undo(string runId)
    {
        var path = RunJournal.PathOf(_locations, runId);
        var entries = RunJournal.Read(path);
        if (entries.Count == 0)
        {
            throw new MaidConfigException($"No journal for run {runId} at {path}.");
        }

        var report = new RunReport { RunId = runId };
        var undoPath = RunJournal.UndoPathOf(_locations, runId);
        var alreadyRestored = RunJournal.Read(undoPath).Where(e => e.Op == "restore" && e.State == "done").Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untagged = new List<(string, TaggedFile?)>();

        // The last record per item wins: a pending without a done means the crash came mid-move.
        var moves = entries.Where(e => e.Op == "move" && e.State is "done" or "pending" && e.Item is not null)
            .GroupBy(e => e.Item!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .Reverse()
            .ToList();
        using (var journal = RunJournal.Open(undoPath))
        {
            foreach (var move in moves)
            {
                if (alreadyRestored.Contains(move.Item!))
                {
                    report.Skipped++;
                    report.Lines.Add($"{move.Item}: already undone.");
                    continue;
                }

                var line = UndoOne(move, journal, out var result);
                report.Lines.Add($"{move.Item}: {line}");
                switch (result)
                {
                    case Outcome.Done:
                        report.Done++;
                        if (move.Side == MaidSide.Personal && move.Action == MaidAction.Move)
                        {
                            untagged.Add((move.To!, null));
                        }

                        break;
                    case Outcome.Failed:
                        report.Failed++;
                        break;
                    default:
                        report.Skipped++;
                        break;
                }
            }

            foreach (var folder in entries.Where(e => e.Op == "mkdir").Select(e => e.To!).Reverse())
            {
                if (MaidDeletion.RemoveEmptyFolder(folder))
                {
                    journal.Write(new JournalEntry { Op = "rmdir", To = folder, State = "done", At = _clock.GetUtcNow() });
                }
            }

            foreach (var source in entries.Where(e => e.Op == "move" && e.Action == MaidAction.Trash && e.To is not null).Select(e => Path.GetDirectoryName(e.To)!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Empty folders inside this run's trash folder, deepest first, then the run folder itself.
                RemoveEmptyTrashChain(source, runId);
            }

            report.Outputs.Add(journal.Path);
        }

        TagIndex.UpdatePersonal(_locations, untagged);
        return report;
    }

    private static void RemoveEmptyTrashChain(string folder, string runId)
    {
        for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            var isRunFolder = string.Equals(Path.GetFileName(current), runId, StringComparison.OrdinalIgnoreCase);
            if (!MaidDeletion.RemoveEmptyFolder(current) || isRunFolder)
            {
                return;
            }
        }
    }

    private string UndoOne(JournalEntry move, RunJournal journal, out Outcome result)
    {
        var from = move.From!;
        var to = move.To!;
        var atTarget = move.IsFolder ? Directory.Exists(to) : File.Exists(to);
        if (!atTarget)
        {
            var atOrigin = move.IsFolder ? Directory.Exists(from) : File.Exists(from);
            result = Outcome.Skipped;
            return atOrigin ? "already back where it was." : $"skipped: not found at {to} (moved or renamed since?).";
        }

        if (!move.IsFolder && move.Sha256 is not null && Scanner.HashFile(to) is { } now && !string.Equals(now, move.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            result = Outcome.Skipped;
            return $"skipped: {to} was edited after the move, so it stays (move it back yourself if you want).";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(from)!);
            var target = FreeName(from, move.IsFolder, out var renamed);
            if (move.IsFolder)
            {
                Directory.Move(to, target);
            }
            else
            {
                File.Move(to, target, overwrite: false);
            }

            journal.Write(new JournalEntry { Op = "restore", Item = move.Item, From = to, To = target, State = "done", At = _clock.GetUtcNow(), Note = renamed ? "original name taken" : null });
            result = Outcome.Done;
            return renamed ? $"moved back as \"{Path.GetFileName(target)}\" (something new took the original name)." : $"moved back to {target}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal.Write(new JournalEntry { Op = "restore", Item = move.Item, From = to, To = from, State = "failed", Note = ex.Message, At = _clock.GetUtcNow() });
            result = Outcome.Failed;
            return $"failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists (and with <paramref name="confirm"/>, recycles) trash older than the hold. Only files a run put in
    /// <c>_Trash Review</c>, still there, unchanged, and not undone. Goes to the Recycle Bin, never a permanent delete.
    /// </summary>
    public RunReport Purge(bool confirm, IRecycler recycler)
    {
        ArgumentNullException.ThrowIfNull(recycler);
        var now = _clock.GetUtcNow();
        var hold = TimeSpan.FromDays(Math.Max(30, _config.Settings.TrashHoldDays));
        var report = new RunReport { RunId = "purge-" + now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) };
        using var journal = confirm ? RunJournal.Open(Path.Combine(_locations.RunsDirectory, report.RunId + ".jsonl")) : null;
        foreach (var run in RunJournal.RunIds(_locations))
        {
            var restored = RunJournal.Read(RunJournal.UndoPathOf(_locations, run)).Where(e => e.Op == "restore" && e.State == "done").Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var trash in RunJournal.Read(RunJournal.PathOf(_locations, run)).Where(e => e.Op == "move" && e.State == "done" && e.Action == MaidAction.Trash && !restored.Contains(e.Item)))
            {
                var to = trash.To!;
                var exists = trash.IsFolder ? Directory.Exists(to) : File.Exists(to);
                if (!exists || !to.Contains(Path.DirectorySeparatorChar + MaidLocations.TrashFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (now - trash.At < hold)
                {
                    report.Skipped++;
                    report.Lines.Add($"held until {(trash.At + hold).ToLocalTime():yyyy-MM-dd}: {to}");
                    continue;
                }

                if (!trash.IsFolder && trash.Sha256 is not null && !string.Equals(Scanner.HashFile(to), trash.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    report.Skipped++;
                    report.Lines.Add($"kept (changed while in trash): {to}");
                    continue;
                }

                if (!confirm)
                {
                    report.Lines.Add($"would recycle: {to}");
                    continue;
                }

                try
                {
                    recycler.Recycle(to);
                    journal!.Write(new JournalEntry { Op = "purge", Item = trash.Item, From = to, State = "done", At = _clock.GetUtcNow(), Note = "sent to the Recycle Bin" });
                    report.Done++;
                    report.Lines.Add($"recycled: {to}");
                }
                catch (IOException ex)
                {
                    report.Failed++;
                    report.Lines.Add($"failed: {to}: {ex.Message}");
                }
            }
        }

        return report;
    }
}
