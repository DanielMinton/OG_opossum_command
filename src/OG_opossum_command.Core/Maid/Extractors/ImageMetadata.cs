using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Maid.Extractors;

/// <summary>
/// Image facts from the file header, with a small parser instead of WIC so Core stays headless and testable
/// (ADR-059): JPEG (SOF size, EXIF make, model, software, date taken), PNG (IHDR size, tEXt software), GIF and BMP
/// sizes. HEIC and WebP get no header facts, only the extension. Nothing is decoded.
/// </summary>
public static partial class ImageMetadata
{
    private const int MaxHeaderBytes = 512 * 1024;

    // Not a u8 string literal: "\x89" would be UTF-8 encoded as two bytes.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff",
    };

    /// <summary>Common screen and phone resolutions (either orientation).</summary>
    private static readonly HashSet<(int, int)> ScreenSizes =
    [
        (1280, 720), (1280, 800), (1280, 1024), (1366, 768), (1440, 900), (1536, 864), (1600, 900), (1680, 1050), (1920, 1080), (1920, 1200),
        (2560, 1080), (2560, 1440), (2560, 1600), (2736, 1824), (2880, 1800), (2880, 1920), (3000, 2000), (3440, 1440), (3840, 2160),
        (750, 1334), (828, 1792), (1080, 1920), (1080, 2340), (1080, 2400), (1125, 2436), (1170, 2532), (1179, 2556), (1242, 2688), (1284, 2778), (1290, 2796), (1440, 3200),
    ];

    /// <summary>Whether <paramref name="extension"/> is a picture format this class knows.</summary>
    public static bool Handles(string extension) => Extensions.Contains(extension);

    /// <summary>Reads the header facts of <paramref name="path"/>.</summary>
    public static ImageInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] head;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            head = new byte[(int)Math.Min(MaxHeaderBytes, stream.Length)];
            var total = 0;
            int read;
            while (total < head.Length && (read = stream.Read(head, total, head.Length - total)) > 0)
            {
                total += read;
            }
        }

        return Parse(head, Path.GetFileName(path));
    }

    /// <summary>Parses header bytes; <paramref name="fileName"/> feeds the screenshot and camera heuristics.</summary>
    public static ImageInfo Parse(ReadOnlySpan<byte> head, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var facts = new Facts();
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8)
        {
            facts.Format = "jpeg";
            ParseJpeg(head, facts);
        }
        else if (head.Length >= 24 && head[..8].SequenceEqual(PngSignature))
        {
            facts.Format = "png";
            ParsePng(head, facts);
        }
        else if (head.Length >= 10 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
        {
            facts.Format = "gif";
            facts.Width = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
            facts.Height = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
        }
        else if (head.Length >= 26 && head[0] == (byte)'B' && head[1] == (byte)'M')
        {
            facts.Format = "bmp";
            facts.Width = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[18..]));
            facts.Height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..]));
        }
        else
        {
            facts.Format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        }

        var screenName = ScreenshotName().IsMatch(fileName);
        var screenSoftware = facts.Software is not null && ScreenshotSoftware().IsMatch(facts.Software);
        var hasCamera = !string.IsNullOrWhiteSpace(facts.Make) || !string.IsNullOrWhiteSpace(facts.Model);
        var screenSize = ScreenSizes.Contains((facts.Width, facts.Height)) || ScreenSizes.Contains((facts.Height, facts.Width));
        var screenshot = !hasCamera && (screenName || screenSoftware || (facts.Format == "png" && screenSize));
        var cameraName = CameraName().IsMatch(fileName);
        var photo = !screenshot && (hasCamera || cameraName || facts.Format is "heic" or "heif");
        return new ImageInfo(facts.Format, facts.Width, facts.Height, facts.Taken, facts.Make, facts.Model, facts.Software, screenshot, photo);
    }

    private static void ParseJpeg(ReadOnlySpan<byte> data, Facts facts)
    {
        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF)
            {
                return;
            }

            var marker = data[i + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (length < 2 || i + 2 + length > data.Length)
            {
                return;
            }

            var segment = data.Slice(i + 4, length - 2);
            if (marker == 0xE1 && segment.Length > 6 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                ParseTiff(segment[6..], facts);
            }
            else if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && segment.Length >= 5)
            {
                facts.Height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                facts.Width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                return;
            }
            else if (marker == 0xDA)
            {
                return;
            }

            i += 2 + length;
        }
    }

    /// <summary>EXIF is a TIFF structure: IFD0 holds make, model, software, date; its EXIF sub-IFD holds the date taken.</summary>
    private static void ParseTiff(ReadOnlySpan<byte> span, Facts facts)
    {
        if (span.Length < 8)
        {
            return;
        }

        var tiff = span.ToArray();
        var little = tiff[0] == (byte)'I';
        uint U32(int at) => at >= 0 && at + 4 <= tiff.Length ? (little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(at))) : 0;
        int U16(int at) => at >= 0 && at + 2 <= tiff.Length ? (little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(at))) : 0;

        string? Ascii(int entry, byte[] data)
        {
            var count = (int)U32(entry + 4);
            if (count <= 0 || count > 256)
            {
                return null;
            }

            var offset = count <= 4 ? entry + 8 : (int)U32(entry + 8);
            if (offset < 0 || offset + count > data.Length)
            {
                return null;
            }

            var value = Encoding.ASCII.GetString(data, offset, count).TrimEnd('\0', ' ');
            return value.Length == 0 ? null : value;
        }

        var ifd = (int)U32(4);
        var exifIfd = 0;
        string? dateTime = null;
        string? original = null;
        for (var pass = 0; pass < 2 && ifd > 0 && ifd + 2 <= tiff.Length; pass++)
        {
            var count = Math.Min(U16(ifd), 200);
            for (var n = 0; n < count; n++)
            {
                var entry = ifd + 2 + (n * 12);
                if (entry + 12 > tiff.Length)
                {
                    break;
                }

                switch (U16(entry))
                {
                    case 0x010F: facts.Make = Ascii(entry, tiff); break;
                    case 0x0110: facts.Model = Ascii(entry, tiff); break;
                    case 0x0131: facts.Software = Ascii(entry, tiff); break;
                    case 0x0132: dateTime = Ascii(entry, tiff); break;
                    case 0x8769: exifIfd = (int)U32(entry + 8); break;
                    case 0x9003: original = Ascii(entry, tiff); break;
                    default: break;
                }
            }

            ifd = pass == 0 ? exifIfd : 0;
        }

        facts.Taken = ParseExifDate(original) ?? ParseExifDate(dateTime);
    }

    private static DateTimeOffset? ParseExifDate(string? value) =>
        value is not null && DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
            ? new DateTimeOffset(at)
            : null;

    private static void ParsePng(ReadOnlySpan<byte> data, Facts facts)
    {
        facts.Width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        facts.Height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        var i = 8;
        while (i + 12 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            if (length < 0 || i + 12 + length > data.Length)
            {
                return;
            }

            var type = Encoding.ASCII.GetString(data.Slice(i + 4, 4));
            if (type == "tEXt")
            {
                var chunk = data.Slice(i + 8, length);
                var zero = chunk.IndexOf((byte)0);
                if (zero > 0 && Encoding.Latin1.GetString(chunk[..zero]) == "Software")
                {
                    facts.Software = Encoding.Latin1.GetString(chunk[(zero + 1)..]).Trim();
                }
            }
            else if (type == "IDAT" || type == "IEND")
            {
                return;
            }

            i += 12 + length;
        }
    }

    [GeneratedRegex(@"(?i)(screen\s?shot|screen\s?capture|screen\s?recording|snip|capture|scrnli|^Screenshot)")]
    private static partial Regex ScreenshotName();

    [GeneratedRegex(@"(?i)(snipping|snip & sketch|sharex|greenshot|lightshot|screenshot|gnome-screenshot|spectacle)")]
    private static partial Regex ScreenshotSoftware();

    [GeneratedRegex(@"(?i)^(IMG|DSC|DSCN|PXL|MVIMG|DCIM|P\d{7}|GOPR|DJI)[_\-]?\d")]
    private static partial Regex CameraName();

    private sealed class Facts
    {
        public string Format { get; set; } = string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }

        public DateTimeOffset? Taken { get; set; }

        public string? Make { get; set; }

        public string? Model { get; set; }

        public string? Software { get; set; }
    }
}
