using System.Text;
using Application.Ingestion;
using AppFormat = Application.Ingestion.DocumentFormat;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Infrastructure.Ingestion;

/// <summary>Extracts plain text from PDF, DOCX, Markdown and text files.</summary>
public sealed class DocumentTextExtractor(IOptions<IngestionOptions> options) : IDocumentTextExtractor
{
    public async Task<string> ExtractAsync(AppFormat format, Stream content, CancellationToken cancellationToken)
    {
        var text = format switch
        {
            AppFormat.Pdf => FromPdf(content, cancellationToken),
            AppFormat.Docx => FromDocx(content, cancellationToken),
            _ => await FromText(content, cancellationToken),
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            throw IngestionException.Permanent("The document contains no extractable text.");
        }

        return text;
    }

    private string FromPdf(Stream content, CancellationToken ct)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);
            var builder = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                builder.AppendLine(page.Text);
                builder.AppendLine();
                EnsureWithinLimit(builder.Length);
            }

            return builder.ToString();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IngestionException))
        {
            // Corrupt, encrypted or malformed PDFs will not improve on retry.
            throw IngestionException.Permanent("The PDF could not be read (it may be corrupt or password-protected).", ex);
        }
    }

    private string FromDocx(Stream content, CancellationToken ct)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(content, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw IngestionException.Permanent("The DOCX file has no document body.");

            var builder = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                ct.ThrowIfCancellationRequested();
                builder.AppendLine(paragraph.InnerText);
                builder.AppendLine();
                EnsureWithinLimit(builder.Length);
            }

            return builder.ToString();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IngestionException))
        {
            throw IngestionException.Permanent("The DOCX file could not be read (it may be corrupt).", ex);
        }
    }

    private async Task<string> FromText(Stream content, CancellationToken ct)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(false, throwOnInvalidBytes: true));
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                builder.Append(buffer, 0, read);
                EnsureWithinLimit(builder.Length);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw IngestionException.Permanent("The file is not valid UTF-8 text.", ex);
        }

        return builder.ToString();
    }

    private void EnsureWithinLimit(int length)
    {
        if (length > options.Value.MaxExtractedChars)
        {
            throw IngestionException.Permanent(
                $"The document's text exceeds the limit of {options.Value.MaxExtractedChars:N0} characters.");
        }
    }
}
