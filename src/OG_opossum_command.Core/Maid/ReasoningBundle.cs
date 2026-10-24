using System.Text.RegularExpressions;
using OG_opossum_command.Core.History;

namespace OG_opossum_command.Core.Maid;

/// <summary>A category offered to the reasoning step.</summary>
public sealed record BundleCategory(string Name, string Description, IReadOnlyList<string> Subfolders);

/// <summary>What the rules proposed, so the reasoning step can agree or improve.</summary>
public sealed record BundleProposal(string Category, string? Subfolder, string Name, IReadOnlyList<string> Tags, string Verdict, double Confidence);

/// <summary>One personal file in a bundle. No paths, only names; the snippet is capped and scrubbed.</summary>
public sealed class BundleItem
{
    /// <summary>Plan item id; the decision must use it.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Always <c>personal</c>: nothing else is ever bundled.</summary>
    public MaidSide Side { get; set; } = MaidSide.Personal;

    /// <summary>File name as it is now.</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>Size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Last modified.</summary>
    public DateTimeOffset Modified { get; set; }

    /// <summary>Doc type from the local rules.</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>Metadata title.</summary>
    public string? Title { get; set; }

    /// <summary>The first <see cref="ReasoningBundle.MaxSnippetChars"/> characters of text, secrets and long numbers masked.</summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Image facts, for pictures.</summary>
    public ImageInfo? Image { get; set; }

    /// <summary>Names of other bundled files from the same folder or group.</summary>
    public IReadOnlyList<string> Siblings { get; set; } = [];

    /// <summary>Tags it has now.</summary>
    public IReadOnlyList<string> ExistingTags { get; set; } = [];

    /// <summary>Keywords from the local rules.</summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>The local proposal.</summary>
    public BundleProposal? Proposal { get; set; }

    /// <summary>The group it was put in, if any.</summary>
    public string? Group { get; set; }
}

/// <summary>
/// The offline hand-off to Claude Code (ADR-069, docs/MAID-HANDOFF.md). The app never calls an AI (ADR-013): it
/// writes this file, you choose to hand it to Claude Code, and Claude writes a decisions file the app merges.
/// </summary>
public sealed class ReasoningBundle
{
    /// <summary>Format id.</summary>
    public const string SchemaId = "opossum-maid.bundle/1";

    /// <summary>Longest snippet per file.</summary>
    public const int MaxSnippetChars = 2000;

    /// <summary>Format id.</summary>
    public string Schema { get; set; } = SchemaId;

    /// <summary>The plan it belongs to.</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>When it was written.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Where Claude should write the decisions.</summary>
    public string DecisionsFile { get; set; } = string.Empty;

    /// <summary>The short version of the rules for the reasoning step (the full version is the slash command).</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Personal categories.</summary>
    public IReadOnlyList<BundleCategory> Categories { get; set; } = [];

    /// <summary>The personal fallback for unsure files.</summary>
    public string Fallback { get; set; } = string.Empty;

    /// <summary>The files.</summary>
    public IReadOnlyList<BundleItem> Items { get; set; } = [];
}

/// <summary>One decision written by the reasoning step.</summary>
public sealed class MaidDecision
{
    /// <summary>The bundle item id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>A personal category (or the fallback).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>A subfolder from that category's list, or null.</summary>
    public string? Subfolder { get; set; }

    /// <summary>Short name, no extension, 40 characters at most.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Up to 8 tags.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary><c>keep</c>, <c>junk</c> or <c>review</c>.</summary>
    public string Verdict { get; set; } = "keep";

    /// <summary>0 to 1.</summary>
    public double Confidence { get; set; }

    /// <summary>One line: why.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Optional label shared by files that belong together.</summary>
    public string? Group { get; set; }
}

/// <summary>The decisions file.</summary>
public sealed class MaidDecisions
{
    /// <summary>Format id.</summary>
    public const string SchemaId = "opossum-maid.decisions/1";

    /// <summary>Format id.</summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>The plan it answers.</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>The decisions.</summary>
    public IReadOnlyList<MaidDecision> Decisions { get; set; } = [];
}

/// <summary>Writes bundles and merges decisions back into a plan.</summary>
public static partial class ReasoningHandOff
{
    private const string ShortInstructions =
        "For each item decide: what it is, what it is about, which files belong together, the best personal category " +
        "(only from 'categories', or the fallback when unsure), a short plain name (max 40 chars, no extension, no dates " +
        "unless the name would be meaningless), up to 8 lower-case tags, keep/junk/review, a confidence 0-1 and a one-line " +
        "reason. Official, financial, legal or signed documents are never junk. Write the decisions JSON to 'decisionsFile'. " +
        "Full rules: .claude/commands/maid-reason.md.";

    /// <summary>
    /// Whether an item may go in a bundle. Default: found on the personal side, not SECURITY. With
    /// <paramref name="includeUnassigned"/>: also a Downloads file that is confidently personal with no work evidence at all.
    /// </summary>
    public static bool IsEligible(PlanItem item, Classification? fresh, bool includeUnassigned)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Action == MaidAction.Security || fresh is null || fresh.IsSecurity || fresh.HasWorkEvidence || item.Side != MaidSide.Personal)
        {
            return false;
        }

        return item.Origin == MaidSide.Personal
            || (includeUnassigned && item.Origin == MaidSide.Unassigned && fresh.Side == MaidSide.Personal && fresh.SideConfidence >= 0.8);
    }

    /// <summary>Writes <c>maid\bundles\&lt;plan&gt;.bundle.json</c>, marks the bundled items in the plan, and returns the path and count.</summary>
    public static (string Path, int Count) WriteBundle(MaidPlan plan, MaidConfig config, MaidLocations locations, bool includeUnassigned, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(locations);
        var classifier = new Classifier(config);
        var picked = new List<(PlanItem Item, ScannedFile File, Classification Class)>();
        foreach (var item in plan.Items.Where(i => !i.IsFolder))
        {
            item.Bundled = false;
            if (item.Action == MaidAction.Security || !File.Exists(item.Source))
            {
                continue;
            }

            // Re-read now: the plan holds no text, and the file may have changed.
            var source = locations.Sources.FirstOrDefault(s => Paths.OneDriveBoundary.IsSamePath(s.Path, item.SourceRoot))
                ?? new MaidSource(item.SourceRoot, item.Origin, false);
            var info = new FileInfo(item.Source);
            var file = new ScannedFile
            {
                FullPath = info.FullName,
                RelativePath = Path.GetRelativePath(item.SourceRoot, info.FullName),
                Source = source,
                Size = info.Length,
                Created = info.CreationTimeUtc,
                Modified = info.LastWriteTimeUtc,
                IsCredentialName = CredentialGuard.LooksLikeCredential(info.Name, config.Settings.ExtraCredentialNames),
                IsOnlineOnly = Paths.OneDriveBoundary.IsOnlineOnly(info.Attributes),
            };
            if (file.IsCredentialName || file.IsOnlineOnly)
            {
                continue;
            }

            file.Extraction = Extractors.ContentExtractor.Extract(file.FullPath, file.Size);
            var fresh = classifier.Classify(file);
            if (IsEligible(item, fresh, includeUnassigned))
            {
                picked.Add((item, file, fresh));
            }
        }

        var personal = config.Taxonomy.Personal;
        var names = picked.ToDictionary(p => p.Item, p => p.File.Name);
        var bundle = new ReasoningBundle
        {
            PlanId = plan.Id,
            CreatedAt = (clock ?? TimeProvider.System).GetUtcNow(),
            DecisionsFile = Path.Combine(locations.BundlesDirectory, plan.Id + ".decisions.json"),
            Instructions = ShortInstructions,
            Categories = personal.Categories.Select(c => new BundleCategory(c.Name, c.Description, c.Subfolders)).ToList(),
            Fallback = personal.Fallback,
            Items = picked.Select(p => new BundleItem
            {
                Id = p.Item.Id,
                OriginalName = p.File.Name,
                Size = p.File.Size,
                Modified = p.File.Modified,
                DocType = p.Class.DocType,
                Title = p.File.Extraction?.Title,
                Snippet = Snippet(p.File.Extraction?.Text ?? string.Empty),
                Image = p.File.Extraction?.Image,
                Siblings = picked.Where(o => o.Item != p.Item && (string.Equals(Path.GetDirectoryName(o.Item.Source), Path.GetDirectoryName(p.Item.Source), StringComparison.OrdinalIgnoreCase) || (p.Item.Group is not null && o.Item.Group == p.Item.Group)))
                    .Select(o => names[o.Item]).Take(10).ToList(),
                ExistingTags = p.Item.Tags,
                Keywords = p.Class.Keywords,
                Proposal = new BundleProposal(p.Item.Category, p.Item.Subfolder, p.Item.NewName, p.Item.Tags, p.Item.Verdict.ToString().ToLowerInvariant(), p.Item.Confidence),
                Group = p.Item.Group,
            }).ToList(),
        };

        foreach (var (item, _, _) in picked)
        {
            item.Bundled = true;
        }

        var path = Path.Combine(locations.BundlesDirectory, plan.Id + ".bundle.json");
        MaidJson.Write(path, bundle);
        return (path, picked.Count);
    }

    /// <summary>The snippet: first 2,000 characters, secrets redacted (ADR-045 patterns), numbers of 9 or more digits (accounts, cards, phones) masked.</summary>
    public static string Snippet(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var capped = text.Length > ReasoningBundle.MaxSnippetChars * 2 ? text[..(ReasoningBundle.MaxSnippetChars * 2)] : text;
        var scrubbed = OutputRedactor.Redact(capped, ReasoningBundle.MaxSnippetChars * 2) ?? string.Empty;
        scrubbed = LongNumber().Replace(scrubbed, "[number]");
        return scrubbed.Length > ReasoningBundle.MaxSnippetChars ? scrubbed[..ReasoningBundle.MaxSnippetChars] : scrubbed;
    }

    /// <summary>
    /// Merges a decisions file into the plan. Only bundled personal items can change; the side never changes; categories
    /// and subfolders must come from the taxonomy; names are cleaned; protected files are never made junk. The plan
    /// goes back to unapproved. Returns one line per decision.
    /// </summary>
    public static IReadOnlyList<string> Merge(MaidPlan plan, MaidDecisions decisions, MaidConfig config)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(config);
        if (decisions.Schema != MaidDecisions.SchemaId)
        {
            throw new MaidConfigException($"The decisions file must say \"schema\": \"{MaidDecisions.SchemaId}\".");
        }

        if (!string.Equals(decisions.PlanId, plan.Id, StringComparison.Ordinal))
        {
            throw new MaidConfigException($"The decisions are for plan {decisions.PlanId}, not {plan.Id}.");
        }

        var lines = new List<string>();
        var personal = config.Taxonomy.Personal;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in decisions.Decisions)
        {
            var item = plan.Find(d.Id);
            if (item is null || !seen.Add(d.Id))
            {
                lines.Add($"{d.Id}: rejected (no such item, or a second decision for it).");
                continue;
            }

            if (!item.Bundled || item.Side != MaidSide.Personal || item.Action == MaidAction.Security)
            {
                lines.Add($"{d.Id}: rejected (it was not in the bundle; only bundled personal files take AI decisions).");
                continue;
            }

            var category = personal.Find(d.Category)?.Name ?? (string.Equals(d.Category, personal.Fallback, StringComparison.OrdinalIgnoreCase) ? personal.Fallback : null);
            if (category is null)
            {
                lines.Add($"{d.Id}: rejected (\"{d.Category}\" is not a personal category).");
                continue;
            }

            var subfolder = d.Subfolder is null ? null : personal.Find(category)?.Subfolders.FirstOrDefault(s => string.Equals(s, d.Subfolder, StringComparison.OrdinalIgnoreCase));
            if (d.Subfolder is not null && subfolder is null)
            {
                lines.Add($"{d.Id}: subfolder \"{d.Subfolder}\" ignored (not in taxonomy.json).");
            }

            var verdict = d.Verdict?.ToLowerInvariant() switch
            {
                "junk" => JunkVerdict.Junk,
                "review" => JunkVerdict.Review,
                _ => JunkVerdict.Keep,
            };
            if (verdict == JunkVerdict.Junk && item.Protected)
            {
                verdict = JunkVerdict.Review;
                lines.Add($"{d.Id}: junk downgraded to review (official, financial or legal files are always kept).");
            }

            var confidence = Math.Clamp(double.IsFinite(d.Confidence) ? d.Confidence : 0, 0, 1);
            var reason = OneLine(d.Reason);
            item.Category = category;
            item.Subfolder = subfolder;
            if (item.DocType != "saved-web-page" && !string.IsNullOrWhiteSpace(d.Name))
            {
                item.NewName = NameCleaner.Sanitise(d.Name);
            }

            item.Tags = Classifier.NormaliseTags(d.Group is { Length: > 0 } g ? [.. d.Tags, g] : d.Tags);
            item.Verdict = verdict;
            item.Action = verdict switch
            {
                JunkVerdict.Junk => MaidAction.Trash,
                JunkVerdict.Review => MaidAction.Leave,
                _ => MaidAction.Move,
            };
            item.Confidence = Math.Round(confidence, 2);
            item.Approved = item.Action != MaidAction.Leave && confidence >= config.Settings.AutoApproveConfidence;
            item.DecidedBy = "ai";
            item.Destination = null;
            item.Reasons = [.. item.Reasons, $"AI ({confidence:0.00}): {reason}"];
            lines.Add($"{d.Id}: {item.Action.ToString().ToLowerInvariant()} → {category}{(subfolder is null ? string.Empty : "\\" + subfolder)} as \"{item.NewName}\" ({confidence:0.00}).");
        }

        plan.Approved = false;
        return lines;
    }

    private static string OneLine(string? text)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 200 ? line[..200] : line;
    }

    [GeneratedRegex(@"(?<!\d)\d(?:[ \-]?\d){8,}(?!\d)")]
    private static partial Regex LongNumber();
}
