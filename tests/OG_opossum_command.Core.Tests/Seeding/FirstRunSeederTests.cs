using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Seeding;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Seeding;

public sealed class FirstRunSeederTests : IDisposable
{
    private readonly TempDataRoot _root = new();
    private readonly ActionRegistry _registry = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);

    [Fact]
    public void Copies_a_profiles_examples_when_its_workflows_folder_is_missing()
    {
        var work = _root.Profile(ProfileId.Work, _registry);
        var personal = _root.Profile(ProfileId.Personal, _registry);

        var results = FirstRunSeeder.Seed([work, personal], TempDataRoot.ExamplesRoot);

        Assert.Equal(2, results.Count);
        Assert.Equal(["focus-mode.json", "start-workday.json"], results[0].CopiedFiles.Select(Path.GetFileName));
        Assert.Equal(["plan-tomorrow.json"], results[1].CopiedFiles.Select(Path.GetFileName));
        Assert.Null(results[0].Reason);
        Assert.True(File.Exists(Path.Combine(work.WorkflowsDirectory, "start-workday.json")));
        Assert.False(File.Exists(Path.Combine(personal.WorkflowsDirectory, "start-workday.json")), "work examples leaked into the personal profile");
    }

    [Fact]
    public void Copies_when_the_folder_exists_but_holds_no_json()
    {
        var work = _root.Profile(ProfileId.Work, _registry);
        Directory.CreateDirectory(work.WorkflowsDirectory);
        File.WriteAllText(Path.Combine(work.WorkflowsDirectory, "readme.txt"), "not a workflow");

        var result = FirstRunSeeder.SeedProfile(work, TempDataRoot.ExamplesRoot);

        Assert.Equal(2, result.CopiedFiles.Count);
    }

    [Fact]
    public void Never_overwrites_or_adds_to_a_folder_that_already_has_workflows()
    {
        var work = _root.Profile(ProfileId.Work, _registry);
        Directory.CreateDirectory(work.WorkflowsDirectory);
        var mine = Path.Combine(work.WorkflowsDirectory, "start-workday.json");
        const string sentinel = "{ \"mine\": true }";
        File.WriteAllText(mine, sentinel);

        var result = FirstRunSeeder.SeedProfile(work, TempDataRoot.ExamplesRoot);

        Assert.Empty(result.CopiedFiles);
        Assert.Contains("already has workflow files", result.Reason, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllText(mine));
        Assert.Single(Directory.GetFiles(work.WorkflowsDirectory));
    }

    [Fact]
    public void Seeding_twice_is_a_no_op_the_second_time()
    {
        var work = _root.Profile(ProfileId.Work, _registry);

        var first = FirstRunSeeder.SeedProfile(work, TempDataRoot.ExamplesRoot);
        var edited = first.CopiedFiles[0];
        File.WriteAllText(edited, "{ \"edited\": true }");
        var second = FirstRunSeeder.SeedProfile(work, TempDataRoot.ExamplesRoot);

        Assert.Empty(second.CopiedFiles);
        Assert.Equal("{ \"edited\": true }", File.ReadAllText(edited));
    }

    [Fact]
    public void Creates_an_empty_folder_when_no_examples_ship()
    {
        var personal = _root.Profile(ProfileId.Personal, _registry);

        var result = FirstRunSeeder.SeedProfile(personal, _root.EmptyExamplesRoot);

        Assert.Empty(result.CopiedFiles);
        Assert.Contains("No examples found", result.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(personal.WorkflowsDirectory));
        Assert.Empty(Directory.GetFiles(personal.WorkflowsDirectory));
    }

    public void Dispose() => _root.Dispose();
}
