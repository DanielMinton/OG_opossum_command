using OG_opossum_command.Core.Maid;

namespace OG_opossum_command.Core.Tests.Maid;

public sealed class LearnAndHandOffTests : IDisposable
{
    private readonly MaidFixture _world = new();

    public void Dispose() => _world.Dispose();

    private Learner Learner => new(_world.Config, _world.MaidDirectory, _world.Clock);

    [Fact]
    public void A_correction_edits_the_item_and_teaches_the_next_plan()
    {
        var first = _world.Write(_world.Downloads, "letter.txt", "Letter from Northwind Credit Union.\nNorthwind Credit Union loan approved.");
        var plan = _world.Plan();
        var item = plan.Items.Single(i => i.Source == first);
        Assert.Equal(MaidAction.Decide, item.Action);

        var rule = Learner.Correct(plan, item, new Correction(Side: MaidSide.Personal, Category: "money", Tags: ["Loan"]));
        Assert.Equal(MaidAction.Move, item.Action);
        Assert.True(item.Approved);
        Assert.Equal("Money", item.Category);
        Assert.Equal("you", item.DecidedBy);
        Assert.Contains("loan", item.Tags);
        Assert.False(item.CrossSideConfirmed); // Downloads has no side, so nothing was crossed
        Assert.NotNull(rule);
        Assert.Equal("northwind credit union", rule!.Value);

        // Saved, reloaded, and used by the next plan.
        var reloaded = MaidConfig.LoadOrSeed(_world.MaidDirectory);
        Assert.Single(reloaded.Learned.Rules);
        var second = _world.Write(_world.Downloads, "statement.txt", "Northwind Credit Union.\nNorthwind Credit Union account summary.");
        var next = new Planner(_world.Config, _world.Locations, _world.Clock).Plan().Items.Single(i => i.Source == second);
        Assert.Equal(MaidSide.Personal, next.Side);
        Assert.Equal("Money", next.Category);
        Assert.Equal("learned", next.DecidedBy);
    }

    [Fact]
    public void Repeating_a_correction_raises_its_weight_and_contradicting_it_resets()
    {
        _world.Write(_world.Downloads, "a.txt", "Fabrikam Garden Club.\nFabrikam Garden Club newsletter.");
        var plan = _world.Plan();
        var item = plan.Items.Single();
        Learner.Correct(plan, item, new Correction(Side: MaidSide.Personal, Category: "Hobbies"));
        var again = Learner.Correct(plan, item, new Correction(Side: MaidSide.Personal, Category: "Hobbies"));
        Assert.Equal(2, again!.Hits);
        var changed = Learner.Correct(plan, item, new Correction(Side: MaidSide.Personal, Category: "Home"));
        Assert.Equal(1, changed!.Hits);
        Assert.Equal("Home", changed.Category);
        Assert.Single(_world.Config.Learned.Rules);
    }

    [Fact]
    public void Choosing_the_other_side_for_an_inbox_file_confirms_the_cross_side_move()
    {
        var path = _world.Write(_world.Inbox, "x.txt", "Program budget for Contoso.");
        var plan = _world.Plan();
        var item = plan.Items.Single(i => i.Source == path);
        Learner.Correct(plan, item, new Correction(Side: MaidSide.Personal, Category: "Money"));
        Assert.True(item.CrossSideConfirmed);
    }

    [Fact]
    public void Bad_corrections_are_refused()
    {
        _world.Write(_world.Downloads, "a.txt", "something");
        _world.Write(_world.Downloads, "my passwords.txt", "x");
        var plan = _world.Plan();
        var normal = plan.Items.Single(i => i.Source.EndsWith("a.txt", StringComparison.Ordinal));
        var secret = plan.Items.Single(i => i.Action == MaidAction.Security);
        Assert.Throws<MaidConfigException>(() => Learner.Correct(plan, normal, new Correction(Side: MaidSide.Personal, Category: "Nope")));
        Assert.Throws<MaidConfigException>(() => Learner.Correct(plan, normal, new Correction(Side: MaidSide.Personal, Subfolder: @"a\b")));
        Assert.Throws<MaidConfigException>(() => Learner.Correct(plan, secret, new Correction(Side: MaidSide.Personal, Category: "Money")));

        // An empty correction (as `learn <plan> <item>` with no options, or `--tag` absent) changes nothing.
        var ticked = normal.Approved;
        Assert.Throws<MaidConfigException>(() => Learner.Correct(plan, normal, new Correction(Tags: [])));
        Assert.Equal(ticked, normal.Approved);
        Assert.NotEqual("you", normal.DecidedBy);
    }

    [Fact]
    public void Hand_edits_in_the_plan_file_are_learned_at_apply_time()
    {
        var path = _world.Write(_world.Inbox, "Gala.txt", "Fabrikam Hall gala.\nFabrikam Hall fundraiser event agenda.");
        var plan = _world.Plan();
        var item = plan.Items.Single(i => i.Source == path);
        Assert.Equal(MaidSide.Work, item.Side);
        item.Category = "Brand";
        var rules = Learner.HarvestEdits(plan);
        var rule = Assert.Single(rules);
        Assert.Equal("Brand", rule.Category);
        Assert.Equal(MaidSide.Work, rule.Side);
    }

    [Fact]
    public void The_bundle_holds_only_personal_side_files_with_capped_scrubbed_snippets()
    {
        _world.Config.Settings.Sources =
        [
            new SourceSetting { Path = _world.Inbox, Side = MaidSide.Work },
            new SourceSetting { Path = _world.Downloads, Side = MaidSide.Unassigned },
            new SourceSetting { Path = Path.Combine(_world.PersonalRoot, "_Review"), Side = MaidSide.Personal },
        ];
        var review = Path.Combine(_world.PersonalRoot, "_Review");
        var personal = _world.Write(review, "notes.txt", "Hiking trip ideas. Card 4111 1111 1111 1111 for the hut booking. " + new string('x', 5000));
        _world.Write(review, "server.txt", "home server notes\npassword: hunter2hunter2");
        _world.Write(_world.Inbox, "work.txt", "Quarterly report for Contoso. Executive summary.");
        _world.Write(_world.Downloads, "maybe.txt", "My family holiday photos list.");
        _world.Write(review, "bank recovery codes.txt", "1234");
        var plan = _world.Plan();
        var (path, count) = ReasoningHandOff.WriteBundle(plan, _world.Config, _world.Locations, includeUnassigned: false, _world.Clock);
        var bundle = MaidJson.Read<ReasoningBundle>(path);

        var only = Assert.Single(bundle.Items);
        Assert.Equal(1, count);
        Assert.Equal(MaidSide.Personal, only.Side);
        Assert.Equal("notes.txt", only.OriginalName);
        Assert.True(only.Snippet.Length <= ReasoningBundle.MaxSnippetChars);
        Assert.DoesNotContain(bundle.Items, i => i.OriginalName == "server.txt"); // secret-shaped text is SECURITY, never bundled
        Assert.DoesNotContain("4111", only.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Root, File.ReadAllText(path).Replace(@"\\", @"\", StringComparison.Ordinal).Replace(bundle.DecisionsFile, string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.True(plan.Items.Single(i => i.Source == personal).Bundled);
        Assert.Equal(ReasoningBundle.SchemaId, bundle.Schema);
    }

    [Fact]
    public void Merge_takes_valid_personal_decisions_and_refuses_the_rest()
    {
        var review = Path.Combine(_world.PersonalRoot, "_Review");
        _world.Config.Settings.Sources = [new SourceSetting { Path = review, Side = MaidSide.Personal }, new SourceSetting { Path = _world.Inbox, Side = MaidSide.Work }];
        var recipe = _world.Write(review, "r.txt", "banana bread recipe with flour and bananas");
        var cert = _world.Write(review, "c.txt", "Certificate of completion. This certifies that the holder has successfully completed first aid.");
        var work = _world.Write(_world.Inbox, "w.txt", "Quarterly report for Contoso.");
        var plan = _world.Plan();
        plan.Approved = true;
        ReasoningHandOff.WriteBundle(plan, _world.Config, _world.Locations, includeUnassigned: false, _world.Clock);
        string Id(string p) => plan.Items.Single(i => i.Source == p).Id;

        var decisions = new MaidDecisions
        {
            Schema = MaidDecisions.SchemaId,
            PlanId = plan.Id,
            Decisions =
            [
                new MaidDecision { Id = Id(recipe), Category = "hobbies", Name = "Banana Bread Recipe: the best one / ever", Tags = ["Recipe", "Baking"], Verdict = "keep", Confidence = 0.9, Reason = "A recipe." },
                new MaidDecision { Id = Id(cert), Category = "Career", Name = "First Aid Certificate", Verdict = "junk", Confidence = 0.9, Reason = "Old." },
                new MaidDecision { Id = Id(work), Category = "Money", Name = "x", Confidence = 1, Reason = "trying to move a work file" },
                new MaidDecision { Id = "i9999", Category = "Money", Name = "x", Confidence = 1 },
                new MaidDecision { Id = Id(recipe), Category = "Home", Name = "dup", Confidence = 1 },
            ],
        };
        var lines = ReasoningHandOff.Merge(plan, decisions, _world.Config);

        var r = plan.Items.Single(i => i.Source == recipe);
        Assert.Equal("Hobbies", r.Category);
        Assert.Equal(MaidSide.Personal, r.Side);
        Assert.Equal("ai", r.DecidedBy);
        Assert.DoesNotContain('/', r.NewName);
        Assert.True(r.NewName.Length <= NameCleaner.MaxLength);
        Assert.Equal(["recipe", "baking"], r.Tags);
        Assert.True(r.Approved);

        var c = plan.Items.Single(i => i.Source == cert);
        Assert.Equal(JunkVerdict.Review, c.Verdict); // protected: never junk
        Assert.NotEqual(MaidAction.Trash, c.Action);

        Assert.NotEqual("ai", plan.Items.Single(i => i.Source == work).DecidedBy);
        Assert.Contains(lines, l => l.StartsWith("i9999: rejected", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("second decision", StringComparison.Ordinal));
        Assert.False(plan.Approved); // merged plans need a fresh look
    }

    [Fact]
    public void Merge_refuses_a_decisions_file_for_another_plan_or_format()
    {
        var plan = _world.Plan();
        Assert.Throws<MaidConfigException>(() => ReasoningHandOff.Merge(plan, new MaidDecisions { Schema = "nope", PlanId = plan.Id }, _world.Config));
        Assert.Throws<MaidConfigException>(() => ReasoningHandOff.Merge(plan, new MaidDecisions { Schema = MaidDecisions.SchemaId, PlanId = "plan-other" }, _world.Config));
    }

    [Fact]
    public void Config_files_are_seeded_once_and_read_strictly()
    {
        var folder = Path.Combine(_world.Root, "fresh-maid");
        var seeded = MaidConfig.LoadOrSeed(folder);
        Assert.Equal(6, seeded.Taxonomy.Work.Categories.Count);
        Assert.Equal(7, seeded.Taxonomy.Personal.Categories.Count);
        Assert.True(File.Exists(Path.Combine(folder, MaidConfig.TaxonomyFile)));

        File.WriteAllText(Path.Combine(folder, MaidConfig.RulesFile), "{ \"schemaVersion\": 1, \"typo\": true }");
        Assert.Throws<MaidConfigException>(() => MaidConfig.LoadOrSeed(folder));
    }

    [Fact]
    public void Locations_refuse_overlapping_roots_and_mismatched_sources()
    {
        var settings = new MaidSettings
        {
            WorkRoot = _world.WorkRoot,
            PersonalRoot = Path.Combine(_world.WorkRoot, "Personal"),
            Sources = [new SourceSetting { Path = _world.Inbox, Side = MaidSide.Personal }],
        };
        var overlap = MaidLocations.Resolve(_world.MaidDirectory, settings, _ => null);
        Assert.Null(overlap.WorkRoot);
        Assert.Null(overlap.PersonalRoot);

        settings.PersonalRoot = _world.PersonalRoot;
        var mismatch = MaidLocations.Resolve(_world.MaidDirectory, settings, _ => null);
        Assert.Empty(mismatch.Sources);
        Assert.Contains(mismatch.Problems, p => p.Contains("ADR-012", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_sources_are_the_work_inbox_and_downloads()
    {
        string? Env(string name) => name switch
        {
            "OneDriveCommercial" => @"C:\Fake\OneDrive - Contoso",
            "USERPROFILE" => @"C:\Fake\User",
            _ => null,
        };
        var locations = MaidLocations.Resolve(_world.MaidDirectory, new MaidSettings(), Env);
        Assert.Equal(@"C:\Fake\OneDrive - Contoso", locations.WorkRoot);
        Assert.Equal(@"C:\Fake\User\Personal", locations.PersonalRoot);
        Assert.Collection(
            locations.Sources,
            s => Assert.Equal((@"C:\Fake\OneDrive - Contoso\_Inbox", MaidSide.Work, true), (s.Path, s.Side, s.IsWorkInbox)),
            s => Assert.Equal((@"C:\Fake\User\Downloads", MaidSide.Unassigned, false), (s.Path, s.Side, s.IsWorkInbox)));

        var noOneDrive = MaidLocations.Resolve(_world.MaidDirectory, new MaidSettings(), n => n == "USERPROFILE" ? @"C:\Fake\User" : null);
        Assert.Null(noOneDrive.WorkRoot);
        Assert.Single(noOneDrive.Sources);
    }
}
