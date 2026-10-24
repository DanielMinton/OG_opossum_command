namespace OG_opossum_command.Core.Maid;

/// <summary>A correction you make to one plan item.</summary>
/// <param name="Side">The side it belongs on.</param>
/// <param name="Category">The category it belongs in.</param>
/// <param name="Subfolder">A second-level folder (one name), or empty to clear it.</param>
/// <param name="Name">The name you want (no extension).</param>
/// <param name="Tags">Tags to add.</param>
/// <param name="Verdict"><c>keep</c> or <c>junk</c>.</param>
public sealed record Correction(MaidSide? Side = null, string? Category = null, string? Subfolder = null, string? Name = null, IReadOnlyList<string>? Tags = null, string? Verdict = null)
{
    /// <summary>Nothing to correct (no field set, no tags).</summary>
    public bool IsEmpty => Side is null && Category is null && Subfolder is null && Name is null && Verdict is null && Tags is not { Count: > 0 };
}

/// <summary>
/// Learns from your corrections (ADR-066): each one edits the plan item and records a rule such as "files mentioning
/// Contoso Bank are Personal / Money". Repeating a correction raises the rule's weight; a contradicting one replaces it.
/// The first week of corrections does most of the teaching.
/// </summary>
public sealed class Learner
{
    private readonly MaidConfig _config;
    private readonly string _maidDirectory;
    private readonly TimeProvider _clock;

    /// <summary>Creates a learner that saves to <paramref name="maidDirectory"/>.</summary>
    public Learner(MaidConfig config, string maidDirectory, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(maidDirectory);
        _config = config;
        _maidDirectory = maidDirectory;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Applies <paramref name="correction"/> to the item, records the rule, saves <c>learned.json</c>. Returns the rule.</summary>
    /// <exception cref="MaidConfigException">The side or category doesn't exist.</exception>
    public LearnedRule? Correct(MaidPlan plan, PlanItem item, Correction correction)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(correction);
        if (correction.IsEmpty)
        {
            throw new MaidConfigException("Say what to correct: --side, --category, --subfolder, --name, --tag, --keep or --junk.");
        }

        if (item.Action == MaidAction.Security)
        {
            throw new MaidConfigException($"{item.Id} is a SECURITY item; the maid never touches it. Move it yourself (into a password manager, ideally).");
        }

        var side = correction.Side ?? (item.Side == MaidSide.Unassigned ? item.LikelySide : item.Side);
        if (correction.Side is MaidSide.Unassigned)
        {
            throw new MaidConfigException("--side must be work or personal.");
        }

        string? category = null;
        if (correction.Category is not null)
        {
            if (side == MaidSide.Unassigned)
            {
                throw new MaidConfigException("Choose a side first (--side work|personal).");
            }

            var taxonomy = _config.Taxonomy.For(side);
            category = taxonomy.Find(correction.Category)?.Name
                ?? (string.Equals(correction.Category, taxonomy.Fallback, StringComparison.OrdinalIgnoreCase) ? taxonomy.Fallback : null)
                ?? throw new MaidConfigException($"\"{correction.Category}\" is not a {side} category. Choose one of: {string.Join(", ", taxonomy.Categories.Select(c => c.Name))}.");
        }

        if (correction.Subfolder is { Length: > 0 } sub && !NameCleaner.IsSafeFolderName(sub))
        {
            throw new MaidConfigException("--subfolder must be one plain folder name.");
        }

        if (correction.Verdict is not (null or "keep" or "junk"))
        {
            throw new MaidConfigException("Use --keep or --junk.");
        }

        // Edit the item.
        if (correction.Side is { } newSide)
        {
            item.Side = newSide;
            item.LikelySide = newSide;
            item.CrossSideConfirmed = item.Origin != MaidSide.Unassigned && newSide != item.Origin;
            if (correction.Category is null)
            {
                category ??= _config.Taxonomy.For(newSide).Find(item.Category)?.Name ?? _config.Taxonomy.For(newSide).Fallback;
            }
        }

        if (category is not null)
        {
            item.Category = category;
            if (item.Side == MaidSide.Unassigned)
            {
                // Naming a category of the likely side accepts that side (a Downloads file: no wall is crossed).
                item.Side = side;
            }
        }

        if (correction.Subfolder is not null)
        {
            item.Subfolder = correction.Subfolder.Length == 0 ? null : correction.Subfolder;
        }

        if (correction.Name is not null)
        {
            item.NewName = NameCleaner.Sanitise(correction.Name);
        }

        if (correction.Tags is { Count: > 0 })
        {
            item.Tags = Classifier.NormaliseTags([.. correction.Tags, .. item.Tags]);
        }

        if (correction.Verdict == "junk")
        {
            item.Action = MaidAction.Trash;
            item.Verdict = JunkVerdict.Junk;
        }
        else if (item.Side is MaidSide.Work or MaidSide.Personal && (correction.Verdict == "keep" || item.Action is MaidAction.Decide || correction.Side is not null || correction.Category is not null))
        {
            item.Action = MaidAction.Move;
            item.Verdict = JunkVerdict.Keep;
        }

        item.Approved = item.Action is MaidAction.Move or MaidAction.Trash;
        item.DecidedBy = "you";
        item.Reasons = [.. item.Reasons, $"You corrected it ({Describe(correction)})."];
        item.Destination = null;

        var rule = Record(plan.Id, item, correction.Side, category, correction.Tags, correction.Verdict);
        _config.SaveLearned(_maidDirectory);
        return rule;
    }

    /// <summary>
    /// Learns from edits you made by hand in the plan file (category, side or action changed from the proposal).
    /// A hand edit of the side does not confirm a cross-side move; only <c>learn --side</c> does.
    /// </summary>
    public IReadOnlyList<LearnedRule> HarvestEdits(MaidPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var rules = new List<LearnedRule>();
        foreach (var item in plan.Items.Where(i => i.DecidedBy != "you" && i.Action != MaidAction.Security))
        {
            var sideChanged = item.Side != item.Proposed.Side && item.Side is MaidSide.Work or MaidSide.Personal;
            var categoryChanged = !string.Equals(item.Category, item.Proposed.Category, StringComparison.OrdinalIgnoreCase) && item.Side is MaidSide.Work or MaidSide.Personal
                && _config.Taxonomy.For(item.Side).Find(item.Category) is not null;
            string? verdict = item.Proposed.Action != MaidAction.Trash && item.Action == MaidAction.Trash ? "junk"
                : item.Proposed.Action == MaidAction.Trash && item.Action == MaidAction.Move ? "keep" : null;
            if (!sideChanged && !categoryChanged && verdict is null)
            {
                continue;
            }

            item.DecidedBy = "you";
            if (Record(plan.Id, item, sideChanged ? item.Side : null, categoryChanged ? item.Category : null, null, verdict) is { } rule)
            {
                rules.Add(rule);
            }
        }

        if (rules.Count > 0)
        {
            _config.SaveLearned(_maidDirectory);
        }

        return rules;
    }

    private LearnedRule? Record(string planId, PlanItem item, MaidSide? side, string? category, IReadOnlyList<string>? tags, string? verdict)
    {
        if (side is null && category is null && verdict is null && tags is not { Count: > 0 })
        {
            return null;
        }

        var (match, value) = KeyOf(item);
        if (value is null)
        {
            return null;
        }

        var rules = _config.Learned.Rules.ToList();
        var existing = rules.FirstOrDefault(r => r.Match == match && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase));
        var sideToSave = side ?? (category is not null ? item.Side : null);
        if (existing is not null)
        {
            var agrees = existing.Side == (sideToSave ?? existing.Side) && string.Equals(existing.Category, category ?? existing.Category, StringComparison.OrdinalIgnoreCase) && existing.Verdict == (verdict ?? existing.Verdict);
            existing.Hits = agrees ? existing.Hits + 1 : 1;
            existing.Side = sideToSave ?? existing.Side;
            existing.Category = category ?? existing.Category;
            existing.Verdict = verdict ?? existing.Verdict;
            existing.Tags = Classifier.NormaliseTags([.. existing.Tags, .. tags ?? []]);
            existing.From = $"{planId}/{item.Id}";
            _config.Learned.Rules = rules;
            return existing;
        }

        var rule = new LearnedRule
        {
            Id = $"L{rules.Count + 1:0000}",
            Match = match,
            Value = value,
            Side = sideToSave,
            Category = category,
            Verdict = verdict,
            Tags = Classifier.NormaliseTags(tags ?? []),
            LearnedAt = _clock.GetUtcNow(),
            From = $"{planId}/{item.Id}",
        };
        rules.Add(rule);
        _config.Learned.Rules = rules;
        return rule;
    }

    /// <summary>The most specific thing the item is about: a named entity, else its top keyword, else its name.</summary>
    private static (LearnedMatch Match, string? Value) KeyOf(PlanItem item)
    {
        if (item.Entities.Count > 0)
        {
            return (LearnedMatch.Mentions, item.Entities[0].ToLowerInvariant());
        }

        if (item.Keywords.Count > 0)
        {
            return (LearnedMatch.Mentions, item.Keywords[0].ToLowerInvariant());
        }

        var stem = NameCleaner.BaseOf(Path.GetFileNameWithoutExtension(item.Source));
        return stem.Length >= 3 ? (LearnedMatch.Name, stem) : (LearnedMatch.DocType, item.DocType);
    }

    private static string Describe(Correction c) => string.Join(", ", new[]
    {
        c.Side is null ? null : $"side {c.Side}",
        c.Category is null ? null : $"category {c.Category}",
        c.Subfolder is null ? null : $"subfolder {c.Subfolder}",
        c.Name is null ? null : $"name \"{c.Name}\"",
        c.Tags is { Count: > 0 } ? $"tags {string.Join(' ', c.Tags)}" : null,
        c.Verdict,
    }.Where(s => s is not null));
}
