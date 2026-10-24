using System.Globalization;
using OG_opossum_command.Core.Maid;
using OG_opossum_command.Core.Paths;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace OG_opossum_command.Maid;

/// <summary>
/// <c>OpossumMaid</c>: propose, then apply (ADR-058). Exit codes: 0 fine, 1 something failed, 2 bad arguments or config.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Opossum Maid: tidies the work inbox and Downloads. Proposes first; moves nothing until you approve.

          OpossumMaid scan   [--source <folder>]...        list what's there (read-only)
          OpossumMaid plan   [--source <folder>]...        make a plan + HTML report (read-only)
          OpossumMaid report <plan>                        re-render the report after edits
          OpossumMaid approve <plan>                       mark the plan approved
          OpossumMaid apply  <plan>                        do the approved moves; writes an undo journal
          OpossumMaid undo   <run>                         put a run back exactly
          OpossumMaid learn  <plan> <item> [--side work|personal] [--category <name>] [--subfolder <name>]
                                           [--name <text>] [--tag <tag>]... [--keep | --junk]
          OpossumMaid learn  --list                        show what it has learned
          OpossumMaid bundle <plan> [--include-downloads]  write the reasoning bundle (personal files only)
          OpossumMaid merge  <plan> <decisions.json>       merge Claude's decisions into the plan
          OpossumMaid purge  [--confirm]                   list (or recycle) trash past its 30-day hold

        Data: %OGOC_DATA_DIR% or %LOCALAPPDATA%\OG_opossum_command, under maid\. Nothing is ever deleted;
        junk goes to "_Trash Review" and only `purge --confirm` sends it to the Recycle Bin after 30 days.
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());
            var engine = new MaidEngine(AppPaths.FromEnvironment());
            return args[0].ToLowerInvariant() switch
            {
                "scan" => Scan(engine, options),
                "plan" => Plan(engine, options),
                "report" => Say($"Report: {engine.Report(options.Positional(0, "plan id"))}"),
                "approve" => Approve(engine, options),
                "apply" => Apply(engine, options),
                "undo" => Print(engine.Undo(options.Positional(0, "run id")), "Undo"),
                "learn" => Learn(engine, options),
                "bundle" => Bundle(engine, options),
                "merge" => Merge(engine, options),
                "purge" => Purge(engine, options),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run `OpossumMaid help` for the commands.");
            return 2;
        }
        catch (MaidConfigException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            // AppPaths: a relative OGOC_DATA_DIR and the like.
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Say(string line)
    {
        Console.WriteLine(line);
        return 0;
    }

    private static int Scan(MaidEngine engine, Options options)
    {
        var locations = engine.Locations(options.Sources);
        foreach (var source in locations.Sources)
        {
            Console.WriteLine($"Source: {source.Path} ({source.Side}{(source.IsWorkInbox ? ", work inbox" : string.Empty)})");
        }

        var scan = engine.Scan(options.Sources);
        foreach (var file in scan.Files)
        {
            var flag = file.IsCredentialName ? " [SECURITY: not opened]" : file.IsOnlineOnly ? " [online-only: not opened]" : string.Empty;
            Console.WriteLine($"  {file.RelativePath}  {file.Size.ToString("N0", CultureInfo.CurrentCulture)} bytes{flag}");
        }

        foreach (var note in scan.Notes.Concat(locations.Problems))
        {
            Console.WriteLine($"Note: {note}");
        }

        Console.WriteLine($"{scan.Files.Count} item(s). Nothing was changed.");
        return 0;
    }

    private static int Plan(MaidEngine engine, Options options)
    {
        var (plan, planPath, reportPath) = engine.Plan(options.Sources);
        var count = (MaidAction a) => plan.Items.Count(i => i.Action == a);
        Console.WriteLine($"Plan {plan.Id}: {count(MaidAction.Move)} to file, {count(MaidAction.Trash)} to trash review, {count(MaidAction.Decide)} need you, {count(MaidAction.Security)} security, {count(MaidAction.Leave)} stay put.");
        Console.WriteLine($"Plan:   {planPath}");
        Console.WriteLine($"Report: {reportPath}");
        Console.WriteLine($"Nothing moved. Read the report, then: OpossumMaid approve {plan.Id} && OpossumMaid apply {plan.Id}");
        return 0;
    }

    private static int Approve(MaidEngine engine, Options options)
    {
        var plan = engine.Approve(options.Positional(0, "plan id"));
        Console.WriteLine($"Approved {plan.Id}: {plan.Items.Count(i => i.Approved && i.Action is MaidAction.Move or MaidAction.Trash)} ticked item(s) will move on `apply`.");
        return 0;
    }

    private static int Apply(MaidEngine engine, Options options)
    {
        var (report, learned) = engine.Apply(options.Positional(0, "plan id"));
        foreach (var rule in learned)
        {
            Console.WriteLine($"Learned {rule.Id} from your edit: {Classifier.Describe(rule)} → {Describe(rule)}");
        }

        var code = Print(report, "Apply");
        Console.WriteLine($"Undo with: OpossumMaid undo {report.RunId}");
        return code;
    }

    private static int Learn(MaidEngine engine, Options options)
    {
        if (options.Flag("--list"))
        {
            foreach (var known in engine.Config.Learned.Rules)
            {
                Console.WriteLine($"{known.Id}  {Classifier.Describe(known)} → {Describe(known)}  (x{known.Hits}, from {known.From})");
            }

            Console.WriteLine($"{engine.Config.Learned.Rules.Count} learned rule(s) in {Path.Combine(engine.MaidDirectory, MaidConfig.LearnedFile)}.");
            return 0;
        }

        var side = options.Value("--side") switch
        {
            null => (MaidSide?)null,
            var s when s.Equals("work", StringComparison.OrdinalIgnoreCase) => MaidSide.Work,
            var s when s.Equals("personal", StringComparison.OrdinalIgnoreCase) => MaidSide.Personal,
            var s => throw new UsageException($"--side must be work or personal, not '{s}'."),
        };
        var verdict = options.Flag("--keep") ? "keep" : options.Flag("--junk") ? "junk" : null;
        var correction = new Correction(side, options.Value("--category"), options.Value("--subfolder"), options.Value("--name"), options.Values("--tag"), verdict);
        if (correction.IsEmpty)
        {
            throw new UsageException("Say what to correct: --side, --category, --subfolder, --name, --tag, --keep or --junk.");
        }

        var (item, rule) = engine.Learn(options.Positional(0, "plan id"), options.Positional(1, "item id"), correction);
        Console.WriteLine($"{item.Id}: now {item.Action.ToString().ToLowerInvariant()} → {item.Side} › {item.Category}{(item.Subfolder is null ? string.Empty : " › " + item.Subfolder)} as \"{item.NewName}\" (ticked).");
        if (rule is not null)
        {
            Console.WriteLine($"Learned {rule.Id}: {Classifier.Describe(rule)} → {Describe(rule)} (x{rule.Hits}).");
        }

        return 0;
    }

    private static int Bundle(MaidEngine engine, Options options)
    {
        var (path, count) = engine.Bundle(options.Positional(0, "plan id"), options.Flag("--include-downloads"));
        Console.WriteLine($"Bundle with {count} personal file(s): {path}");
        Console.WriteLine("Only personal-side files are in it (work files never are). Hand it to Claude Code with: /maid-reason " + path);
        return 0;
    }

    private static int Merge(MaidEngine engine, Options options)
    {
        foreach (var line in engine.Merge(options.Positional(0, "plan id"), options.Positional(1, "decisions file")))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine("Merged. The plan is unapproved again: read the report, then approve and apply.");
        return 0;
    }

    private static int Purge(MaidEngine engine, Options options)
    {
        var confirm = options.Flag("--confirm");
        var report = engine.Purge(confirm);
        foreach (var line in report.Lines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine(confirm
            ? $"{report.Done} sent to the Recycle Bin, {report.Skipped} still held or kept, {report.Failed} failed."
            : "Dry run: nothing touched. Add --confirm to send the listed items to the Recycle Bin.");
        return report.Failed > 0 ? 1 : 0;
    }

    private static int Print(RunReport report, string what)
    {
        foreach (var line in report.Lines)
        {
            Console.WriteLine(line);
        }

        foreach (var output in report.Outputs)
        {
            Console.WriteLine($"Wrote {output}");
        }

        Console.WriteLine($"{what} {report.RunId}: {report.Done} done, {report.Skipped} skipped, {report.Failed} failed.");
        return report.Failed > 0 ? 1 : 0;
    }

    private static string Describe(LearnedRule rule) =>
        string.Join(" / ", new[] { rule.Side?.ToString(), rule.Category, rule.Verdict, rule.Tags.Count > 0 ? "tags " + string.Join(' ', rule.Tags) : null }.Where(s => s is not null));
}

/// <summary>Bad command-line arguments.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Tiny argument parser: positionals, <c>--flag</c>, <c>--key value</c> (repeatable).</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "--list", "--keep", "--junk", "--confirm", "--include-downloads" };
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase) { "--source", "--side", "--category", "--subfolder", "--name", "--tag" };

    private readonly List<string> _positional = [];
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, string Value)> _values = [];

    public IReadOnlyList<string>? Sources => Values("--source") is { Count: > 0 } s ? s : null;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (Flags.Contains(arg))
            {
                options._flags.Add(arg);
            }
            else if (Keys.Contains(arg))
            {
                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"{arg} needs a value.");
                }

                options._values.Add((arg, args[++i]));
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"Unknown option '{arg}'.");
            }
            else
            {
                options._positional.Add(arg);
            }
        }

        return options;
    }

    public string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"Missing the {what}.");

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(string key) => _values.LastOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public IReadOnlyList<string> Values(string key) => _values.Where(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).ToList();
}
