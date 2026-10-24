using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Maid;

/// <summary>
/// The one front door to Opossum Maid for the CLI and the desktop "Tidy up" button (ADR-058). Every verb is a method
/// here; planning, reporting and bundling are read-only for your files; only <see cref="Apply"/>, <see cref="Undo"/>
/// and a confirmed <see cref="Purge"/> move anything.
/// </summary>
public sealed class MaidEngine
{
    private readonly Func<string, string?> _env;
    private readonly TimeProvider _clock;

    /// <summary>Creates the engine over a data root.</summary>
    /// <param name="paths">The app's paths (the maid folder is <see cref="IAppPaths.MaidDirectory"/>).</param>
    /// <param name="getEnvironmentVariable">Lookup for <c>%VAR%</c> in <c>rules.json</c>; tests inject one.</param>
    /// <param name="clock">Clock; tests inject one.</param>
    public MaidEngine(IAppPaths paths, Func<string, string?>? getEnvironmentVariable = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        MaidDirectory = paths.MaidDirectory;
        _env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _clock = clock ?? TimeProvider.System;
        Config = MaidConfig.LoadOrSeed(MaidDirectory);
    }

    /// <summary>The maid folder.</summary>
    public string MaidDirectory { get; }

    /// <summary>The loaded config.</summary>
    public MaidConfig Config { get; }

    /// <summary>Resolves the places; <paramref name="sources"/> replace the configured sources when given.</summary>
    public MaidLocations Locations(IReadOnlyList<string>? sources = null) => MaidLocations.Resolve(MaidDirectory, Config.Settings, _env, sources);

    /// <summary>Scan only: what is there, nothing written.</summary>
    public Scanner.ScanResult Scan(IReadOnlyList<string>? sources = null) => new Scanner(Config.Settings).Scan(Locations(sources).Sources);

    /// <summary>Makes a plan and its report. Read-only for your files.</summary>
    public (MaidPlan Plan, string PlanPath, string ReportPath) Plan(IReadOnlyList<string>? sources = null)
    {
        var locations = Locations(sources);
        var plan = new Planner(Config, locations, _clock).Plan();
        var planPath = PlanStore.Save(locations, plan);
        return (plan, planPath, HtmlReport.Write(plan, locations));
    }

    /// <summary>Loads a plan, resolving the places from the plan's own sources so its items validate.</summary>
    public (MaidPlan Plan, MaidLocations Locations) Load(string planIdOrPath)
    {
        var plan = PlanStore.Load(Locations(), planIdOrPath);
        var locations = Locations(plan.Sources);
        if (!SameRoot(plan.WorkRoot, locations.WorkRoot) || !SameRoot(plan.PersonalRoot, locations.PersonalRoot))
        {
            throw new MaidConfigException($"The work or personal root changed since {plan.Id} was made. Make a new plan.");
        }

        return (plan, locations);
    }

    /// <summary>Re-renders a plan's report (after edits, learn or merge).</summary>
    public string Report(string planId)
    {
        var (plan, locations) = Load(planId);
        RefreshDestinations(plan, locations);
        PlanStore.Save(locations, plan);
        return HtmlReport.Write(plan, locations);
    }

    /// <summary>Marks a plan approved.</summary>
    public MaidPlan Approve(string planId)
    {
        var (plan, locations) = Load(planId);
        plan.Approved = true;
        PlanStore.Save(locations, plan);
        HtmlReport.Write(plan, locations);
        return plan;
    }

    /// <summary>Applies an approved plan; edits you made by hand are learned first.</summary>
    public (RunReport Report, IReadOnlyList<LearnedRule> Learned) Apply(string planId)
    {
        var (plan, locations) = Load(planId);
        var learned = new Learner(Config, MaidDirectory, _clock).HarvestEdits(plan);
        var report = new Applier(Config, locations, _clock).Apply(plan);
        PlanStore.Save(locations, plan);
        return (report, learned);
    }

    /// <summary>Reverses a run.</summary>
    public RunReport Undo(string runId) => new Applier(Config, Locations(), _clock).Undo(runId);

    /// <summary>Lists, or with <paramref name="confirm"/> recycles, trash past its hold.</summary>
    public RunReport Purge(bool confirm, IRecycler? recycler = null) => new Applier(Config, Locations(), _clock).Purge(confirm, recycler ?? new WindowsRecycleBin());

    /// <summary>Records a correction to one item and re-renders the report.</summary>
    public (PlanItem Item, LearnedRule? Rule) Learn(string planId, string itemId, Correction correction)
    {
        var (plan, locations) = Load(planId);
        var item = plan.Find(itemId) ?? throw new MaidConfigException($"No item {itemId} in {plan.Id}.");
        var rule = new Learner(Config, MaidDirectory, _clock).Correct(plan, item, correction);
        RefreshDestinations(plan, locations);
        PlanStore.Save(locations, plan);
        HtmlReport.Write(plan, locations);
        return (item, rule);
    }

    /// <summary>Writes the reasoning bundle for a plan's personal files.</summary>
    public (string Path, int Count) Bundle(string planId, bool includeUnassigned)
    {
        var (plan, locations) = Load(planId);
        var result = ReasoningHandOff.WriteBundle(plan, Config, locations, includeUnassigned, _clock);
        PlanStore.Save(locations, plan);
        return result;
    }

    /// <summary>Merges a decisions file into a plan (the plan goes back to unapproved).</summary>
    public IReadOnlyList<string> Merge(string planId, string decisionsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionsPath);
        var (plan, locations) = Load(planId);
        var decisions = MaidJson.Read<MaidDecisions>(Path.GetFullPath(decisionsPath));
        var lines = ReasoningHandOff.Merge(plan, decisions, Config);
        RefreshDestinations(plan, locations);
        PlanStore.Save(locations, plan);
        HtmlReport.Write(plan, locations);
        return lines;
    }

    private static void RefreshDestinations(MaidPlan plan, MaidLocations locations)
    {
        foreach (var item in plan.Items)
        {
            item.Destination = Planner.DestinationOf(item, locations, "<run>");
        }
    }

    private static bool SameRoot(string? a, string? b) => a is null ? b is null : OneDriveBoundary.IsSamePath(a, b);
}
