using System.Globalization;
using System.Net;
using System.Text;

namespace OG_opossum_command.Core.Maid;

/// <summary>
/// The plan as a page you can read (ADR-065): what needs you first, then security, then the moves grouped by
/// destination, then trash and what stays. Self-contained: no scripts, no external resources, works offline.
/// It names real files, so it stays in the data folder.
/// </summary>
public static class HtmlReport
{
    /// <summary>Renders the report.</summary>
    public static string Render(MaidPlan plan, MaidLocations locations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(locations);
        var items = plan.Items;
        var decide = items.Where(i => i.Action == MaidAction.Decide).ToList();
        var security = items.Where(i => i.Action == MaidAction.Security).ToList();
        var moves = items.Where(i => i.Action == MaidAction.Move).ToList();
        var trash = items.Where(i => i.Action == MaidAction.Trash).ToList();
        var leave = items.Where(i => i.Action == MaidAction.Leave).ToList();

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>Opossum Maid plan</title><style>").Append(Css).Append("</style></head><body><main>");
        html.Append("<h1>Opossum Maid plan</h1>");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">{E(plan.Id)} · made {E(plan.CreatedAt.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture))} · ")
            .Append(plan.Approved ? "<strong class=\"ok\">approved</strong>" : "<strong class=\"warn\">not approved yet</strong>").Append("</p>");
        html.Append("<div class=\"stats\">")
            .Append(Stat(moves.Count, "to file", moves.Count(m => m.Approved) + " ticked"))
            .Append(Stat(trash.Count, "to trash review", "30-day hold"))
            .Append(Stat(decide.Count, "need you", "side or cross-side"))
            .Append(Stat(security.Count, "security", "never touched"))
            .Append(Stat(leave.Count, "stay put", "review or filed"))
            .Append("</div>");
        html.Append("<section class=\"how\"><h2>What next</h2><ol>")
            .Append("<li>Read this page. Untick anything in <code>").Append(E(Path.GetFileName(PlanStore.PathOf(locations, plan.Id)))).Append("</code> by setting <code>\"approved\": false</code> on the item, or fix it with <code>OpossumMaid learn ").Append(E(plan.Id)).Append(" &lt;item&gt; --category X</code>.</li>")
            .Append("<li><code>OpossumMaid approve ").Append(E(plan.Id)).Append("</code>, then <code>OpossumMaid apply ").Append(E(plan.Id)).Append("</code>.</li>")
            .Append("<li>Changed your mind? <code>OpossumMaid undo &lt;run&gt;</code> puts everything back. Nothing is ever deleted.</li>")
            .Append("</ol></section>");

        if (decide.Count > 0)
        {
            html.Append("<section><h2>Needs your decision</h2><p>Never moved until you choose. Cross-side files never move by themselves (ADR-012).</p>");
            html.Append(Table(decide, locations, showDestination: false, decideHints: plan.Id));
            html.Append("</section>");
        }

        if (security.Count > 0)
        {
            html.Append("<section class=\"security\"><h2>Security</h2><p>These look like credentials. They were not opened, hashed, moved or sent anywhere. Move them into a password manager yourself.</p>");
            html.Append(Table(security, locations, showDestination: false, decideHints: null));
            html.Append("</section>");
        }

        html.Append("<section><h2>Filing</h2>");
        if (moves.Count == 0)
        {
            html.Append("<p>Nothing to file.</p>");
        }

        foreach (var folder in moves.GroupBy(m => FolderLabel(m)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            html.Append("<h3>").Append(E(folder.Key)).Append(CultureInfo.InvariantCulture, $" <span class=\"count\">{folder.Count()}</span></h3>");
            html.Append(Table(folder.ToList(), locations, showDestination: true, decideHints: null));
        }

        html.Append("</section>");
        if (trash.Count > 0)
        {
            html.Append("<section><h2>Trash review</h2><p>Moved to <code>").Append(E(MaidLocations.TrashFolderName)).Append("</code> in the folder they came from, held 30 days. Only <code>OpossumMaid purge --confirm</code> sends them to the Recycle Bin after that.</p>");
            html.Append(Table(trash, locations, showDestination: false, decideHints: null));
            html.Append("</section>");
        }

        if (leave.Count > 0)
        {
            html.Append("<section><h2>Staying put</h2>");
            html.Append(Table(leave, locations, showDestination: false, decideHints: null));
            html.Append("</section>");
        }

        if (plan.Notes.Count > 0)
        {
            html.Append("<section><h2>Notes</h2><ul>");
            foreach (var note in plan.Notes)
            {
                html.Append("<li>").Append(E(note)).Append("</li>");
            }

            html.Append("</ul></section>");
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }

    /// <summary>Writes the report next to the plan and returns its path.</summary>
    public static string Write(MaidPlan plan, MaidLocations locations)
    {
        var path = PlanStore.ReportPathOf(locations, plan.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Exports.AtomicFile.WriteAllText(path, Render(plan, locations));
        return path;
    }

    private static string FolderLabel(PlanItem item) =>
        item.Subfolder is null ? $"{item.Side} › {item.Category}" : $"{item.Side} › {item.Category} › {item.Subfolder}";

    private static string Stat(int value, string label, string hint) =>
        string.Create(CultureInfo.InvariantCulture, $"<div class=\"stat\"><span class=\"n\">{value}</span><span>{E(label)}</span><small>{E(hint)}</small></div>");

    private static string Table(IReadOnlyList<PlanItem> items, MaidLocations locations, bool showDestination, string? decideHints)
    {
        var html = new StringBuilder("<table><thead><tr><th scope=\"col\">Item</th><th scope=\"col\">File</th><th scope=\"col\">What it is</th><th scope=\"col\">Sure</th><th scope=\"col\">Tags</th><th scope=\"col\">Why</th></tr></thead><tbody>");
        foreach (var item in items)
        {
            var original = Path.GetFileName(item.Source);
            var renamed = showDestination && item.Destination is not null && !string.Equals(Path.GetFileName(item.Destination), original, StringComparison.Ordinal)
                ? $"<br><span class=\"arrow\">→ {E(Path.GetFileName(item.Destination))}</span>"
                : string.Empty;
            var tick = item.Approved ? "<span class=\"tick\" title=\"ticked\">✓</span> " : "<span class=\"untick\" title=\"not ticked\">○</span> ";
            var from = Path.GetDirectoryName(Path.GetRelativePath(item.SourceRoot, item.Source));
            var where = $"<br><small>in {E(Path.GetFileName(item.SourceRoot))}{(string.IsNullOrEmpty(from) ? string.Empty : "\\" + E(from))}</small>";
            var hint = decideHints is null ? string.Empty
                : $"<br><small>Probably {E(item.LikelySide.ToString())}. Try: <code>learn {E(decideHints)} {E(item.Id)} --side {E((item.LikelySide == MaidSide.Unassigned ? MaidSide.Personal : item.LikelySide).ToString().ToLowerInvariant())} --category {E(item.Category)}</code></small>";
            html.Append("<tr><td>").Append(tick).Append(E(item.Id)).Append("</td><td>").Append(E(original)).Append(renamed).Append(where).Append("</td><td>")
                .Append(E(item.DocType)).Append(item.Protected ? " <span class=\"badge\">kept</span>" : string.Empty)
                .Append(item.DecidedBy is "ai" or "you" or "learned" ? $" <span class=\"badge\">{E(item.DecidedBy)}</span>" : string.Empty)
                .Append("</td><td>").Append(item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)).Append("</td><td>")
                .Append(string.Join(" ", item.Tags.Select(t => $"<span class=\"tag\">{E(t)}</span>"))).Append("</td><td><ul>");
            foreach (var reason in item.Reasons)
            {
                html.Append("<li>").Append(E(reason)).Append("</li>");
            }

            html.Append("</ul>").Append(hint).Append("</td></tr>");
        }

        return html.Append("</tbody></table>").ToString();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private const string Css = """
        :root { --bg:#f7f7f8; --fg:#1b1b1f; --muted:#5b5b66; --card:#fff; --line:#e3e3e8; --accent:#5b4bdb; --warn:#a15c00; --bad:#b3261e; --ok:#1b7f3b; }
        @media (prefers-color-scheme: dark) { :root { --bg:#151518; --fg:#ececf1; --muted:#a0a0ab; --card:#1f1f24; --line:#33333b; --accent:#a99cff; --warn:#f0b35a; --bad:#ff8a80; --ok:#7bd88f; } }
        body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 "Segoe UI", system-ui, sans-serif; }
        main { max-width:1100px; margin:0 auto; padding:24px 16px 64px; }
        h1 { margin:0 0 4px; font-size:28px; } h2 { margin:32px 0 8px; font-size:20px; } h3 { margin:20px 0 6px; font-size:16px; }
        .meta, small { color:var(--muted); } .ok { color:var(--ok); } .warn { color:var(--warn); }
        .stats { display:flex; flex-wrap:wrap; gap:12px; margin:16px 0; }
        .stat { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:10px 14px; min-width:120px; display:flex; flex-direction:column; }
        .stat .n { font-size:24px; font-weight:600; }
        .how { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:4px 16px; }
        .security h2 { color:var(--bad); }
        table { width:100%; border-collapse:collapse; background:var(--card); border:1px solid var(--line); border-radius:10px; overflow:hidden; display:block; overflow-x:auto; }
        th, td { text-align:left; vertical-align:top; padding:8px 10px; border-bottom:1px solid var(--line); }
        th { font-size:13px; color:var(--muted); font-weight:600; }
        td ul { margin:0; padding-left:18px; } code { font-family:Consolas, monospace; font-size:13px; }
        .tag, .badge { display:inline-block; border:1px solid var(--line); border-radius:999px; padding:0 8px; font-size:12px; margin:1px; }
        .badge { border-color:var(--accent); color:var(--accent); } .arrow { color:var(--accent); } .tick { color:var(--ok); } .untick { color:var(--muted); }
        .count { color:var(--muted); font-weight:400; }
        """;
}
