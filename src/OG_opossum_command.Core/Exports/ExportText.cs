using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Exports;

/// <summary>
/// Text rules for everything that leaves the PC through OneDrive (ADR-050, ADR-052): every free-text field goes through
/// <see cref="OutputRedactor"/>, then file paths and URLs are replaced, control characters and runs of white space are
/// collapsed, and the result is cut to <see cref="MaxFieldChars"/>. Exported files never carry step output or arguments.
/// </summary>
public static partial class ExportText
{
    /// <summary>Longest free-text field in an exported file.</summary>
    public const int MaxFieldChars = 200;

    /// <summary>Placeholder for a removed file path.</summary>
    public const string PathPlaceholder = "[path]";

    /// <summary>Placeholder for a removed URL.</summary>
    public const string LinkPlaceholder = "[link]";

    /// <summary>Environment variables whose (full-path) values are removed literally from exported text.</summary>
    private static readonly string[] RootVariables = ["OneDriveCommercial", "OneDriveConsumer", "OneDrive", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "USERPROFILE"];

    /// <summary>The app version without build metadata (<c>0.2.0</c>).</summary>
    public static string AppVersion { get; } = ReadAppVersion();

    /// <summary>
    /// A scrubbed, single-line copy of <paramref name="text"/> of at most <paramref name="maxChars"/> characters;
    /// <c>null</c> stays <c>null</c> and blank becomes <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Paths are removed in this order (ADR-054): known roots (the user profile, the OneDrive roots, the data root, and
    /// the temp and app-data folders) literally and case-insensitively; a quoted path up to its closing quote; an
    /// unquoted path up to the next quote, <c>&lt; &gt; |</c>, line break, or punctuation followed by a space or the
    /// end, because folder and file names may contain spaces (<c>OneDrive - Org\HR\Salary review.xlsx</c>); and finally
    /// any remaining word with a backslash. Removing a few words after a path is the safe direction.
    /// </remarks>
    /// <param name="paths">Supplies the data and OneDrive roots to remove; <c>null</c> uses the environment's roots only.</param>
    public static string? Scrub(string? text, int maxChars = MaxFieldChars, IAppPaths? paths = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 2);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Redact first (it understands key=value and URL query secrets), with a cap well above ours so its truncation marker never shows.
        var scrubbed = OutputRedactor.Redact(text, OutputRedactor.MaxStoredChars) ?? string.Empty;
        scrubbed = Url().Replace(scrubbed, LinkPlaceholder);
        foreach (var root in KnownRoots(paths))
        {
            scrubbed = scrubbed.Replace(root, PathPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        scrubbed = QuotedPath().Replace(scrubbed, "${open}" + PathPlaceholder + "${close}");
        scrubbed = UnquotedPath().Replace(scrubbed, PathPlaceholder);
        scrubbed = WordWithBackslash().Replace(scrubbed, PathPlaceholder);
        scrubbed = SingleLine(scrubbed);
        if (scrubbed.Length == 0)
        {
            return null;
        }

        return scrubbed.Length <= maxChars ? scrubbed : string.Concat(scrubbed.AsSpan(0, maxChars - 1).TrimEnd(), "…");
    }

    /// <summary>Control characters (including tab and line breaks) become spaces; runs of white space become one space; trimmed.</summary>
    public static string SingleLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var isSpace = char.IsControl(c) || char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(c);
            }

            lastWasSpace = isSpace;
        }

        return builder.ToString().Trim();
    }

    /// <summary>An instant as ISO 8601 with its offset, in the clock's local zone (<c>2026-10-24T08:15:00-07:00</c>).</summary>
    public static string Timestamp(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string ReadAppVersion()
    {
        var assembly = typeof(ExportText).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    // scheme://anything-up-to-white-space, plus bare www. hosts.
    [GeneratedRegex(@"(?i)\b(?:[a-z][a-z0-9+.-]{1,15}://|www\.)[^\s""'<>]+")]
    private static partial Regex Url();

    /// <summary>Full-path roots that are removed literally, longest first so a root inside another is not half-replaced.</summary>
    private static List<string> KnownRoots(IAppPaths? paths)
    {
        var candidates = new List<string?>();
        foreach (var variable in RootVariables)
        {
            candidates.Add(Environment.GetEnvironmentVariable(variable));
        }

        if (paths is not null)
        {
            candidates.Add(paths.DataRoot);
            candidates.Add(paths.OneDriveCommercialRoot);
            candidates.Add(paths.OneDriveConsumerRoot);
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c) && !c.Contains('\0', StringComparison.Ordinal) && Path.IsPathFullyQualified(c))
            .Select(c => Path.TrimEndingDirectorySeparator(c!.Trim()))
            .Where(c => c.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(c => c.Length)
            .ToList();
    }

    // Where a path starts: C:\ or C:/, \\server or \\?\, %VAR%\, or a root already replaced by [path] and followed by a separator.
    private const string PathStart = @"(?:(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\|%[A-Za-z_][A-Za-z0-9_]*%[\\/]|\[path\][\\/])";

    // 'C:\a b\c d.xlsx' or "...": everything up to the closing quote, which is one followed by white space, punctuation,
    // or the end (so an apostrophe inside a name, as in O'Brien, does not end the path early).
    [GeneratedRegex(@"(?<open>[""'‘“])" + PathStart + @"[^\r\n]*?(?<close>[""'’”])(?=[\s.,;:!?)\]]|$)")]
    private static partial Regex QuotedPath();

    // An unquoted path runs to a quote, < > |, a line break, or . , ; : ! ? ) ] followed by white space or the end.
    [GeneratedRegex(PathStart + @"(?:(?![.,;:!?)\]](?:\s|$))[^""'‘’“”<>|\r\n])*")]
    private static partial Regex UnquotedPath();

    // Whatever still holds a backslash (a path tail after an apostrophe in a folder name, a relative path).
    [GeneratedRegex(@"[^\s""'‘’“”<>|]*\\[^\s""'‘’“”<>|]*")]
    private static partial Regex WordWithBackslash();
}
