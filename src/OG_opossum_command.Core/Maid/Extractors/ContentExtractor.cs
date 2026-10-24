namespace OG_opossum_command.Core.Maid.Extractors;

/// <summary>
/// Picks the local extractor for a file by extension (ADR-059). Everything runs on this PC; nothing here opens a
/// network connection. Every extractor treats the file as untrusted input: reads are capped, failures become a note.
/// </summary>
public static class ContentExtractor
{
    /// <summary>Files larger than this are not opened for text (the name and metadata still count).</summary>
    public const long MaxBytesForText = 64L * 1024 * 1024;

    /// <summary>Longest a single extraction may take before it is abandoned.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log", ".yaml", ".yml", ".rtf", ".ics", ".vcf",
        ".cs", ".py", ".js", ".ts", ".ps1", ".sh", ".sql", ".java", ".go", ".rs", ".cpp", ".c", ".h",
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml", ".mhtml", ".mht" };

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".potx" };

    /// <summary>Extracts text and metadata from <paramref name="path"/>. Never throws for a bad file.</summary>
    public static Extraction Extract(string path, long size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        if (ImageMetadata.Handles(extension))
        {
            return Guard(() => new Extraction { Image = ImageMetadata.Read(path) });
        }

        if (size > MaxBytesForText)
        {
            return Extraction.Empty("not read for text: larger than 64 MB");
        }

        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Guard(() => PdfText.Extract(path));
        }

        if (OfficeExtensions.Contains(extension))
        {
            return Guard(() => OfficeText.Extract(path));
        }

        if (HtmlExtensions.Contains(extension))
        {
            return Guard(() => PlainText.ExtractHtml(path));
        }

        if (PlainTextExtensions.Contains(extension))
        {
            return Guard(() => PlainText.Extract(path));
        }

        return Extraction.Empty();
    }

    /// <summary>Runs one extractor with a time limit; any failure is a note, never an exception.</summary>
    private static Extraction Guard(Func<Extraction> extract)
    {
        var task = Task.Run(() =>
        {
            try
            {
                return extract();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Untrusted input: a malformed file is a note in the report, never a crash.
                return Extraction.Empty($"could not read the content ({ex.GetType().Name})");
            }
        });

        // A parser stuck on a hostile file is abandoned, not awaited forever; the CLI exits anyway.
        return task.Wait(Timeout) ? task.Result : Extraction.Empty("gave up reading the content after 10 s");
    }

    /// <summary>Caps text at <see cref="Extraction.MaxTextChars"/> and collapses runs of blank space.</summary>
    internal static string Cap(string text)
    {
        // Line breaks survive (one per line) so a phrase never runs from one line into the next.
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0);
        var collapsed = string.Join('\n', lines);
        return collapsed.Length <= Extraction.MaxTextChars ? collapsed : collapsed[..Extraction.MaxTextChars];
    }
}
