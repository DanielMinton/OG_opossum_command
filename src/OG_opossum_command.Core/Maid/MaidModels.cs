namespace OG_opossum_command.Core.Maid;

/// <summary>
/// Which side of the work/personal wall a place or a file belongs to (ADR-012). <see cref="Unassigned"/> is a place
/// that belongs to neither (the Downloads folder): a file there gets a side only from evidence, never by default.
/// </summary>
public enum MaidSide
{
    /// <summary>Neither side yet (Downloads, or a file the evidence can't place).</summary>
    Unassigned,

    /// <summary>The work OneDrive (<c>%OneDriveCommercial%</c>).</summary>
    Work,

    /// <summary>The personal root (<c>%USERPROFILE%\Personal</c> unless configured).</summary>
    Personal,
}

/// <summary>What a plan item proposes to do with its file.</summary>
public enum MaidAction
{
    /// <summary>Move (and maybe rename) into a category folder on the file's side.</summary>
    Move,

    /// <summary>Move into the source's <c>_Trash Review</c> folder for 30 days. Never a delete.</summary>
    Trash,

    /// <summary>Do nothing (already filed, online-only, a filed work folder, or too unsure).</summary>
    Leave,

    /// <summary>Needs your decision: which side, or a cross-side candidate. Never applied until you choose.</summary>
    Decide,

    /// <summary>Looks like a credential. Never opened, hashed, moved, or bundled.</summary>
    Security,
}

/// <summary>A folder the maid reads from, with the side it belongs to.</summary>
/// <param name="Path">Full path of the folder.</param>
/// <param name="Side">The folder's side; <see cref="MaidSide.Unassigned"/> for Downloads.</param>
/// <param name="IsWorkInbox">Whether this is the work download inbox (the only work folder sorted by default, rule 4).</param>
public sealed record MaidSource(string Path, MaidSide Side, bool IsWorkInbox);

/// <summary>Image facts read from the file header (no decoding, no WIC).</summary>
public sealed record ImageInfo(
    string Format,
    int Width,
    int Height,
    DateTimeOffset? Taken,
    string? CameraMake,
    string? CameraModel,
    string? Software,
    bool LooksLikeScreenshot,
    bool LooksLikeCameraPhoto);

/// <summary>Text and metadata pulled out of a file, locally (ADR-059). Text is capped.</summary>
public sealed class Extraction
{
    /// <summary>The longest text the classifier sees.</summary>
    public const int MaxTextChars = 8000;

    /// <summary>Extracted body text, at most <see cref="MaxTextChars"/>.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Title from document metadata or an HTML title, when there is one.</summary>
    public string? Title { get; init; }

    /// <summary>Author or creator from document metadata.</summary>
    public string? Author { get; init; }

    /// <summary>Company from Office metadata (strong work/personal evidence).</summary>
    public string? Company { get; init; }

    /// <summary>Keywords from document metadata.</summary>
    public string? MetaKeywords { get; init; }

    /// <summary>Image header facts, for pictures.</summary>
    public ImageInfo? Image { get; init; }

    /// <summary>The document carries a digital signature (PDF <c>/ByteRange</c>, Office <c>_xmlsignatures</c>).</summary>
    public bool IsSigned { get; init; }

    /// <summary>An HTML page saved by a browser (<c>saved from url</c> marker).</summary>
    public bool IsSavedWebPage { get; init; }

    /// <summary>Why there is little or no text (encrypted, scanned, too big, timed out), for the report.</summary>
    public string? Note { get; init; }

    /// <summary>An empty extraction with a note.</summary>
    public static Extraction Empty(string? note = null) => new() { Note = note };
}

/// <summary>One file (or one saved-page <c>_files</c> folder) found in a source.</summary>
public sealed class ScannedFile
{
    /// <summary>Full path.</summary>
    public required string FullPath { get; init; }

    /// <summary>Path relative to the source folder.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The source it was found in.</summary>
    public required MaidSource Source { get; init; }

    /// <summary>File name with extension.</summary>
    public string Name => Path.GetFileName(FullPath);

    /// <summary>Extension in lower case with the dot (<c>.pdf</c>), or empty.</summary>
    public string Extension => IsFolder ? string.Empty : Path.GetExtension(FullPath).ToLowerInvariant();

    /// <summary>Whether this item is a folder (a saved web page's <c>_files</c> folder).</summary>
    public bool IsFolder { get; init; }

    /// <summary>Size in bytes (a folder: the sum of its files).</summary>
    public long Size { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Last write time.</summary>
    public DateTimeOffset Modified { get; init; }

    /// <summary>SHA-256 of the content (hex, lower case), or <c>null</c> when the content was not read.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Hash of the normalised extracted text (catches the same document saved twice), or <c>null</c>.</summary>
    public string? ContentFingerprint { get; set; }

    /// <summary>The name looks like a credential file: never opened (rule 5).</summary>
    public bool IsCredentialName { get; init; }

    /// <summary>A OneDrive online-only placeholder: opening it would download it, so it is not opened.</summary>
    public bool IsOnlineOnly { get; init; }

    /// <summary>What was extracted, or <c>null</c> when the content was not read.</summary>
    public Extraction? Extraction { get; set; }

    /// <summary>The folder the file sits in, relative to the source ("" at the top).</summary>
    public string OriginFolder => Path.GetDirectoryName(RelativePath) ?? string.Empty;
}
