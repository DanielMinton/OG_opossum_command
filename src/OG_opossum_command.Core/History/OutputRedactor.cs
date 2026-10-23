using System.Globalization;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.History;

/// <summary>
/// Scrubs secrets from step output and messages before they are stored, and bounds their size (spec §13).
/// Patterns are deliberately broad: a false positive hides a harmless value, a false negative leaks a token.
/// </summary>
public static partial class OutputRedactor
{
    /// <summary>Hard cap on stored output per step.</summary>
    public const int MaxStoredChars = 4000;

    private const string Placeholder = "[redacted]";

    /// <summary>Returns a redacted, bounded copy; <c>null</c> stays <c>null</c>.</summary>
    public static string? Redact(string? text) => Redact(text, MaxStoredChars);

    /// <summary>Returns a redacted copy bounded to <paramref name="maxChars"/>; <c>null</c> stays <c>null</c>.</summary>
    public static string? Redact(string? text, int maxChars)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Length == 0)
        {
            return text;
        }

        var scrubbed = BearerToken().Replace(text, "Bearer " + Placeholder);
        scrubbed = KeyValueSecret().Replace(scrubbed, "$1=" + Placeholder);
        scrubbed = QuerySecret().Replace(scrubbed, "$1" + Placeholder);
        scrubbed = JsonWebToken().Replace(scrubbed, Placeholder);
        scrubbed = AzureStyleKey().Replace(scrubbed, Placeholder);

        if (scrubbed.Length > maxChars)
        {
            var dropped = (scrubbed.Length - maxChars).ToString(CultureInfo.InvariantCulture);
            scrubbed = string.Concat(scrubbed.AsSpan(0, maxChars), $"{Environment.NewLine}[truncated {dropped} characters]");
        }

        return scrubbed;
    }

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    // password=..., "client_secret": "...", api-key: ..., Authorization: ...
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|client[_-]?secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|authorization|sas|sig|signature)\b[""']?\s*[:=]\s*[""']?[^\s""'&;,]+", RegexOptions.None)]
    private static partial Regex KeyValueSecret();

    // ?token=..., &sig=..., &code=... in URLs
    [GeneratedRegex(@"(?i)([?&](?:token|access_token|id_token|code|sig|signature|key|api[_-]?key|sas)=)[^&\s]+")]
    private static partial Regex QuerySecret();

    // header.payload.signature
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex JsonWebToken();

    // Long base64 blobs ending in == that look like storage account keys or similar.
    [GeneratedRegex(@"\b[A-Za-z0-9+/]{64,}={1,2}")]
    private static partial Regex AzureStyleKey();
}
