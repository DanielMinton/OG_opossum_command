using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Maid.Extractors;
using OG_opossum_command.Core.Paths;

namespace OG_opossum_command.Core.Maid;

/// <summary>
/// Rule 5: files whose names look like credentials are never opened, not even to hash them. Matching is on the file
/// name only, so the check itself reads nothing.
/// </summary>
public static partial class CredentialGuard
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".pfx", ".p12", ".key", ".kdbx", ".kdb", ".env", ".ppk", ".jks", ".keystore", ".gpg", ".asc", ".ovpn", ".rdp", ".cer", ".crt", ".der", ".p7b", ".1pux", ".agilekeychain", ".opvault",
    };

    /// <summary>Whether <paramref name="fileName"/> looks like a credential (built-in list plus <paramref name="extraFragments"/>).</summary>
    public static bool LooksLikeCredential(string fileName, IReadOnlyList<string>? extraFragments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var name = Path.GetFileName(fileName);
        if (Extensions.Contains(Path.GetExtension(name)) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (CredentialName().IsMatch(name))
        {
            return true;
        }

        return extraFragments is not null && extraFragments.Any(f => !string.IsNullOrWhiteSpace(f) && name.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether extracted text holds something secret-shaped (then the file is flagged and never bundled).</summary>
    public static bool TextLooksSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SecretText().IsMatch(text);
    }

    [GeneratedRegex(@"(?ix)(recovery[\s_\-]*(codes?|key)|backup[\s_\-]*codes?|passwords?|passwd|pass[\s_\-]*phrase|credentials?|secrets?|private[\s_\-]*key|id_(rsa|dsa|ecdsa|ed25519)|api[\s_\-]*keys?|access[\s_\-]*keys?|\btokens?\b|2fa|\bmfa\b|seed[\s_\-]*phrase|mnemonic|wallet|logins?\b|\bpins?\b)")]
    private static partial Regex CredentialName();

    [GeneratedRegex(@"(?im)(-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(password|passwd|pwd|secret|api[_\- ]?key|client[_\- ]?secret)\s*[:=]\s*\S{4,}|\b(AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9]{20,})\b|recovery codes?)")]
    private static partial Regex SecretText();
}

/// <summary>
/// Walks the sources and collects, per file: size, timestamps, extension, SHA-256 (streamed) and a content
/// fingerprint, plus extracted text. Read-only. Never follows links, never opens a credential-looking or online-only
/// file, never descends into <c>_Trash Review</c>.
/// </summary>
public sealed class Scanner
{
    private readonly MaidSettings _settings;

    /// <summary>Creates a scanner.</summary>
    public Scanner(MaidSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>What a scan found, and what it skipped.</summary>
    public sealed record ScanResult(IReadOnlyList<ScannedFile> Files, IReadOnlyList<string> Notes);

    /// <summary>Scans every existing source.</summary>
    public ScanResult Scan(IEnumerable<MaidSource> sources, bool extract = true)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var files = new List<ScannedFile>();
        var notes = new List<string>();
        foreach (var source in sources)
        {
            if (!Directory.Exists(source.Path))
            {
                notes.Add($"Source not found, skipped: {source.Path}");
                continue;
            }

            Walk(source, new DirectoryInfo(source.Path), 0, files, notes);
        }

        foreach (var file in files)
        {
            Read(file, extract);
        }

        return new ScanResult(files, notes);
    }

    private void Walk(MaidSource source, DirectoryInfo folder, int depth, List<ScannedFile> files, List<string> notes)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = folder.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not read {folder.FullName}: {ex.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            if (files.Count >= _settings.MaxFiles)
            {
                notes.Add($"Stopped at {_settings.MaxFiles} files (maxFiles in rules.json).");
                return;
            }

            if ((entry.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0 || OneDriveBoundary.IsLink(entry))
            {
                continue;
            }

            if (entry is DirectoryInfo dir)
            {
                if (string.Equals(dir.Name, MaidLocations.TrashFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (dir.Name.EndsWith("_files", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(FolderItem(source, dir));
                    continue;
                }

                if (depth + 1 >= _settings.MaxDepth)
                {
                    notes.Add($"Too deep, not read: {dir.FullName}");
                    continue;
                }

                Walk(source, dir, depth + 1, files, notes);
                continue;
            }

            var file = (FileInfo)entry;
            files.Add(new ScannedFile
            {
                FullPath = file.FullName,
                RelativePath = Path.GetRelativePath(source.Path, file.FullName),
                Source = source,
                Size = file.Length,
                Created = file.CreationTimeUtc,
                Modified = file.LastWriteTimeUtc,
                IsCredentialName = CredentialGuard.LooksLikeCredential(file.Name, _settings.ExtraCredentialNames),
                IsOnlineOnly = OneDriveBoundary.IsOnlineOnly(file.Attributes),
            });
        }
    }

    private static ScannedFile FolderItem(MaidSource source, DirectoryInfo dir)
    {
        long size = 0;
        try
        {
            size = dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Size is informational only.
        }

        return new ScannedFile
        {
            FullPath = dir.FullName,
            RelativePath = Path.GetRelativePath(source.Path, dir.FullName),
            Source = source,
            IsFolder = true,
            Size = size,
            Created = dir.CreationTimeUtc,
            Modified = dir.LastWriteTimeUtc,
        };
    }

    /// <summary>Hashes and extracts one file, unless rule 5 or an online-only placeholder says not to open it.</summary>
    private static void Read(ScannedFile file, bool extract)
    {
        if (file.IsFolder)
        {
            file.Extraction = Extraction.Empty("saved web page assets folder");
            return;
        }

        if (file.IsCredentialName)
        {
            file.Extraction = Extraction.Empty("not opened: the name looks like a credential");
            return;
        }

        if (file.IsOnlineOnly)
        {
            file.Extraction = Extraction.Empty("not opened: online-only OneDrive file (opening would download it)");
            return;
        }

        file.Sha256 = HashFile(file.FullPath);
        if (!extract)
        {
            return;
        }

        file.Extraction = ContentExtractor.Extract(file.FullPath, file.Size);
        file.ContentFingerprint = Fingerprint(file.Extraction);
    }

    /// <summary>Streamed SHA-256 of a file in lower-case hex, or <c>null</c> when it can't be read.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Hash of the normalised text (lower case, whitespace collapsed), or of the image facts; <c>null</c> when there is too little to go on.</summary>
    internal static string? Fingerprint(Extraction extraction)
    {
        string basis;
        if (extraction.Text.Length >= 200)
        {
            basis = string.Join(' ', extraction.Text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        else if (extraction.Image is { Taken: not null } image)
        {
            basis = $"img|{image.Width}x{image.Height}|{image.Taken:O}|{image.CameraModel}";
        }
        else
        {
            return null;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..16];
    }
}
