using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace OG_opossum_command.Core.Maid.Extractors;

/// <summary>
/// PDF text with PdfPig (ADR-059): document info (title, author, keywords) and the words of the first few pages.
/// A scanned PDF with no text layer yields metadata only (no OCR). Encrypted files yield a note.
/// </summary>
internal static class PdfText
{
    /// <summary>Pages read; the first pages say what a document is.</summary>
    internal const int MaxPages = 5;

    public static Extraction Extract(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var signed = LooksSigned(bytes);
        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var text = new StringBuilder();
            var pages = Math.Min(document.NumberOfPages, MaxPages);
            for (var number = 1; number <= pages && text.Length < Extraction.MaxTextChars; number++)
            {
                double? lastBottom = null;
                foreach (var word in document.GetPage(number).GetWords())
                {
                    // A new baseline is a new line, so phrases don't run across lines.
                    var bottom = word.BoundingBox.Bottom;
                    if (lastBottom is { } previous && Math.Abs(previous - bottom) > Math.Max(2, word.BoundingBox.Height * 0.5))
                    {
                        text.Append('\n');
                    }

                    text.Append(word.Text).Append(' ');
                    lastBottom = bottom;
                }

                text.Append('\n');
            }

            var info = document.Information;
            var body = ContentExtractor.Cap(text.ToString());
            return new Extraction
            {
                Text = body,
                Title = Blank(info.Title),
                Author = Blank(info.Author),
                MetaKeywords = Blank(info.Keywords) ?? Blank(info.Subject),
                IsSigned = signed,
                Note = body.Length == 0 ? "no text layer (a scan or an image-only PDF; there is no OCR)" : null,
            };
        }
        catch (PdfDocumentEncryptedException)
        {
            return new Extraction { IsSigned = signed, Note = "encrypted PDF: content not read" };
        }
    }

    /// <summary>A PDF signature dictionary leaves <c>/ByteRange</c> and <c>/Sig</c> in the file.</summary>
    private static bool LooksSigned(byte[] bytes)
    {
        var span = bytes.AsSpan();
        return span.IndexOf("/ByteRange"u8) >= 0 && span.IndexOf("/Sig"u8) >= 0;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
