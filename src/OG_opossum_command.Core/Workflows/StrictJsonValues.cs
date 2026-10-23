using System.Globalization;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Workflows;

/// <summary>
/// The value rules shared by the strict readers of files the user's flows write (<c>calendar.json</c>,
/// <c>tomorrow.json</c>), so both agree on what a timestamp and a date are.
/// </summary>
internal static partial class StrictJsonValues
{
    /// <summary>
    /// An ISO 8601 timestamp that carries an offset (<c>2026-10-22T16:00:00Z</c>, <c>2026-10-22T09:00:00.0000000-07:00</c>,
    /// seconds and up to 7 fraction digits optional). Offset-less values are rejected.
    /// </summary>
    public static bool TryParseOffsetTimestamp(string text, out DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = default;
        return TimestampPattern().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>A calendar date written exactly as <c>yyyy-MM-dd</c>.</summary>
    public static bool TryParseDate(string text, out DateOnly value)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = default;
        return DatePattern().IsMatch(text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();
}
