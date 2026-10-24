using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OG_opossum_command.Core.Maid;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Maid;

/// <summary>
/// A throwaway world under %TEMP%: a work root with its inbox, a personal root, a Downloads folder and a data
/// folder. Nothing outside it is ever read or touched; %VAR% lookups always return null.
/// </summary>
internal sealed class MaidFixture : IDisposable
{
    public MaidFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "OG_opossum_command-tests", "maid-" + Guid.NewGuid().ToString("N"));
        WorkRoot = Path.Combine(Root, "Work");
        Inbox = Path.Combine(WorkRoot, "_Inbox");
        PersonalRoot = Path.Combine(Root, "Personal");
        Downloads = Path.Combine(Root, "Downloads");
        MaidDirectory = Path.Combine(Root, "data", "maid");
        foreach (var folder in new[] { Inbox, PersonalRoot, Downloads, MaidDirectory })
        {
            Directory.CreateDirectory(folder);
        }

        Config = MaidConfig.Default();
        Config.Settings.WorkRoot = WorkRoot;
        Config.Settings.PersonalRoot = PersonalRoot;
        Config.Settings.WorkSignals = ["contoso"];
        Config.Settings.PersonalSignals = ["my family"];
        Config.Settings.Sources =
        [
            new SourceSetting { Path = Inbox, Side = MaidSide.Work },
            new SourceSetting { Path = Downloads, Side = MaidSide.Unassigned },
        ];
        Clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    }

    public string Root { get; }

    public string WorkRoot { get; }

    public string Inbox { get; }

    public string PersonalRoot { get; }

    public string Downloads { get; }

    public string MaidDirectory { get; }

    public MaidConfig Config { get; }

    public ManualTimeProvider Clock { get; }

    public MaidLocations Locations => MaidLocations.Resolve(MaidDirectory, Config.Settings, _ => null);

    public Planner Planner => new(Config, Locations, Clock);

    public Applier Applier => new(Config, Locations, Clock);

    public MaidPlan Plan() => Planner.Plan();

    /// <summary>Writes a text file and optionally back-dates it.</summary>
    public string Write(string folder, string relative, string text, DateTimeOffset? modified = null) =>
        WriteBytes(folder, relative, Encoding.UTF8.GetBytes(text), modified);

    public string WriteBytes(string folder, string relative, byte[] bytes, DateTimeOffset? modified = null)
    {
        var path = Path.GetFullPath(Path.Combine(folder, relative));
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase); // tests only ever write inside the temp world
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (modified is { } at)
        {
            File.SetCreationTimeUtc(path, at.UtcDateTime);
            File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        }

        return path;
    }

    /// <summary>Every file outside the data folder: relative path → SHA-256.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Path.Combine(Root, "data"), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(Root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file outside the data folder, by content hash (multiset).</summary>
    public List<string> ContentHashes() => [.. Snapshot().Values.Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

/// <summary>Builds small, valid documents in memory so the tests need no binary fixtures.</summary>
internal static class DocBuilder
{
    /// <summary>A DOCX with body paragraphs and core/app properties.</summary>
    public static byte[] Docx(string[] paragraphs, string? title = null, string? company = null, bool signed = false)
    {
        var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"));
        return Zip(new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["word/document.xml"] = $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>",
            ["docProps/core.xml"] = $"<?xml version=\"1.0\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title><dc:creator>Someone</dc:creator></cp:coreProperties>",
            ["docProps/app.xml"] = $"<?xml version=\"1.0\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Company>{company}</Company></Properties>",
        }, signed ? "_xmlsignatures/sig1.xml" : null);
    }

    /// <summary>An XLSX with sheet names and shared strings.</summary>
    public static byte[] Xlsx(string[] sheets, string[] strings) => Zip(new Dictionary<string, string>
    {
        ["xl/workbook.xml"] = "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets>" + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s}\" sheetId=\"{i + 1}\"/>")) + "</sheets></workbook>",
        ["xl/sharedStrings.xml"] = "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" + string.Concat(strings.Select(s => $"<si><t>{s}</t></si>")) + "</sst>",
    });

    /// <summary>A PPTX with one text run per slide.</summary>
    public static byte[] Pptx(params string[] slides) => Zip(slides.Select((s, i) => (i, s)).ToDictionary(
        x => $"ppt/slides/slide{x.i + 1}.xml",
        x => $"<?xml version=\"1.0\"?><p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{x.s}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));

    private static byte[] Zip(Dictionary<string, string> entries, string? extraEntry = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in entries.Append(new KeyValuePair<string, string>(extraEntry ?? string.Empty, "<x/>")).Where(e => e.Key.Length > 0))
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A one-page PDF with Helvetica text lines and an Info title; offsets computed so the xref is exact.</summary>
    public static byte[] Pdf(string[] lines, string? title = null)
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 720 Td ");
        foreach (var line in lines)
        {
            content.Append('(').Append(line.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)).Append(") Tj 0 -16 Td ");
        }

        content.Append("ET");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Title ({title ?? string.Empty}) >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    /// <summary>A JPEG header with an EXIF block (make, model, date taken) and a SOF0 size. Not decodable; it doesn't need to be.</summary>
    public static byte[] Jpeg(int width, int height, string? make, string? model, string? taken)
    {
        var tiff = new List<byte>();
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Data)>();
        if (make is not null)
        {
            entries.Add((0x010F, 2, (uint)(make.Length + 1), Encoding.ASCII.GetBytes(make + "\0")));
        }

        if (model is not null)
        {
            entries.Add((0x0110, 2, (uint)(model.Length + 1), Encoding.ASCII.GetBytes(model + "\0")));
        }

        // IFD0 at 8: count, entries, next=0, then data area, then the EXIF IFD with DateTimeOriginal.
        var ifd0Size = 2 + ((entries.Count + 1) * 12) + 4;
        var dataStart = 8 + ifd0Size;
        var data = new List<byte>();
        var ifd0 = new List<byte>();
        Add16(ifd0, (ushort)(entries.Count + 1));
        foreach (var (tag, type, count, bytes) in entries)
        {
            Add16(ifd0, tag);
            Add16(ifd0, type);
            Add32(ifd0, count);
            Add32(ifd0, (uint)(dataStart + data.Count));
            data.AddRange(bytes);
        }

        var exifIfdOffset = dataStart + data.Count;
        Add16(ifd0, 0x8769);
        Add16(ifd0, 4);
        Add32(ifd0, 1);
        Add32(ifd0, (uint)exifIfdOffset);
        Add32(ifd0, 0);
        var exif = new List<byte>();
        var takenBytes = Encoding.ASCII.GetBytes((taken ?? string.Empty) + "\0");
        Add16(exif, taken is null ? (ushort)0 : (ushort)1);
        if (taken is not null)
        {
            Add16(exif, 0x9003);
            Add16(exif, 2);
            Add32(exif, (uint)takenBytes.Length);
            Add32(exif, (uint)(exifIfdOffset + 2 + 12 + 4));
        }

        Add32(exif, 0);
        if (taken is not null)
        {
            exif.AddRange(takenBytes);
        }

        tiff.AddRange("II*\0"u8.ToArray());
        Add32(tiff, 8);
        tiff.AddRange(ifd0);
        tiff.AddRange(data);
        tiff.AddRange(exif);

        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        jpeg.Add((byte)((app1.Length + 2) >> 8));
        jpeg.Add((byte)((app1.Length + 2) & 0xFF));
        jpeg.AddRange(app1);
        jpeg.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0 });
        jpeg.AddRange(new byte[] { 0xFF, 0xD9 });
        return [.. jpeg];
    }

    /// <summary>A PNG signature, IHDR and IEND (CRCs zero; the parser doesn't check them).</summary>
    public static byte[] Png(int width, int height, string? software = null)
    {
        var png = new List<byte>(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] body)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
            png.AddRange(length);
            png.AddRange(Encoding.ASCII.GetBytes(type));
            png.AddRange(body);
            png.AddRange(new byte[4]);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        if (software is not null)
        {
            Chunk("tEXt", Encoding.Latin1.GetBytes("Software\0" + software));
        }

        Chunk("IEND", []);
        return [.. png];
    }

    private static void Add16(List<byte> list, ushort value)
    {
        list.Add((byte)value);
        list.Add((byte)(value >> 8));
    }

    private static void Add32(List<byte> list, uint value)
    {
        list.Add((byte)value);
        list.Add((byte)(value >> 8));
        list.Add((byte)(value >> 16));
        list.Add((byte)(value >> 24));
    }
}
