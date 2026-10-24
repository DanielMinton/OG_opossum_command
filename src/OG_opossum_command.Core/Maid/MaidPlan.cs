using System.Globalization;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Maid;

/// <summary>What the plan first proposed for an item, kept so your edits can be learned from (ADR-066).</summary>
public sealed class ProposedSnapshot
{
    /// <summary>Proposed action.</summary>
    public MaidAction Action { get; set; }

    /// <summary>Proposed side.</summary>
    public MaidSide Side { get; set; }

    /// <summary>Proposed category.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Proposed name.</summary>
    public string NewName { get; set; } = string.Empty;
}

/// <summary>
/// One line of a plan. Edit <c>approved</c>, <c>action</c>, <c>side</c>, <c>category</c>, <c>subfolder</c>,
/// <c>newName</c> or <c>tags</c>; <c>destination</c> is recomputed from them at apply time.
/// </summary>
public sealed class PlanItem
{
    /// <summary>Id within the plan (<c>i0001</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Tick to include it when the plan is applied.</summary>
    public bool Approved { get; set; }

    /// <summary>What to do.</summary>
    public MaidAction Action { get; set; }

    /// <summary>Full path of the file or folder now.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The source folder it was found in.</summary>
    public string SourceRoot { get; set; } = string.Empty;

    /// <summary>The side of the place it was found.</summary>
    public MaidSide Origin { get; set; }

    /// <summary>Whether it is a folder (a saved page's <c>_files</c>).</summary>
    public bool IsFolder { get; set; }

    /// <summary>Size in bytes when planned.</summary>
    public long Size { get; set; }

    /// <summary>Last write time when planned; apply skips the item if it changed.</summary>
    public DateTimeOffset Modified { get; set; }

    /// <summary>SHA-256 when planned (not for credentials, online-only files, or folders).</summary>
    public string? Sha256 { get; set; }

    /// <summary>Side to file it on.</summary>
    public MaidSide Side { get; set; }

    /// <summary>The side the evidence leans to, when <see cref="Side"/> is unassigned.</summary>
    public MaidSide LikelySide { get; set; }

    /// <summary>Category folder.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Optional second level.</summary>
    public string? Subfolder { get; set; }

    /// <summary>New file name without extension.</summary>
    public string NewName { get; set; } = string.Empty;

    /// <summary>Where it would land (informational; recomputed at apply).</summary>
    public string? Destination { get; set; }

    /// <summary>What it is.</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>Tags.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Keywords (what it's about).</summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>Named things.</summary>
    public IReadOnlyList<string> Entities { get; set; } = [];

    /// <summary>Confidence, 0 to 1.</summary>
    public double Confidence { get; set; }

    /// <summary>Why, in words.</summary>
    public IReadOnlyList<string> Reasons { get; set; } = [];

    /// <summary>The group it moves with.</summary>
    public string? Group { get; set; }

    /// <summary>Keep, junk or review.</summary>
    public JunkVerdict Verdict { get; set; }

    /// <summary>Official, financial, legal or signed.</summary>
    public bool Protected { get; set; }

    /// <summary>Set by you (<c>learn --side</c>) to allow a cross-side move. Apply refuses a cross-side move without it.</summary>
    public bool CrossSideConfirmed { get; set; }

    /// <summary>Who decided the current values: <c>rules</c>, <c>learned</c>, <c>ai</c> or <c>you</c>.</summary>
    public string DecidedBy { get; set; } = "rules";

    /// <summary>Included in a reasoning bundle.</summary>
    public bool Bundled { get; set; }

    /// <summary>The first proposal, for learning from edits.</summary>
    public ProposedSnapshot Proposed { get; set; } = new();
}

/// <summary>A whole plan: written by <c>plan</c>, read by everything else. Nothing moves until <see cref="Approved"/> is true.</summary>
public sealed partial class MaidPlan
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Id (<c>plan-yyyyMMdd-HHmmss</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>You set this (or run <c>approve</c>) after reading the report. Apply refuses an unapproved plan.</summary>
    public bool Approved { get; set; }

    /// <summary>The work root when planned.</summary>
    public string? WorkRoot { get; set; }

    /// <summary>The personal root when planned.</summary>
    public string? PersonalRoot { get; set; }

    /// <summary>Sources read.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Notes from the scan (skipped folders and the like).</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];

    /// <summary>The items.</summary>
    public IReadOnlyList<PlanItem> Items { get; set; } = [];

    /// <summary>A new plan id for <paramref name="at"/>.</summary>
    public static string NewId(DateTimeOffset at) => "plan-" + at.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="id"/> is a plan or run id (letters, digits, dashes only), so it can name a file safely.</summary>
    public static bool IsSafeId(string? id) => id is not null && SafeId().IsMatch(id);

    /// <summary>Finds an item by id.</summary>
    public PlanItem? Find(string itemId) => Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-]{0,63}$")]
    private static partial Regex SafeId();
}

/// <summary>Reads and writes plans under <c>maid\plans\</c>.</summary>
public static class PlanStore
{
    /// <summary>The JSON path of a plan.</summary>
    public static string PathOf(MaidLocations locations, string planId)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (!MaidPlan.IsSafeId(planId))
        {
            throw new MaidConfigException($"'{planId}' is not a plan id (letters, digits and dashes).");
        }

        return Path.Combine(locations.PlansDirectory, planId + ".json");
    }

    /// <summary>The HTML report path of a plan.</summary>
    public static string ReportPathOf(MaidLocations locations, string planId) => Path.ChangeExtension(PathOf(locations, planId), ".html");

    /// <summary>Loads a plan by id, or by a path to its JSON file.</summary>
    public static MaidPlan Load(MaidLocations locations, string planIdOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planIdOrPath);
        var path = planIdOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(planIdOrPath) : PathOf(locations, planIdOrPath);
        if (!File.Exists(path))
        {
            throw new MaidConfigException($"No plan at {path}.");
        }

        var plan = MaidJson.Read<MaidPlan>(path);
        if (plan.SchemaVersion != 1 || !MaidPlan.IsSafeId(plan.Id))
        {
            throw new MaidConfigException($"{Path.GetFileName(path)} is not a version 1 maid plan.");
        }

        return plan;
    }

    /// <summary>Saves a plan (atomic replace).</summary>
    public static string Save(MaidLocations locations, MaidPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var path = PathOf(locations, plan.Id);
        MaidJson.Write(path, plan);
        return path;
    }
}
