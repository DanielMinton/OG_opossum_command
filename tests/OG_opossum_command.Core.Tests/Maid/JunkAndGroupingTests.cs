using OG_opossum_command.Core.Maid;

namespace OG_opossum_command.Core.Tests.Maid;

public sealed class JunkAndGroupingTests : IDisposable
{
    private readonly MaidFixture _world = new();

    public void Dispose() => _world.Dispose();

    private static PlanItem ItemFor(MaidPlan plan, string path) => plan.Items.Single(i => string.Equals(i.Source, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Exact_duplicates_keep_the_best_named_copy()
    {
        var original = _world.Write(_world.Downloads, "Garden plan.txt", "Tomatoes, beans and herbs for the raised beds this spring.");
        var copy = _world.Write(_world.Downloads, "Garden plan (1).txt", "Tomatoes, beans and herbs for the raised beds this spring.");
        var plan = _world.Plan();
        Assert.NotEqual(JunkVerdict.Junk, ItemFor(plan, original).Verdict);
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, copy).Verdict);
        Assert.Equal(MaidAction.Trash, ItemFor(plan, copy).Action);
        Assert.Contains(ItemFor(plan, copy).Reasons, r => r.Contains("exact duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicates_on_different_sides_are_not_junk()
    {
        _world.Write(_world.Inbox, "Agenda.txt", "Board meeting agenda and minutes for the event.");
        var personal = _world.Write(_world.Downloads, "Agenda (1).txt", "Board meeting agenda and minutes for the event.");
        var plan = _world.Plan();
        Assert.NotEqual(JunkVerdict.Junk, ItemFor(plan, personal).Verdict);
    }

    [Fact]
    public void Partial_downloads_installers_and_empty_files_are_junk()
    {
        var old = _world.Clock.GetUtcNow().AddDays(-20);
        var partial = _world.Write(_world.Downloads, "movie.mp4.crdownload", "xx", old);
        var installer = _world.Write(_world.Downloads, "ToolSetup.msi", "MSI", old);
        var empty = _world.Write(_world.Downloads, "empty.txt", string.Empty, old);
        var fresh = _world.Write(_world.Downloads, "NewSetup.exe", "MZ");
        var plan = _world.Plan();
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, partial).Verdict);
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, installer).Verdict);
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, empty).Verdict);
        Assert.Equal(JunkVerdict.Review, ItemFor(plan, fresh).Verdict);
    }

    [Fact]
    public void Superseded_versions_are_junk_when_a_final_exists()
    {
        var v1 = _world.Write(_world.Downloads, "Budget v1.txt", "budget draft one");
        var v2 = _world.Write(_world.Downloads, "Budget v2.txt", "budget draft two");
        var final = _world.Write(_world.Downloads, "Budget final.txt", "budget final numbers");
        var plan = _world.Plan();
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, v1).Verdict);
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, v2).Verdict);
        Assert.NotEqual(JunkVerdict.Junk, ItemFor(plan, final).Verdict);
        Assert.Contains(ItemFor(plan, v1).Reasons, r => r.Contains("Superseded", StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_unedited_screenshots_are_junk_but_recent_ones_stay()
    {
        var stale = _world.WriteBytes(_world.Downloads, "Screenshot 2025-01-01 090000.png", DocBuilder.Png(1920, 1080), _world.Clock.GetUtcNow().AddDays(-120));
        var recent = _world.WriteBytes(_world.Downloads, "Screenshot 2026-01-01 090000.png", DocBuilder.Png(1920, 1080, "x"), _world.Clock.GetUtcNow().AddDays(-3));
        var plan = _world.Plan();
        Assert.Equal(JunkVerdict.Junk, ItemFor(plan, stale).Verdict);
        Assert.Equal(JunkVerdict.Keep, ItemFor(plan, recent).Verdict);
    }

    [Fact]
    public void Official_documents_are_never_trashed_automatically()
    {
        var text = "Certificate of completion. This certifies that the holder has successfully completed the course.";
        _world.Write(_world.Downloads, "Certificate.txt", text);
        var duplicate = _world.Write(_world.Downloads, "Certificate (1).txt", text);
        var plan = _world.Plan();
        var item = ItemFor(plan, duplicate);
        Assert.True(item.Protected);
        Assert.Equal(JunkVerdict.Review, item.Verdict);
        Assert.NotEqual(MaidAction.Trash, item.Action);
    }

    [Fact]
    public void An_orphaned_saved_page_folder_is_junk_and_a_complete_page_moves_with_its_folder()
    {
        _world.Write(_world.Downloads, @"Lost page_files\style.css", "body{}");
        var page = _world.Write(_world.Downloads, "Recipe.html", "<!-- saved from url=(0020)https://example.com --><title>Recipe</title>banana bread recipe flour");
        _world.Write(_world.Downloads, @"Recipe_files\pic.css", "x");
        var plan = _world.Plan();
        var orphan = plan.Items.Single(i => i.IsFolder && i.Source.EndsWith("Lost page_files", StringComparison.Ordinal));
        var assets = plan.Items.Single(i => i.IsFolder && i.Source.EndsWith("Recipe_files", StringComparison.Ordinal));
        var html = ItemFor(plan, page);
        Assert.Equal(MaidAction.Trash, orphan.Action);
        Assert.Equal(html.Group, assets.Group);
        Assert.Equal(html.Action, assets.Action);
        Assert.Equal(html.Category, assets.Category);
    }

    [Fact]
    public void Files_from_one_folder_move_together_into_one_subfolder()
    {
        _world.Config.Settings.WorkSignals = ["contoso"];
        foreach (var n in new[] { "Roster", "Schedule", "Supply list" })
        {
            _world.Write(_world.Downloads, $@"Summer Camp\{n}.txt", $"Contoso summer camp {n}: youth program session, registration and enrollment for the camp class.");
        }

        var plan = _world.Plan();
        var camp = plan.Items.Where(i => i.Source.Contains(@"\Summer Camp\", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, camp.Count);
        Assert.Single(camp.Select(i => i.Group).Distinct());
        Assert.All(camp, i => Assert.Equal("Programs", i.Category));
        Assert.All(camp, i => Assert.Equal("Summer Camp", i.Subfolder));
        Assert.All(camp, i => Assert.Equal(MaidAction.Move, i.Action));
    }

    [Fact]
    public void Files_about_the_same_entity_are_grouped_and_tagged()
    {
        _world.Write(_world.Downloads, "a.txt", "Letter from Fabrikam Insurance for my family.\nFabrikam Insurance policy renewal for the car.");
        _world.Write(_world.Downloads, "b.txt", "Fabrikam Insurance claim for my family.\nFabrikam Insurance claim for the windscreen.");
        var plan = _world.Plan();
        var items = plan.Items.Where(i => i.Source.EndsWith("a.txt", StringComparison.Ordinal) || i.Source.EndsWith("b.txt", StringComparison.Ordinal)).ToList();
        Assert.NotNull(items[0].Group);
        Assert.Equal(items[0].Group, items[1].Group);
    }
}
