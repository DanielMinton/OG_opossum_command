using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OG_opossum_command.Core.Maid.Extractors;

/// <summary>
/// DOCX, XLSX and PPTX text without Office (ADR-059): the files are ZIP packages of XML, read with
/// <see cref="ZipArchive"/> and a DTD-free <see cref="XmlReader"/>. Every entry read is capped, so a zip bomb costs
/// at most a few megabytes.
/// </summary>
internal static class OfficeText
{
    private const int MaxEntries = 4000;
    private const int MaxEntryBytes = 4 * 1024 * 1024;
    private const int MaxSlides = 40;

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    public static Extraction Extract(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaxEntries)
        {
            return Extraction.Empty("not read: the package has too many parts");
        }

        var text = new StringBuilder();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension.StartsWith(".doc", StringComparison.Ordinal) || extension == ".dotx")
        {
            AppendText(zip.GetEntry("word/document.xml"), text);
        }
        else if (extension.StartsWith(".xls", StringComparison.Ordinal))
        {
            AppendSheetNames(zip.GetEntry("xl/workbook.xml"), text);
            AppendText(zip.GetEntry("xl/sharedStrings.xml"), text);
        }
        else
        {
            var slides = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => SlideNumber(e.FullName))
                .Take(MaxSlides);
            foreach (var slide in slides)
            {
                AppendText(slide, text);
                if (text.Length >= Extraction.MaxTextChars)
                {
                    break;
                }
            }
        }

        var core = ReadProperties(zip.GetEntry("docProps/core.xml"));
        var app = ReadProperties(zip.GetEntry("docProps/app.xml"));
        return new Extraction
        {
            Text = ContentExtractor.Cap(text.ToString()),
            Title = Get(core, "title"),
            Author = Get(core, "creator"),
            MetaKeywords = Get(core, "keywords") ?? Get(core, "subject"),
            Company = Get(app, "Company"),
            IsSigned = zip.Entries.Any(e => e.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static int SlideNumber(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }

    /// <summary>Text of every <c>t</c> element (w:t, a:t, sharedStrings t), with a break after each paragraph.</summary>
    private static void AppendText(ZipArchiveEntry? entry, StringBuilder text)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            using var reader = XmlReader.Create(Open(entry), Settings);
            while (reader.Read() && text.Length < Extraction.MaxTextChars * 2)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && !reader.IsEmptyElement)
                {
                    text.Append(reader.ReadElementContentAsString());
                }

                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "si" or "tab" or "br")
                {
                    text.Append(reader.LocalName == "tab" ? ' ' : '\n');
                }
            }
        }
        catch (XmlException)
        {
            // Truncated by the cap or malformed: keep what was read.
        }
    }

    private static void AppendSheetNames(ZipArchiveEntry? entry, StringBuilder text)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            using var reader = XmlReader.Create(Open(entry), Settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && reader.GetAttribute("name") is { } name)
                {
                    text.Append(name).Append(' ');
                }
            }
        }
        catch (XmlException)
        {
            // Keep what was read.
        }
    }

    /// <summary>Leaf elements of a docProps part, by local name.</summary>
    private static Dictionary<string, string> ReadProperties(ZipArchiveEntry? entry)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entry is null)
        {
            return values;
        }

        try
        {
            using var reader = XmlReader.Create(Open(entry), Settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && !reader.IsEmptyElement)
                {
                    var name = reader.LocalName;
                    var value = reader.ReadElementContentAsString().Trim();
                    if (value.Length > 0)
                    {
                        values.TryAdd(name, value.Length > 200 ? value[..200] : value);
                    }
                }
            }
        }
        catch (XmlException)
        {
            // Keep what was read.
        }

        return values;
    }

    private static string? Get(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Reads at most <see cref="MaxEntryBytes"/> of an entry into memory.</summary>
    private static MemoryStream Open(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < MaxEntryBytes && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxEntryBytes - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
