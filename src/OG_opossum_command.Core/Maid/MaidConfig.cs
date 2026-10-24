using System.Text.Json;
using System.Text.Json.Serialization;
using OG_opossum_command.Core.Exports;

namespace OG_opossum_command.Core.Maid;

/// <summary>JSON settings shared by every maid file: camelCase, enums as lower-case words, strict on read (ADR-060).</summary>
public static class MaidJson
{
    /// <summary>Options for reading hand-edited files: comments and trailing commas are fine, unknown or duplicate keys are errors.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Plain apostrophes and quotes in hand-read files; these files are never embedded in HTML or script.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Reads <paramref name="path"/> as <typeparamref name="T"/>; throws <see cref="MaidConfigException"/> with a readable message.</summary>
    public static T Read<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new MaidConfigException($"{Path.GetFileName(path)} is empty (null).");
        }
        catch (JsonException ex)
        {
            throw new MaidConfigException($"{Path.GetFileName(path)} is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>Writes <paramref name="value"/> atomically (temp file then move), so a reader never sees half a file.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Options) + Environment.NewLine);
    }
}

/// <summary>A maid config, plan, or decisions file that can't be used, with a message fit for the console.</summary>
public sealed class MaidConfigException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MaidConfigException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MaidConfigException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MaidConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A source folder in <c>rules.json</c>.</summary>
public sealed class SourceSetting
{
    /// <summary>Folder path; <c>%VAR%</c> references are expanded.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The folder's side: <c>work</c>, <c>personal</c> or <c>unassigned</c>.</summary>
    public MaidSide Side { get; set; }
}

/// <summary><c>maid\rules.json</c>: where to look, where things go, and how careful to be.</summary>
public sealed class MaidSettings
{
    /// <summary>Schema version of the file.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The personal root; <c>%USERPROFILE%\Personal</c> by default.</summary>
    public string PersonalRoot { get; set; } = @"%USERPROFILE%\Personal";

    /// <summary>The work root; the work OneDrive by default.</summary>
    public string WorkRoot { get; set; } = "%OneDriveCommercial%";

    /// <summary>Folders to tidy. Default: the work inbox and Downloads.</summary>
    public IReadOnlyList<SourceSetting> Sources { get; set; } =
    [
        new() { Path = @"%OneDriveCommercial%\_Inbox", Side = MaidSide.Work },
        new() { Path = @"%USERPROFILE%\Downloads", Side = MaidSide.Unassigned },
    ];

    /// <summary>
    /// Opt-in to re-sort work files that already sit in a work folder other than the inbox. Off by default: a file in
    /// a filed work folder may be shared or linked, and moving it breaks that (rule 4).
    /// </summary>
    public bool ReorganiseFiledWorkFiles { get; set; }

    /// <summary>Items at or above this confidence are pre-ticked in the plan.</summary>
    public double AutoApproveConfidence { get; set; } = 0.75;

    /// <summary>A Downloads file needs at least this side confidence to be placed on a side; below it, you decide.</summary>
    public double SideConfidence { get; set; } = 0.7;

    /// <summary>Screenshots older than this, never edited, count as stale.</summary>
    public int StaleScreenshotDays { get; set; } = 90;

    /// <summary>How long trash waits in <c>_Trash Review</c> before <c>purge</c> may touch it.</summary>
    public int TrashHoldDays { get; set; } = 30;

    /// <summary>Most files read in one plan.</summary>
    public int MaxFiles { get; set; } = 5000;

    /// <summary>Deepest folder level read under a source.</summary>
    public int MaxDepth { get; set; } = 6;

    /// <summary>Words, organisation names or e-mail domains that mean "this is work" (e.g. <c>contoso.com</c>). Yours, local only.</summary>
    public IReadOnlyList<string> WorkSignals { get; set; } = [];

    /// <summary>Words that mean "this is personal".</summary>
    public IReadOnlyList<string> PersonalSignals { get; set; } = [];

    /// <summary>Extra name fragments that mark a file as a credential (added to the built-in list).</summary>
    public IReadOnlyList<string> ExtraCredentialNames { get; set; } = [];
}

/// <summary>One category folder.</summary>
public sealed class CategorySetting
{
    /// <summary>Folder name (one level).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What goes here, in a sentence (also shown to the reasoning step).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Document types that belong here.</summary>
    public IReadOnlyList<string> DocTypes { get; set; } = [];

    /// <summary>Words or phrases that point here.</summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>Allowed second-level folders. A plan never goes deeper than category\subfolder.</summary>
    public IReadOnlyList<string> Subfolders { get; set; } = [];
}

/// <summary>The categories of one side.</summary>
public sealed class SideTaxonomy
{
    /// <summary>Where an unsure file of this side goes. For Work it is the inbox itself, so the file stays put.</summary>
    public string Fallback { get; set; } = string.Empty;

    /// <summary>The category folders.</summary>
    public IReadOnlyList<CategorySetting> Categories { get; set; } = [];

    /// <summary>Finds a category by name, ignoring case.</summary>
    public CategorySetting? Find(string name) =>
        Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary><c>maid\taxonomy.json</c>: a small folder tree per side; tags carry the detail (ADR-061).</summary>
public sealed class MaidTaxonomy
{
    /// <summary>Schema version of the file.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Work categories.</summary>
    public SideTaxonomy Work { get; set; } = new();

    /// <summary>Personal categories.</summary>
    public SideTaxonomy Personal { get; set; } = new();

    /// <summary>The taxonomy of <paramref name="side"/>.</summary>
    public SideTaxonomy For(MaidSide side) => side switch
    {
        MaidSide.Work => Work,
        MaidSide.Personal => Personal,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unassigned has no categories."),
    };

    /// <summary>The seeded default: about seven folders a side.</summary>
    public static MaidTaxonomy CreateDefault() => new()
    {
        Work = new SideTaxonomy
        {
            Fallback = "_Inbox",
            Categories =
            [
                Cat("Programs", "Program material: classes, camps, curricula, sign-ups, rosters.", ["schedule", "form", "presentation"], ["program", "class", "camp", "curriculum", "enrollment", "enrolment", "registration", "roster", "participants", "session", "youth", "member"]),
                Cat("Reports", "Reports, metrics, analysis, data exports.", ["report", "dataset"], ["report", "metrics", "quarterly", "annual", "analysis", "dashboard", "kpi", "summary", "results", "survey"]),
                Cat("Events", "Events: flyers, agendas, run sheets, meeting notes.", ["schedule"], ["event", "agenda", "flyer", "gala", "fundraiser", "meeting", "minutes", "conference", "workshop", "volunteer"]),
                Cat("Admin", "Admin: invoices, contracts, policies, HR, budgets, forms.", ["invoice", "receipt", "contract", "form", "statement", "certificate", "letter"], ["invoice", "budget", "policy", "procedure", "timesheet", "payroll", "contract", "vendor", "purchase order", "hr", "onboarding", "compliance", "insurance"]),
                Cat("Brand", "Logos, templates, photos and design assets.", ["design-asset", "photo"], ["logo", "brand", "template", "style guide", "font", "banner", "graphic"]),
                Cat("Archive", "Finished or superseded material worth keeping.", [], ["archive", "old", "previous", "legacy"]),
            ],
        },
        Personal = new SideTaxonomy
        {
            Fallback = "_Review",
            Categories =
            [
                Cat("Home", "House, lease, utilities, car, travel, family admin.", ["contract", "letter", "ticket"], ["lease", "rent", "landlord", "tenant", "mortgage", "utility", "electric", "water", "internet", "car", "vehicle", "travel", "itinerary", "booking", "warranty", "manual"]),
                Cat("Money", "Bank, taxes, bills, receipts, insurance.", ["invoice", "receipt", "statement", "tax-form"], ["bank", "statement", "tax", "invoice", "receipt", "payment", "bill", "insurance", "loan", "credit card", "refund", "paystub", "pay stub"]),
                Cat("Career", "CV, cover letters, certificates, training, job offers.", ["resume", "certificate"], ["resume", "curriculum vitae", "cover letter", "interview", "job offer", "certificate", "certification", "training", "course", "transcript", "reference"]),
                Cat("Health", "Medical, dental, prescriptions, fitness.", ["medical"], ["doctor", "patient", "clinic", "hospital", "prescription", "dental", "vaccination", "lab results", "appointment", "health"]),
                Cat("Hobbies", "Interests, projects, recipes, games, sports.", [], ["recipe", "guitar", "game", "hike", "garden", "craft", "knitting", "fishing", "sport"]),
                Cat("Media", "Photos, screenshots, videos, music, ebooks.", ["photo", "screenshot", "video", "audio", "ebook"], ["photo", "video", "album", "music", "wallpaper"]),
                Cat("Dev", "Code, datasets, installers worth keeping, tech notes.", ["code", "dataset", "archive"], ["github", "api", "script", "repository", "python", "dotnet", "javascript", "sql"]),
            ],
        },
    };

    private static CategorySetting Cat(string name, string description, string[] docTypes, string[] keywords) =>
        new() { Name = name, Description = description, DocTypes = docTypes, Keywords = keywords };
}

/// <summary>What a learned rule matches on.</summary>
public enum LearnedMatch
{
    /// <summary>The text, title or name mentions a word or phrase.</summary>
    Mentions,

    /// <summary>The cleaned file name contains a word or phrase.</summary>
    Name,

    /// <summary>The document type equals a value.</summary>
    DocType,
}

/// <summary>A rule learned from one of your corrections: "files mentioning X are Y".</summary>
public sealed class LearnedRule
{
    /// <summary>Stable id (<c>L0001</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What to match on.</summary>
    public LearnedMatch Match { get; set; }

    /// <summary>The word, phrase or doc type (lower case).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The side to use, when the correction set one.</summary>
    public MaidSide? Side { get; set; }

    /// <summary>The category to use, when the correction set one.</summary>
    public string? Category { get; set; }

    /// <summary>Tags to add.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary><c>keep</c> or <c>junk</c>, when the correction set one.</summary>
    public string? Verdict { get; set; }

    /// <summary>How many corrections agree with this rule; more hits, more weight.</summary>
    public int Hits { get; set; } = 1;

    /// <summary>When the rule was first learned.</summary>
    public DateTimeOffset LearnedAt { get; set; }

    /// <summary>Where it came from (<c>plan-id/item-id</c>).</summary>
    public string From { get; set; } = string.Empty;
}

/// <summary><c>maid\learned.json</c>.</summary>
public sealed class LearnedRules
{
    /// <summary>Schema version of the file.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The rules, oldest first.</summary>
    public IReadOnlyList<LearnedRule> Rules { get; set; } = [];
}

/// <summary>Loads (and on first use seeds) the three config files under <c>maid\</c>.</summary>
public sealed class MaidConfig
{
    /// <summary>File names under the maid folder.</summary>
    public const string TaxonomyFile = "taxonomy.json", RulesFile = "rules.json", LearnedFile = "learned.json";

    /// <summary>The rules.</summary>
    public required MaidSettings Settings { get; init; }

    /// <summary>The taxonomy.</summary>
    public required MaidTaxonomy Taxonomy { get; init; }

    /// <summary>The learned rules.</summary>
    public required LearnedRules Learned { get; init; }

    /// <summary>Defaults, without touching disk (tests).</summary>
    public static MaidConfig Default() => new() { Settings = new MaidSettings(), Taxonomy = MaidTaxonomy.CreateDefault(), Learned = new LearnedRules() };

    /// <summary>Reads the three files from <paramref name="maidDirectory"/>, writing the defaults for any that are missing.</summary>
    /// <exception cref="MaidConfigException">A file exists but is not valid.</exception>
    public static MaidConfig LoadOrSeed(string maidDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maidDirectory);
        Directory.CreateDirectory(maidDirectory);
        var config = new MaidConfig
        {
            Settings = ReadOrSeed(Path.Combine(maidDirectory, RulesFile), () => new MaidSettings()),
            Taxonomy = ReadOrSeed(Path.Combine(maidDirectory, TaxonomyFile), MaidTaxonomy.CreateDefault),
            Learned = ReadOrSeed(Path.Combine(maidDirectory, LearnedFile), () => new LearnedRules()),
        };
        config.Validate();
        return config;
    }

    /// <summary>Saves the learned rules.</summary>
    public void SaveLearned(string maidDirectory) => MaidJson.Write(Path.Combine(maidDirectory, LearnedFile), Learned);

    /// <summary>Checks the taxonomy shape: one-level names, no duplicates, at most 8 categories a side.</summary>
    public void Validate()
    {
        foreach (var (label, side) in new[] { ("work", Taxonomy.Work), ("personal", Taxonomy.Personal) })
        {
            if (!NameCleaner.IsSafeFolderName(side.Fallback))
            {
                throw new MaidConfigException($"taxonomy.json: the {label} fallback '{side.Fallback}' is not a single folder name.");
            }

            if (side.Categories.Count is 0 or > 8)
            {
                throw new MaidConfigException($"taxonomy.json: {label} needs 1 to 8 categories (has {side.Categories.Count}). Tags carry the detail, not more folders.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in side.Categories)
            {
                if (!NameCleaner.IsSafeFolderName(category.Name) || !seen.Add(category.Name))
                {
                    throw new MaidConfigException($"taxonomy.json: {label} category '{category.Name}' is not a unique single folder name.");
                }

                if (category.Subfolders.Any(s => !NameCleaner.IsSafeFolderName(s)))
                {
                    throw new MaidConfigException($"taxonomy.json: {label} category '{category.Name}' has a subfolder that is not a single folder name.");
                }
            }
        }

        if (Settings.SchemaVersion != 1 || Taxonomy.SchemaVersion != 1 || Learned.SchemaVersion != 1)
        {
            throw new MaidConfigException("maid config: schemaVersion must be 1.");
        }
    }

    private static T ReadOrSeed<T>(string path, Func<T> create)
    {
        if (File.Exists(path))
        {
            return MaidJson.Read<T>(path);
        }

        var value = create();
        MaidJson.Write(path, value);
        return value;
    }
}
