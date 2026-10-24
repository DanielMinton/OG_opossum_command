using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Maid;

/// <summary>What a file is ABOUT: keywords and named things, from its text, title and name. Plain counting, no model.</summary>
public static partial class TextAnalysis
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "all", "any", "can", "had", "her", "was", "one", "our", "out", "has", "him", "his", "how",
        "its", "may", "new", "now", "old", "see", "two", "way", "who", "did", "get", "got", "let", "say", "she", "too", "use", "per", "via", "yes",
        "this", "that", "with", "from", "have", "will", "they", "them", "then", "than", "been", "were", "what", "when", "where", "which", "while", "would",
        "there", "their", "these", "those", "into", "onto", "upon", "about", "after", "before", "other", "some", "such", "only", "also", "more", "most",
        "very", "each", "both", "same", "just", "over", "under", "again", "here", "page", "pages", "please", "thank", "thanks", "dear", "regards",
        "date", "time", "name", "number", "total", "file", "files", "document", "copy", "final", "draft", "version", "untitled", "download", "downloads",
        "should", "could", "shall", "must", "does", "done", "make", "made", "like", "well", "being", "because", "between", "through", "during", "without",
        "within", "against", "including", "include", "includes", "following", "below", "above", "http", "https", "www", "com", "org", "net", "html",
        "january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "img", "dsc", "pxl", "scan", "image", "photo", "screenshot", "pdf", "docx", "xlsx", "pptx", "txt",
    };

    private static readonly string[] OrgSuffixes =
    [
        "Inc", "LLC", "Ltd", "Corp", "Corporation", "Company", "Co", "Bank", "University", "College", "School", "Hospital", "Clinic", "Church",
        "Association", "Foundation", "Department", "Council", "Agency", "Group", "Services", "Insurance", "Credit Union",
    ];

    /// <summary>Lower-case word tokens of three or more letters (digits allowed after a letter).</summary>
    public static IEnumerable<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in WordToken().Matches(text))
        {
            yield return match.Value.ToLowerInvariant();
        }
    }

    /// <summary>
    /// The most telling words: frequency in the body, with words in the title and file name counted three times;
    /// stop words and numbers are dropped. At most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<string> Keywords(string body, string title, string name, int max = 8)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string text, int weight)
        {
            foreach (var word in Words(text))
            {
                if (word.Length >= 3 && !StopWords.Contains(word) && !word.All(char.IsAsciiDigit))
                {
                    counts[word] = counts.GetValueOrDefault(word) + weight;
                }
            }
        }

        Count(body, 1);
        Count(title, 3);
        Count(name, 3);
        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(max).Select(p => p.Key).ToList();
    }

    /// <summary>
    /// Named things: capitalised phrases that end in an organisation word (<c>Contoso Bank</c>), capitalised phrases
    /// that recur, and e-mail domains. At most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<string> Entities(string text, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CapitalisedPhrase().Matches(text))
        {
            var phrase = match.Value.Trim();
            var words = phrase.Split(' ');
            if (words.Length % 2 == 0 && words.Length >= 2 && words.Take(words.Length / 2).SequenceEqual(words.Skip(words.Length / 2)))
            {
                // "Contoso Bank Contoso Bank": a letterhead repeated on one line.
                words = words.Take(words.Length / 2).ToArray();
                phrase = string.Join(' ', words);
            }

            if (words.All(w => StopWords.Contains(w)))
            {
                continue;
            }

            var isOrg = OrgSuffixes.Any(s => phrase.EndsWith(" " + s, StringComparison.Ordinal));
            if (words.Length >= 2 || isOrg)
            {
                scores[phrase] = scores.GetValueOrDefault(phrase) + (isOrg ? 3 : 1);
            }
        }

        foreach (Match match in EmailDomain().Matches(text))
        {
            var domain = match.Groups[1].Value.ToLowerInvariant();
            if (domain is not ("gmail.com" or "outlook.com" or "hotmail.com" or "yahoo.com" or "icloud.com" or "live.com"))
            {
                scores[domain] = scores.GetValueOrDefault(domain) + 2;
            }
        }

        return scores
            .Where(p => p.Value >= 2)
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(max)
            .Select(p => p.Key)
            .ToList();
    }

    /// <summary>Whether <paramref name="haystack"/> (already lower case) contains <paramref name="phrase"/> as whole words.</summary>
    public static bool Mentions(string haystack, string phrase)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        var needle = phrase.Trim().ToLowerInvariant();
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
            var end = at + needle.Length;
            var after = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (before && after)
            {
                return true;
            }

            at = end;
        }

        return false;
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9']{2,}")]
    private static partial Regex WordToken();

    [GeneratedRegex(@"\b(?:[A-Z][a-zA-Z&]+)(?: (?:of |and |& )?[A-Z][a-zA-Z&]+){0,3}\b")]
    private static partial Regex CapitalisedPhrase();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})")]
    private static partial Regex EmailDomain();
}
