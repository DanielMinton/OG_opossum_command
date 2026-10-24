using System.Globalization;

namespace OG_opossum_command.Core.Maid;

/// <summary>The classifier's verdict on one file, with every reason in plain words.</summary>
public sealed class Classification
{
    /// <summary>What the file is (invoice, photo, report, ...).</summary>
    public required string DocType { get; init; }

    /// <summary>What it is about.</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>Named things in it.</summary>
    public required IReadOnlyList<string> Entities { get; init; }

    /// <summary>The side it belongs on; <see cref="MaidSide.Unassigned"/> when the evidence is too thin.</summary>
    public required MaidSide Side { get; init; }

    /// <summary>How sure the side is.</summary>
    public required double SideConfidence { get; init; }

    /// <summary>The best guess when <see cref="Side"/> is unassigned (shown as "probably ...").</summary>
    public required MaidSide LikelySide { get; init; }

    /// <summary>A file found on one side that looks like it belongs on the other: never moved automatically.</summary>
    public required bool CrossSideSuspect { get; init; }

    /// <summary>Category folder on the side (or the side's fallback).</summary>
    public required string Category { get; init; }

    /// <summary>Optional second level (taxonomy subfolder).</summary>
    public string? Subfolder { get; init; }

    /// <summary>The short name, without extension.</summary>
    public required string ShortName { get; init; }

    /// <summary>Tags (lower case, hyphenated).</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>Overall confidence, 0 to 1.</summary>
    public required double Confidence { get; init; }

    /// <summary>Why, in words.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Official, financial, legal or signed: always kept.</summary>
    public required bool IsProtected { get; init; }

    /// <summary>Credential-like: never touched (rule 5).</summary>
    public required bool IsSecurity { get; init; }

    /// <summary>Anything at all pointed at work (signals, Company property, a learned work rule). Such a file is never put in a reasoning bundle.</summary>
    public bool HasWorkEvidence { get; init; }

    /// <summary>A learned rule's keep/junk verdict, if one matched.</summary>
    public string? LearnedVerdict { get; init; }

    /// <summary>Whether a learned rule decided the category or side.</summary>
    public bool UsedLearnedRule { get; init; }
}

/// <summary>
/// Decides, locally, what a file is, what it's about, which side and category it belongs to, a short name and tags
/// (ADR-061). Order: learned rules, then content rules, then the defaults. Work-side content never leaves this
/// method; there is no network and no model here (ADR-013).
/// </summary>
public sealed class Classifier
{
    private static readonly HashSet<string> PersonalLeaningTypes = new(StringComparer.Ordinal) { "resume", "medical", "tax-form", "ticket", "photo" };
    private static readonly HashSet<string> WorkLeaningTypes = new(StringComparer.Ordinal) { "report", "presentation", "schedule" };

    private readonly MaidConfig _config;

    /// <summary>Creates a classifier over one config.</summary>
    public Classifier(MaidConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>Classifies one scanned file.</summary>
    public Classification Classify(ScannedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var extraction = file.Extraction ?? Extraction.Empty();
        var stem = file.IsFolder ? file.Name : Path.GetFileNameWithoutExtension(file.Name);
        var cleanName = NameCleaner.Strip(stem);
        var text = extraction.Text;
        var title = extraction.Title ?? string.Empty;
        var lowerName = cleanName.ToLowerInvariant();
        var lowerAll = string.Join(' ', text, title, extraction.MetaKeywords, extraction.Company, cleanName).ToLowerInvariant();
        var reasons = new List<string>();

        var (docType, docConfidence, docReason) = DocTypeDetector.Detect(file, (text + " " + title).ToLowerInvariant(), lowerName);
        reasons.Add($"It's {Article(docType)} {docType}: {docReason}.");
        if (extraction.Note is { } note)
        {
            reasons.Add($"Content: {note}.");
        }

        var keywords = TextAnalysis.Keywords(text, title, cleanName);
        var entities = TextAnalysis.Entities(string.Join(". ", title, text, extraction.Company));
        if (keywords.Count > 0)
        {
            reasons.Add($"About: {string.Join(", ", keywords.Take(5))}.");
        }

        var security = file.IsCredentialName || CredentialGuard.TextLooksSecret(text);
        if (security)
        {
            reasons.Add(file.IsCredentialName
                ? "SECURITY: the name looks like a credential, so it was never opened. Put it in a password manager, then decide."
                : "SECURITY: the content holds something secret-shaped (a key, a password line). Left where it is.");
        }

        var isProtected = DocTypeDetector.Protected.Contains(docType) || extraction.IsSigned
            || TextAnalysis.Mentions(lowerAll, "signature") || TextAnalysis.Mentions(lowerAll, "notarized") || TextAnalysis.Mentions(lowerAll, "official transcript");
        if (isProtected)
        {
            reasons.Add(extraction.IsSigned ? "Digitally signed: always kept." : "Official, financial or legal: always kept.");
        }

        var learned = MatchLearned(lowerAll, lowerName, docType);
        var side = DecideSide(file, extraction, lowerAll, docType, learned, reasons);
        var placeSide = side.Side == MaidSide.Unassigned ? side.Likely : side.Side;
        var category = placeSide == MaidSide.Unassigned
            ? (Name: _config.Taxonomy.Personal.Fallback, Confidence: 0.2, Subfolder: (string?)null)
            : DecideCategory(placeSide, docType, lowerAll, learned, reasons);

        var date = extraction.Image?.Taken ?? file.Modified;
        var shortName = file.IsFolder || docType == "saved-web-page"
            ? stem // a saved page's HTML refers to its _files folder by name, so neither is renamed
            : NameCleaner.Clean(stem, extraction.Title, docType, entities, keywords, date);
        if (!string.Equals(shortName, stem, StringComparison.Ordinal))
        {
            reasons.Add($"Short name: \"{shortName}\".");
        }

        var tags = BuildTags(docType, category.Name, keywords, entities, learned, isProtected ? date : null);
        var confidence = Math.Round(Math.Min(Math.Min(side.Confidence, category.Confidence), Math.Max(docConfidence, 0.5)), 2);
        return new Classification
        {
            DocType = docType,
            Keywords = keywords,
            Entities = entities,
            Side = side.Side,
            SideConfidence = Math.Round(side.Confidence, 2),
            LikelySide = side.Likely,
            CrossSideSuspect = side.CrossSide,
            Category = category.Name,
            Subfolder = category.Subfolder,
            ShortName = shortName,
            Tags = tags,
            Confidence = confidence,
            Reasons = reasons,
            IsProtected = isProtected,
            IsSecurity = security,
            LearnedVerdict = learned.Select(r => r.Verdict).FirstOrDefault(v => v is not null),
            UsedLearnedRule = learned.Count > 0,
            HasWorkEvidence = file.Source.Side == MaidSide.Work || learned.Any(r => r.Side == MaidSide.Work)
                || _config.Settings.WorkSignals.Any(s => !string.IsNullOrWhiteSpace(s) && TextAnalysis.Mentions(lowerAll, s)),
        };
    }

    private List<LearnedRule> MatchLearned(string lowerAll, string lowerName, string docType) =>
        _config.Learned.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Value) && r.Match switch
            {
                LearnedMatch.Mentions => TextAnalysis.Mentions(lowerAll, r.Value),
                LearnedMatch.Name => TextAnalysis.Mentions(lowerName, r.Value),
                LearnedMatch.DocType => string.Equals(docType, r.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            })
            .OrderByDescending(r => r.Hits)
            .ThenByDescending(r => r.LearnedAt)
            .ToList();

    private (MaidSide Side, double Confidence, MaidSide Likely, bool CrossSide) DecideSide(
        ScannedFile file, Extraction extraction, string lowerAll, string docType, List<LearnedRule> learned, List<string> reasons)
    {
        double work = 0, personal = 0;
        var settings = _config.Settings;
        var workHits = settings.WorkSignals.Where(s => !string.IsNullOrWhiteSpace(s) && TextAnalysis.Mentions(lowerAll, s)).ToList();
        var personalHits = settings.PersonalSignals.Where(s => !string.IsNullOrWhiteSpace(s) && TextAnalysis.Mentions(lowerAll, s)).ToList();
        if (workHits.Count > 0)
        {
            work += Math.Min(0.6, 0.35 * workHits.Count);
            reasons.Add($"Work evidence: mentions {string.Join(", ", workHits.Take(3))}.");
        }

        if (personalHits.Count > 0)
        {
            personal += Math.Min(0.6, 0.35 * personalHits.Count);
            reasons.Add($"Personal evidence: mentions {string.Join(", ", personalHits.Take(3))}.");
        }

        if (extraction.Company is { } company && settings.WorkSignals.Any(s => !string.IsNullOrWhiteSpace(s) && company.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            work += 0.4;
            reasons.Add("Work evidence: the document's Company property is your organisation.");
        }

        if (learned.FirstOrDefault(r => r.Side is MaidSide.Work or MaidSide.Personal) is { } sideRule)
        {
            var weight = Math.Min(0.9, 0.6 + (0.1 * sideRule.Hits));
            if (sideRule.Side == MaidSide.Work)
            {
                work += weight;
            }
            else
            {
                personal += weight;
            }

            reasons.Add($"Learned rule {sideRule.Id}: {Describe(sideRule)} → {sideRule.Side}.");
        }

        var workFit = BestCategoryScore(MaidSide.Work, docType, lowerAll);
        var personalFit = BestCategoryScore(MaidSide.Personal, docType, lowerAll);
        work += 0.3 * workFit;
        personal += 0.3 * personalFit;
        if (PersonalLeaningTypes.Contains(docType))
        {
            personal += 0.1;
        }
        else if (WorkLeaningTypes.Contains(docType))
        {
            work += 0.1;
        }

        var likely = work > personal ? MaidSide.Work : personal > work ? MaidSide.Personal : MaidSide.Unassigned;
        var origin = file.Source.Side;
        if (origin != MaidSide.Unassigned)
        {
            var own = origin == MaidSide.Work ? work : personal;
            var other = origin == MaidSide.Work ? personal : work;
            var cross = other >= 0.7 && other > own + 0.3;
            if (cross)
            {
                reasons.Add($"Cross-side: it's on the {origin} side but looks {(origin == MaidSide.Work ? "personal" : "work")}. Needs your decision; never moved across automatically (ADR-012).");
            }
            else
            {
                reasons.Add($"Side: {origin} (that's where it was found).");
            }

            return (origin, 0.9, cross ? (origin == MaidSide.Work ? MaidSide.Personal : MaidSide.Work) : origin, cross);
        }

        var confidence = likely == MaidSide.Unassigned ? 0 : Math.Min(0.95, Math.Abs(work - personal) + 0.3);
        if (likely != MaidSide.Unassigned && confidence >= settings.SideConfidence)
        {
            reasons.Add($"Side: {likely} ({confidence.ToString("0.00", CultureInfo.InvariantCulture)}).");
            return (likely, confidence, likely, false);
        }

        reasons.Add(likely == MaidSide.Unassigned
            ? "Side: no evidence for work or personal. Needs your decision."
            : $"Side: probably {likely}, but only {confidence.ToString("0.00", CultureInfo.InvariantCulture)} sure. Needs your decision.");
        return (MaidSide.Unassigned, confidence, likely, false);
    }

    private double BestCategoryScore(MaidSide side, string docType, string lowerAll) =>
        _config.Taxonomy.For(side).Categories.Select(c => Score(c, docType, lowerAll).Score).DefaultIfEmpty(0).Max();

    private static (double Score, int Hits) Score(CategorySetting category, string docType, string lowerAll)
    {
        var typeMatch = category.DocTypes.Contains(docType, StringComparer.OrdinalIgnoreCase) ? 0.45 : 0;
        var hits = category.Keywords.Count(k => !string.IsNullOrWhiteSpace(k) && TextAnalysis.Mentions(lowerAll, k));
        return (typeMatch + Math.Min(0.45, 0.15 * hits), hits);
    }

    private (string Name, double Confidence, string? Subfolder) DecideCategory(MaidSide side, string docType, string lowerAll, List<LearnedRule> learned, List<string> reasons)
    {
        var taxonomy = _config.Taxonomy.For(side);
        var scores = taxonomy.Categories.ToDictionary(c => c.Name, c => Score(c, docType, lowerAll).Score, StringComparer.OrdinalIgnoreCase);
        var rule = learned.FirstOrDefault(r => r.Category is not null && (r.Side is null || r.Side == side) && taxonomy.Find(r.Category) is not null);
        if (rule is not null)
        {
            var name = taxonomy.Find(rule.Category!)!.Name;
            scores[name] = scores.GetValueOrDefault(name) + Math.Min(0.8, 0.5 + (0.1 * rule.Hits));
            reasons.Add($"Learned rule {rule.Id}: {Describe(rule)} → {name}.");
        }

        var ranked = scores.OrderByDescending(p => p.Value).ToList();
        if (ranked.Count == 0 || ranked[0].Value < 0.3)
        {
            reasons.Add($"Category: nothing fits well, so {taxonomy.Fallback}{(side == MaidSide.Work ? " (it stays in the inbox)" : string.Empty)}.");
            return (taxonomy.Fallback, 0.3, null);
        }

        var top = ranked[0];
        var margin = top.Value - (ranked.Count > 1 ? ranked[1].Value : 0);
        var confidence = Math.Min(0.95, top.Value) * (margin >= 0.15 ? 1 : 0.85);
        var category = taxonomy.Find(top.Key)!;
        var subfolder = category.Subfolders.FirstOrDefault(s => TextAnalysis.Mentions(lowerAll, s));
        reasons.Add($"Category: {top.Key}{(subfolder is null ? string.Empty : "\\" + subfolder)} ({category.Description.TrimEnd('.')}).");
        return (top.Key, confidence, subfolder);
    }

    private static IReadOnlyList<string> BuildTags(string docType, string category, IReadOnlyList<string> keywords, IReadOnlyList<string> entities, List<LearnedRule> learned, DateTimeOffset? yearFrom)
    {
        var tags = new List<string>();
        if (docType is not ("document" or "image"))
        {
            tags.Add(docType);
        }

        tags.AddRange(entities.Take(2));
        tags.AddRange(keywords.Where(k => !string.Equals(k, category, StringComparison.OrdinalIgnoreCase) && !string.Equals(k, docType, StringComparison.Ordinal)).Take(3));
        tags.AddRange(learned.SelectMany(r => r.Tags));
        if (yearFrom is { } at)
        {
            tags.Add(at.Year.ToString(CultureInfo.InvariantCulture));
        }

        return NormaliseTags(tags);
    }

    /// <summary>Lower case, spaces to hyphens, at most 30 characters each, no duplicates, at most 8.</summary>
    public static IReadOnlyList<string> NormaliseTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return tags
            .Select(t => new string(t.Trim().ToLowerInvariant().Replace(' ', '-').Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray()).Trim('-', '.'))
            .Where(t => t.Length >= 2)
            .Select(t => t.Length > 30 ? t[..30] : t)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
    }

    /// <summary>A learned rule in words.</summary>
    public static string Describe(LearnedRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Match switch
        {
            LearnedMatch.Name => $"names with \"{rule.Value}\"",
            LearnedMatch.DocType => $"{rule.Value} files",
            _ => $"files mentioning \"{rule.Value}\"",
        };
    }

    private static string Article(string word) => word.Length > 0 && "aeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a";
}
