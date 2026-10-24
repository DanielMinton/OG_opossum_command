using System.Globalization;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Maid;

/// <summary>
/// Short, plain file names (ADR-063): noise out ((1), copy, GUIDs, hashes, "final final", dates), meaningful words
/// kept, title case, at most 40 characters. A name that is only noise is rebuilt from what the file is about.
/// The file's own metadata keeps the dates, so names don't need them.
/// </summary>
public static partial class NameCleaner
{
    /// <summary>Longest name produced, without the extension.</summary>
    public const int MaxLength = 40;

    private static readonly HashSet<string> Meaningless = new(StringComparer.OrdinalIgnoreCase)
    {
        "document", "doc", "file", "scan", "scanned", "image", "img", "photo", "picture", "pic", "untitled", "new", "download", "downloads",
        "attachment", "export", "output", "print", "printout", "screenshot", "screen shot", "capture", "temp", "tmp", "test", "dsc", "pxl",
        "microsoft word", "word", "excel", "book", "presentation", "sheet", "data", "report", "form", "invoice", "receipt", "unknown", "noname", "page",
    };

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "by", "for", "in", "of", "on", "or", "the", "to", "vs", "via", "with",
    };

    /// <summary>
    /// The cleaned name (no extension). Falls back to "&lt;entity&gt; &lt;doc type&gt;", then the title, then
    /// "&lt;Doc type&gt; &lt;date&gt;" when the original name is only noise.
    /// </summary>
    /// <param name="originalStem">The file name without extension.</param>
    /// <param name="title">A metadata title, if any; used when it reads better than the name.</param>
    /// <param name="docType">The detected doc type.</param>
    /// <param name="entities">Named things found in the content.</param>
    /// <param name="keywords">Keywords found in the content.</param>
    /// <param name="date">A date for names that would otherwise be meaningless (photo taken, or modified).</param>
    public static string Clean(string originalStem, string? title, string docType, IReadOnlyList<string> entities, IReadOnlyList<string> keywords, DateTimeOffset date)
    {
        ArgumentNullException.ThrowIfNull(originalStem);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(keywords);

        var cleaned = Strip(originalStem);
        if (IsMeaningful(cleaned))
        {
            return Finish(cleaned);
        }

        if (title is not null && IsMeaningful(Strip(title)) && !LooksLikeAppTitle(title))
        {
            return Finish(Strip(title));
        }

        var label = DocTypeLabel(docType);
        bool Useful(string word) => !Meaningless.Contains(word) && !string.Equals(word, label, StringComparison.OrdinalIgnoreCase) && !string.Equals(word, docType, StringComparison.OrdinalIgnoreCase);
        var subject = entities.FirstOrDefault(e => !e.Contains('.', StringComparison.Ordinal) && Useful(e)) ?? keywords.FirstOrDefault(Useful);
        if (subject is not null && label.Length > 0)
        {
            return Finish($"{subject} {label}");
        }

        if (subject is not null)
        {
            return Finish(subject);
        }

        return Finish($"{(label.Length > 0 ? label : "File")} {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
    }

    /// <summary>Removes the noise but keeps the words: separators to spaces, copy markers, GUIDs, hashes, dates, "final".</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        s = Guid().Replace(s, " ");
        s = LongHex().Replace(s, " ");
        s = CameraPrefix().Replace(s, " ");
        s = IsoDate().Replace(s, " ");
        s = CompactDate().Replace(s, " ");
        s = WordDate().Replace(s, " ");
        s = Time().Replace(s, " ");
        s = s.Replace('_', ' ').Replace('.', ' ').Replace('+', ' ');
        if (!s.Contains(' ', StringComparison.Ordinal) && s.Count(c => c == '-') >= 2)
        {
            // "annual-budget-review" uses dashes as spaces; "e-ticket" keeps its hyphen.
            s = s.Replace('-', ' ');
        }

        s = DashSeparator().Replace(s, " ");
        s = CopyMarker().Replace(s, " ");
        s = CountSuffix().Replace(s, " ");
        s = FinalWords().Replace(s, " ");
        s = VersionMarker().Replace(s, " ");
        s = Brackets().Replace(s, " ");
        s = LoneDigits().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim(' ', '-', ',', '&');
        return s;
    }

    /// <summary>The version number in a name (<c>v2</c>, <c>version 3</c>, <c>rev 4</c>), or <c>null</c>.</summary>
    public static int? VersionOf(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var match = VersionNumber().Match(stem);
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Whether the name says "final".</summary>
    public static bool SaysFinal(string stem) => FinalWords().IsMatch(stem ?? string.Empty);

    /// <summary>The name with versions, copies and finals removed, lower case: files with the same base are versions of one thing.</summary>
    public static string BaseOf(string stem) => Strip(stem).ToLowerInvariant();

    /// <summary>Whether a configured or AI-supplied folder name is one safe level (no separators, no dots-only, no invalid characters).</summary>
    public static bool IsSafeFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 60
        && name.Trim() == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.Trim('.').Length > 0
        && !name.EndsWith('.');

    /// <summary>Makes any proposed name safe as a file name: invalid characters out, trimmed, capped. Empty in, "File" out.</summary>
    public static string Sanitise(string? name)
    {
        var s = new string((name ?? string.Empty).Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c).ToArray());
        s = Spaces().Replace(s, " ").Trim().TrimEnd('.');
        if (s.Length > MaxLength)
        {
            s = Cut(s);
        }

        return s.Length == 0 ? "File" : s;
    }

    private static bool IsMeaningful(string cleaned)
    {
        if (cleaned.Length < 3)
        {
            return false;
        }

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var meaningful = words.Where(w => w.Length >= 2 && !Meaningless.Contains(w) && !w.All(char.IsAsciiDigit) && w.Any(char.IsLetter)).ToList();
        return meaningful.Count > 0 && !Meaningless.Contains(cleaned);
    }

    private static bool LooksLikeAppTitle(string title) =>
        title.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || title.Contains(".doc", StringComparison.OrdinalIgnoreCase) || title.Contains(".pdf", StringComparison.OrdinalIgnoreCase);

    private static string Finish(string text) => Sanitise(TitleCase(text));

    /// <summary>Title case that leaves acronyms (<c>HR</c>, <c>IRS</c>), mixed-case brands (<c>iPhone</c>) and words with digits alone.</summary>
    public static string TitleCase(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var isAcronym = w.Length <= 5 && w.All(c => !char.IsLetter(c) || char.IsUpper(c)) && w.Any(char.IsLetter) && w.Length > 1;
            var isMixed = w.Skip(1).Any(char.IsUpper) && w.Any(char.IsLower);
            if (isAcronym || isMixed || w.Any(char.IsDigit))
            {
                continue;
            }

            if (i > 0 && SmallWords.Contains(w))
            {
                words[i] = w.ToLowerInvariant();
                continue;
            }

            words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    private static string Cut(string s)
    {
        var cut = s[..MaxLength];
        var space = cut.LastIndexOf(' ');
        return (space >= 20 ? cut[..space] : cut).TrimEnd(' ', '-', ',', '&');
    }

    /// <summary>Human label for a doc type, used when a name has to be rebuilt.</summary>
    public static string DocTypeLabel(string docType) => docType switch
    {
        "tax-form" => "Tax Form",
        "design-asset" => "Design",
        "saved-web-page" or "web-page" => "Web Page",
        "document" or "image" or "partial-download" => string.Empty,
        _ => TitleCase(docType.Replace('-', ' ')),
    };

    [GeneratedRegex(@"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b(?=[0-9a-fA-F]*\d)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{10,}\b")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"(?i)^(IMG|DSC|DSCN|PXL|MVIMG|VID|WIN|Screenshot|Screen Shot)[_\- ]?")]
    private static partial Regex CameraPrefix();

    [GeneratedRegex(@"\b(19|20)\d{2}[-_.](0?[1-9]|1[0-2])[-_.](0?[1-9]|[12]\d|3[01])\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<!\d)(19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(?!\d)|(?<!\d)(0?[1-9]|1[0-2])[-_.](0?[1-9]|[12]\d|3[01])[-_.](19|20)?\d{2}(?!\d)")]
    private static partial Regex CompactDate();

    [GeneratedRegex(@"(?i)\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.? ?\d{1,2}(st|nd|rd|th)?,? ?(19|20)\d{2}\b|\b\d{1,2} (jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]* (19|20)\d{2}\b")]
    private static partial Regex WordDate();

    [GeneratedRegex(@"(?<!\d)([01]\d|2[0-3])[0-5]\d[0-5]\d(?!\d)|(?<!\d)([01]?\d|2[0-3])([.:\-])[0-5]\d\3[0-5]\d(?!\d)|\bat \d{1,2}[.:]\d{2}([.:]\d{2})? ?(AM|PM)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Time();

    [GeneratedRegex(@"\s+-\s+|\s-|-\s|(?<=\d)-|-(?=\d)|-{2,}")]
    private static partial Regex DashSeparator();

    [GeneratedRegex(@"(?i)\b(copy of|copy|kopie)\b|\(\s*copy\s*\d*\s*\)")]
    private static partial Regex CopyMarker();

    [GeneratedRegex(@"\(\s*\d{1,3}\s*\)|\[\s*\d{1,3}\s*\]")]
    private static partial Regex CountSuffix();

    [GeneratedRegex(@"(?i)\b(final|finalfinal|latest|new new|updated)\b")]
    private static partial Regex FinalWords();

    [GeneratedRegex(@"(?i)\b(v|ver|version|rev|r)\s?\d{1,3}(\.\d{1,3})*\b")]
    private static partial Regex VersionMarker();

    [GeneratedRegex(@"(?i)\b(?:v|ver|version|rev)\s?(\d{1,3})\b")]
    private static partial Regex VersionNumber();

    [GeneratedRegex(@"[\(\)\[\]\{\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"(?<=^|\s)(?!(?:19|20)\d{2}(?:\s|$))\d{1,6}(?=\s|$)")]
    private static partial Regex LoneDigits();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
