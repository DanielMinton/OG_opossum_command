using System.Text;

namespace OG_opossum_command.Core.Maid;

/// <summary>One file's tags in the personal sidecar index.</summary>
public sealed class TaggedFile
{
    /// <summary>Tags.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Category.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Doc type.</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>The run that filed it.</summary>
    public string Run { get; set; } = string.Empty;
}

/// <summary><c>maid\exports\personal-tags.json</c>: full path → tags.</summary>
public sealed class PersonalTagIndex
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Files by full path.</summary>
    public Dictionary<string, TaggedFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Where tags go (ADR-068). Personal files: a sidecar index, because writing <c>System.Keywords</c> changes the file's
/// bytes (and OneDrive re-uploads it) and needs the shell property system. Work files: a CSV you can bulk-apply as
/// SharePoint metadata yourself; the maid never calls SharePoint.
/// </summary>
public static class TagIndex
{
    /// <summary>File name of the personal index.</summary>
    public const string PersonalIndexFile = "personal-tags.json";

    /// <summary>Adds or removes entries for one run's personal moves.</summary>
    public static void UpdatePersonal(MaidLocations locations, IEnumerable<(string Path, TaggedFile? Tags)> changes)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(changes);
        var list = changes.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var path = Path.Combine(locations.ExportsDirectory, PersonalIndexFile);
        var index = File.Exists(path) ? MaidJson.Read<PersonalTagIndex>(path) : new PersonalTagIndex();
        var files = new Dictionary<string, TaggedFile>(index.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var (file, tags) in list)
        {
            if (tags is null)
            {
                files.Remove(file);
            }
            else
            {
                files[file] = tags;
            }
        }

        index.Files = files;
        MaidJson.Write(path, index);
    }

    /// <summary>Writes the work tags of a run as <c>maid\exports\&lt;run&gt;-work-tags.csv</c>; returns its path, or <c>null</c> when there were none.</summary>
    public static string? WriteWorkCsv(MaidLocations locations, string runId, IReadOnlyList<JournalEntry> workMoves)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(workMoves);
        if (workMoves.Count == 0 || locations.WorkRoot is null)
        {
            return null;
        }

        var csv = new StringBuilder("Path,Name,Category,DocType,Tags\r\n");
        foreach (var move in workMoves)
        {
            var relative = Path.GetRelativePath(locations.WorkRoot, move.To!);
            csv.Append(Field(relative)).Append(',')
                .Append(Field(Path.GetFileName(move.To!))).Append(',')
                .Append(Field(move.Category ?? string.Empty)).Append(',')
                .Append(Field(move.DocType ?? string.Empty)).Append(',')
                .Append(Field(string.Join(';', move.Tags ?? []))).Append("\r\n");
        }

        var path = Path.Combine(locations.ExportsDirectory, runId + "-work-tags.csv");
        Directory.CreateDirectory(locations.ExportsDirectory);
        Exports.AtomicFile.WriteAllText(path, csv.ToString());
        return path;
    }

    /// <summary>A CSV field, quoted when needed; a leading formula character is neutralised for spreadsheet safety.</summary>
    internal static string Field(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }
}
