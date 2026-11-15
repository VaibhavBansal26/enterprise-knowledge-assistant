namespace Application.Ingestion;

public enum DocumentFormat
{
    Pdf,
    Docx,
    Markdown,
    PlainText,
}

/// <summary>The file types ingestion accepts, and how to recognise them without trusting the client.</summary>
public static class SupportedFormats
{
    public const string PdfContentType = "application/pdf";
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string MarkdownContentType = "text/markdown";
    public const string PlainTextContentType = "text/plain";

    /// <summary>Format implied by the file extension, or null when unsupported.</summary>
    public static DocumentFormat? FromFileName(string fileName) =>
        Path.GetExtension(fileName).ToUpperInvariant() switch
        {
            ".PDF" => DocumentFormat.Pdf,
            ".DOCX" => DocumentFormat.Docx,
            ".MD" or ".MARKDOWN" => DocumentFormat.Markdown,
            ".TXT" => DocumentFormat.PlainText,
            _ => null,
        };

    public static string ContentTypeOf(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => PdfContentType,
        DocumentFormat.Docx => DocxContentType,
        DocumentFormat.Markdown => MarkdownContentType,
        _ => PlainTextContentType,
    };

    public static DocumentFormat? FromContentType(string contentType) => contentType switch
    {
        PdfContentType => DocumentFormat.Pdf,
        DocxContentType => DocumentFormat.Docx,
        MarkdownContentType => DocumentFormat.Markdown,
        PlainTextContentType => DocumentFormat.PlainText,
        _ => null,
    };

    /// <summary>
    /// Whether the leading bytes match the claimed format. A PDF starts with "%PDF-", a DOCX is a ZIP
    /// ("PK\x03\x04"); text formats must be NUL-free valid UTF-8. This stops e.g. an executable renamed to .pdf.
    /// </summary>
    public static bool HeaderMatches(DocumentFormat format, ReadOnlySpan<byte> header) => format switch
    {
        DocumentFormat.Pdf => header.StartsWith("%PDF-"u8),
        DocumentFormat.Docx => header.StartsWith("PK\x03\x04"u8),
        _ => !header.Contains((byte)0) && System.Text.Unicode.Utf8.IsValid(TrimIncompleteTail(header)),
    };

    /// <summary>A header sample may end mid-character; drop a trailing partial UTF-8 sequence before validating.</summary>
    private static ReadOnlySpan<byte> TrimIncompleteTail(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var b = bytes[^back];
            if ((b & 0b1100_0000) == 0b1000_0000)
            {
                continue; // continuation byte, keep looking for the lead byte
            }

            var needed = b >= 0b1111_0000 ? 4 : b >= 0b1110_0000 ? 3 : b >= 0b1100_0000 ? 2 : 1;
            return needed > back ? bytes[..^back] : bytes;
        }

        return bytes;
    }
}
