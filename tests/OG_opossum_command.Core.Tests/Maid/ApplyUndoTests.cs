using System.Reflection;
using OG_opossum_command.Core.Maid;

namespace OG_opossum_command.Core.Tests.Maid;

public sealed class ApplyUndoTests : IDisposable
{
    private readonly MaidFixture _world = new();

    public void Dispose() => _world.Dispose();

    private void Seed()
    {
        _world.Write(_world.Inbox, "Q3 report - Copy.txt", "Quarterly report. Executive summary, key metrics, findings and results for Contoso.");
        _world.Write(_world.Downloads, "Contoso gala flyer.txt", "Contoso fundraiser gala event flyer with the volunteer agenda.");
        _world.Write(_world.Downloads, "Garden plan.txt", "Tomatoes and beans for my family garden.");
        _world.Write(_world.Downloads, "Garden plan (1).txt", "Tomatoes and beans for my family garden.");
        _world.Write(_world.Downloads, "old.zip.crdownload", "partial", _world.Clock.GetUtcNow().AddDays(-3));
        _world.Write(_world.Downloads, "recovery codes.txt", "1111 2222 3333");
    }

    private MaidPlan Approved(MaidPlan plan)
    {
        plan.Approved = true;
        PlanStore.Save(_world.Locations, plan);
        return plan;
    }

    [Fact]
    public void Plan_is_read_only()
    {
        Seed();
        var before = _world.Snapshot();
        var plan = _world.Plan();
        PlanStore.Save(_world.Locations, plan);
        HtmlReport.Write(plan, _world.Locations);
        Assert.Equal(before, _world.Snapshot());
    }

    [Fact]
    public void Apply_refuses_an_unapproved_plan()
    {
        Seed();
        var plan = _world.Plan();
        Assert.Throws<MaidConfigException>(() => _world.Applier.Apply(plan));
    }

    [Fact]
    public void Apply_then_undo_restores_every_file_exactly_and_never_deletes()
    {
        Seed();
        var before = _world.Snapshot();
        var hashes = _world.ContentHashes();
        var plan = Approved(_world.Plan());

        var run = _world.Applier.Apply(plan);
        Assert.True(run.Done >= 3, string.Join(Environment.NewLine, run.Lines));
        Assert.Equal(0, run.Failed);
        Assert.Equal(hashes, _world.ContentHashes()); // same content, same count: moved, never deleted
        Assert.True(File.Exists(Path.Combine(_world.WorkRoot, "Reports", "Q3 Report.txt")));
        Assert.True(File.Exists(Path.Combine(_world.Downloads, MaidLocations.TrashFolderName, run.RunId, "Garden plan (1).txt")));
        Assert.True(File.Exists(Path.Combine(_world.Downloads, "recovery codes.txt"))); // SECURITY: untouched

        var undo = _world.Applier.Undo(run.RunId);
        Assert.Equal(run.Done, undo.Done);
        Assert.Equal(before, _world.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(_world.WorkRoot, "Reports"))); // folders the run made are gone once empty
        Assert.False(Directory.Exists(Path.Combine(_world.Downloads, MaidLocations.TrashFolderName, run.RunId)));

        // Undo twice is harmless.
        var again = _world.Applier.Undo(run.RunId);
        Assert.Equal(0, again.Done);
        Assert.Equal(before, _world.Snapshot());
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        Seed();
        var plan = Approved(_world.Plan());
        var first = _world.Applier.Apply(plan);
        var after = _world.Snapshot();
        var second = _world.Applier.Apply(plan);
        Assert.Equal(0, second.Done);
        Assert.Equal(0, second.Failed);
        Assert.Contains(second.Lines, l => l.Contains("already applied", StringComparison.Ordinal));
        Assert.Equal(after, _world.Snapshot());
        Assert.True(first.Done > 0);
    }

    [Fact]
    public void A_taken_name_gets_a_number_and_nothing_is_overwritten()
    {
        _world.Write(_world.Inbox, "q3 report final.txt", "Quarterly report. Executive summary, key metrics, findings and results for Contoso.");
        var existing = _world.Write(Path.Combine(_world.WorkRoot, "Reports"), "Q3 Report.txt", "an older report that must survive");
        var plan = Approved(_world.Plan());
        var run = _world.Applier.Apply(plan);
        Assert.Equal("an older report that must survive", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(_world.WorkRoot, "Reports", "Q3 Report (2).txt")));
        Assert.Contains(run.Lines, l => l.Contains("was taken", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_changed_or_gone_since_the_plan_is_skipped()
    {
        var edited = _world.Write(_world.Inbox, "Report A.txt", "Quarterly report. Executive summary, key metrics, findings.");
        var gone = _world.Write(_world.Inbox, "Report B.txt", "Annual report. Executive summary, key metrics, findings.");
        var plan = Approved(_world.Plan());
        File.AppendAllText(edited, " plus an edit");
        File.Move(gone, gone + ".moved");
        var run = _world.Applier.Apply(plan);
        Assert.True(File.Exists(edited));
        Assert.Contains(run.Lines, l => l.Contains("since the plan was made", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("no longer there", StringComparison.Ordinal));
        Assert.Equal(0, run.Done);
    }

    [Fact]
    public void Cross_side_moves_are_refused_without_an_explicit_decision()
    {
        var path = _world.Write(_world.Inbox, "Budget.txt", "Program budget, vendor invoice and payroll for Contoso.");
        var plan = _world.Plan();
        var item = plan.Items.Single(i => i.Source == path);
        item.Action = MaidAction.Move;
        item.Side = MaidSide.Personal; // hand-edited
        item.Category = "Money";
        item.Approved = true;
        var run = _world.Applier.Apply(Approved(plan));
        Assert.True(File.Exists(path));
        Assert.Contains(run.Lines, l => l.Contains("cross-side", StringComparison.Ordinal));

        item.CrossSideConfirmed = true; // what `learn --side personal` sets
        var allowed = _world.Applier.Apply(Approved(plan));
        Assert.Equal(1, allowed.Done);
        Assert.True(File.Exists(Path.Combine(_world.PersonalRoot, "Money", "Budget.txt")));
    }

    [Fact]
    public void Decide_and_security_items_are_never_applied_even_if_ticked()
    {
        var unsure = _world.Write(_world.Downloads, "notes.txt", "some notes about nothing in particular at all");
        var secret = _world.Write(_world.Downloads, "passwords.txt", "x");
        var plan = _world.Plan();
        foreach (var item in plan.Items)
        {
            item.Approved = true;
        }

        Assert.Equal(MaidAction.Decide, plan.Items.Single(i => i.Source == unsure).Action);
        Assert.Equal(MaidAction.Security, plan.Items.Single(i => i.Source == secret).Action);
        var run = _world.Applier.Apply(Approved(plan));
        Assert.True(File.Exists(unsure));
        Assert.True(File.Exists(secret));
        Assert.Equal(0, run.Done);
    }

    [Fact]
    public void A_hand_edited_plan_cannot_reach_outside_the_sources_or_the_side_root()
    {
        var outside = _world.Write(_world.Root, "elsewhere.txt", "Quarterly report. Executive summary, key metrics.");
        var inside = _world.Write(_world.Inbox, "Report.txt", "Quarterly report. Executive summary, key metrics, findings.");
        var plan = _world.Plan();
        var item = plan.Items.Single(i => i.Source == inside);
        item.Approved = true;
        item.Source = outside;
        var sneaky = plan.Items.Append(new PlanItem { Id = "x1", Approved = true, Action = MaidAction.Move, Source = inside, SourceRoot = _world.Inbox, Origin = MaidSide.Work, Side = MaidSide.Work, Category = "Reports", Subfolder = @"..\..\..", NewName = "Report", Size = new FileInfo(inside).Length, Modified = File.GetLastWriteTimeUtc(inside) }).ToList();
        plan.Items = sneaky;
        var run = _world.Applier.Apply(Approved(plan));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(inside));
        Assert.Equal(0, run.Done);
    }

    [Fact]
    public void Undo_leaves_a_file_edited_after_the_move_where_it_is()
    {
        _world.Write(_world.Inbox, "Board report.txt", "Quarterly report. Executive summary, key metrics, findings.");
        var run = _world.Applier.Apply(Approved(_world.Plan()));
        var moved = Path.Combine(_world.WorkRoot, "Reports", "Board Report.txt");
        File.AppendAllText(moved, " edited later");
        var undo = _world.Applier.Undo(run.RunId);
        Assert.True(File.Exists(moved));
        Assert.Contains(undo.Lines, l => l.Contains("edited after the move", StringComparison.Ordinal));
    }

    [Fact]
    public void Work_moves_write_a_sharepoint_tag_csv_and_personal_moves_a_sidecar_index()
    {
        _world.Write(_world.Inbox, "Report.txt", "Quarterly report. Executive summary, key metrics, findings.");
        _world.Write(_world.Downloads, "Bank statement.txt", "Statement for my family. Account number, opening balance, closing balance, bank statement period.");
        var plan = _world.Plan();
        foreach (var item in plan.Items.Where(i => i.Action == MaidAction.Move))
        {
            item.Approved = true;
        }

        var run = _world.Applier.Apply(Approved(plan));
        var csv = run.Outputs.Single(o => o.EndsWith("-work-tags.csv", StringComparison.Ordinal));
        Assert.StartsWith("Path,Name,Category,DocType,Tags", File.ReadAllText(csv), StringComparison.Ordinal);
        Assert.Contains(@"Reports\", File.ReadAllText(csv), StringComparison.Ordinal);
        var index = MaidJson.Read<PersonalTagIndex>(Path.Combine(_world.Locations.ExportsDirectory, TagIndex.PersonalIndexFile));
        Assert.Contains(index.Files.Keys, k => k.StartsWith(_world.PersonalRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Filed_work_folders_are_left_in_place_unless_opted_in()
    {
        var filed = Path.Combine(_world.WorkRoot, "Old stuff");
        _world.Config.Settings.Sources = [new SourceSetting { Path = filed, Side = MaidSide.Work }];
        var path = _world.Write(filed, "Report.txt", "Quarterly report. Executive summary, key metrics, findings.");
        var item = _world.Plan().Items.Single(i => i.Source == path);
        Assert.Equal(MaidAction.Leave, item.Action);
        Assert.Contains(item.Reasons, r => r.Contains("may be shared or linked", StringComparison.Ordinal));

        _world.Config.Settings.ReorganiseFiledWorkFiles = true;
        Assert.Equal(MaidAction.Move, _world.Plan().Items.Single(i => i.Source == path).Action);
    }

    [Fact]
    public void Purge_holds_trash_for_thirty_days_and_only_recycles_with_confirm()
    {
        _world.Write(_world.Downloads, "old.zip.crdownload", "partial", _world.Clock.GetUtcNow().AddDays(-3));
        var run = _world.Applier.Apply(Approved(_world.Plan()));
        var trashed = Path.Combine(_world.Downloads, MaidLocations.TrashFolderName, run.RunId, "old.zip.crdownload");
        var recycler = new RecordingRecycler();

        var early = _world.Applier.Purge(confirm: true, recycler);
        Assert.Empty(recycler.Recycled);
        Assert.Contains(early.Lines, l => l.StartsWith("held until", StringComparison.Ordinal));

        _world.Clock.Advance(TimeSpan.FromDays(31));
        var dry = _world.Applier.Purge(confirm: false, recycler);
        Assert.Empty(recycler.Recycled);
        Assert.Contains(dry.Lines, l => l.StartsWith("would recycle", StringComparison.Ordinal));
        Assert.True(File.Exists(trashed));

        var real = _world.Applier.Purge(confirm: true, recycler);
        Assert.Equal(1, real.Done);
        Assert.Equal([trashed], recycler.Recycled);
    }

    [Fact]
    public void Nothing_in_the_maid_deletes_except_the_one_file_that_is_allowed_to()
    {
        // ADR-067: a text scan, like DesktopXamlRulesTests. Only MaidDeletion.cs may remove anything.
        var exe = typeof(ApplyUndoTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DesktopExePath").Value!;
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", "..", "..", ".."));
        var maid = Path.Combine(repo, "src", "OG_opossum_command.Core", "Maid");
        var cli = Path.Combine(repo, "tools", "OpossumMaid");
        Assert.True(Directory.Exists(maid), maid);
        string[] forbidden = ["File.Delete(", "Directory.Delete(", ".Delete(", "FileSystem.DeleteFile", "FileSystem.DeleteDirectory", "SHFileOperation", "RemoveDirectory", "DeleteFile("];
        var offenders = Directory.EnumerateFiles(maid, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(cli, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => !string.Equals(Path.GetFileName(f), "MaidDeletion.cs", StringComparison.Ordinal))
            .SelectMany(f => forbidden.Where(token => File.ReadAllText(f).Contains(token, StringComparison.Ordinal)).Select(token => $"{Path.GetFileName(f)}: {token}"))
            .ToList();
        Assert.Empty(offenders);
    }

    private sealed class RecordingRecycler : IRecycler
    {
        public List<string> Recycled { get; } = [];

        public void Recycle(string path) => Recycled.Add(path);
    }
}
