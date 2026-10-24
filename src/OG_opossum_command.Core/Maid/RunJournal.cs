using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OG_opossum_command.Core.Maid;

/// <summary>One line of a run journal (JSON Lines, append-only, flushed per line).</summary>
public sealed class JournalEntry
{
    /// <summary><c>start</c>, <c>mkdir</c>, <c>move</c>, <c>restore</c>, <c>purge</c> or <c>end</c>.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>The plan item id.</summary>
    public string? Item { get; set; }

    /// <summary>Where the file was.</summary>
    public string? From { get; set; }

    /// <summary>Where it went.</summary>
    public string? To { get; set; }

    /// <summary>Its SHA-256 when known, checked again before undo moves it back.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Whether it is a folder.</summary>
    public bool IsFolder { get; set; }

    /// <summary>The plan action (<c>move</c> or <c>trash</c>).</summary>
    public MaidAction? Action { get; set; }

    /// <summary>The side it was filed on.</summary>
    public MaidSide? Side { get; set; }

    /// <summary><c>pending</c> (written before the move), <c>done</c>, <c>failed</c> or <c>skipped</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>A note for you.</summary>
    public string? Note { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The plan id (on <c>start</c>).</summary>
    public string? PlanId { get; set; }

    /// <summary>Tags written for the file (personal tag index, work CSV).</summary>
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>The category it was filed in.</summary>
    public string? Category { get; set; }

    /// <summary>The doc type.</summary>
    public string? DocType { get; set; }
}

/// <summary>
/// Run journals under <c>maid\runs\</c> (ADR-066): <c>&lt;run&gt;.jsonl</c> for apply, <c>&lt;run&gt;.undo.jsonl</c> for undo.
/// Every move is written as <c>pending</c> before it happens and <c>done</c> after, so a crash in between can still be undone.
/// </summary>
public sealed class RunJournal : IDisposable
{
    private static readonly JsonSerializerOptions LineOptions = new(MaidJson.Options) { WriteIndented = false };

    private readonly FileStream _stream;

    private RunJournal(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>The journal file.</summary>
    public string Path { get; }

    /// <summary>A new run id for <paramref name="at"/>.</summary>
    public static string NewRunId(DateTimeOffset at) => "run-" + at.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>The apply journal of a run.</summary>
    public static string PathOf(MaidLocations locations, string runId)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (!MaidPlan.IsSafeId(runId))
        {
            throw new MaidConfigException($"'{runId}' is not a run id.");
        }

        return System.IO.Path.Combine(locations.RunsDirectory, runId + ".jsonl");
    }

    /// <summary>The undo journal of a run.</summary>
    public static string UndoPathOf(MaidLocations locations, string runId) => System.IO.Path.ChangeExtension(PathOf(locations, runId), ".undo.jsonl");

    /// <summary>Opens a journal for appending (creating it when new).</summary>
    public static RunJournal Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return new RunJournal(path, new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    /// <summary>Appends one entry and flushes it to disk before returning.</summary>
    public void Write(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, LineOptions) + "\n");
        _stream.Write(line, 0, line.Length);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads every entry of a journal; a torn last line (crash mid-write) is ignored.</summary>
    public static IReadOnlyList<JournalEntry> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<JournalEntry>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line, LineOptions) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A torn final line from a crash; everything before it is intact.
            }
        }

        return entries;
    }

    /// <summary>Every apply run id on disk, oldest first.</summary>
    public static IReadOnlyList<string> RunIds(MaidLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (!Directory.Exists(locations.RunsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(locations.RunsDirectory, "run-*.jsonl")
            .Where(p => !p.EndsWith(".undo.jsonl", StringComparison.OrdinalIgnoreCase))
            .Select(p => System.IO.Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}
