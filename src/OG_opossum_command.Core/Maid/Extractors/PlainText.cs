using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Maid.Extractors;

/// <summary>TXT, MD, CSV, JSON, code and HTML text: the first 256 KB, decoded by BOM or as UTF-8.</summary>
internal static partial class PlainText
{
    private const int MaxBytes = 256 * 1024;

    public static Extraction Extract(string path)
    {
        var text = ReadHead(path);
        if (string.Equals(Path.GetExtension(path), ".rtf", StringComparison.OrdinalIgnoreCase))
        {
            text = RtfControl().Replace(text, " ").Replace("{", " ", StringComparison.Ordinal).Replace("}", " ", StringComparison.Ordinal);
        }

        return new Extraction { Text = ContentExtractor.Cap(text) };
    }

    public static Extraction ExtractHtml(string path)
    {
        var html = ReadHead(path);
        var saved = html.Contains("saved from url=", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Snapshot-Content-Location", StringComparison.OrdinalIgnoreCase);
        var title = TitleTag().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        var body = ScriptOrStyle().Replace(html, " ");
        body = Tag().Replace(body, " ");
        return new Extraction
        {
            Text = ContentExtractor.Cap(WebUtility.HtmlDecode(body)),
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            IsSavedWebPage = saved || Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_files")),
        };
    }

    private static string ReadHead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(MaxBytes, stream.Length)];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, total), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace('\0', ' ');
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d* ?|\\'[0-9a-fA-F]{2}")]
    private static partial Regex RtfControl();
}
