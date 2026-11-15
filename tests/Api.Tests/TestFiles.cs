using System.Net.Http.Json;
using System.Text;
using Application.Documents;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Api.Tests;

/// <summary>Builds real files of each supported type, and helpers for uploading them.</summary>
public static class TestFiles
{
    public static byte[] Markdown(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A genuine single-page PDF containing <paramref name="lines"/>, made with PdfPig's writer.</summary>
    public static byte[] Pdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 780.0;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(40, y), font);
            y -= 18;
        }

        return builder.Build();
    }

    /// <summary>A genuine .docx with one paragraph per entry.</summary>
    public static byte[] Docx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
            main.Document.Save();
        }

        return stream.ToArray();
    }

    public static Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid workspaceId,
        string fileName = "note.md",
        byte[]? content = null,
        string? title = null,
        string? idempotencyKey = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(content ?? Markdown("# Note\n\nSome searchable text about onboarding.")), "file", fileName);
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/documents") { Content = form };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    /// <summary>Uploads and returns the created document (asserting 201).</summary>
    public static async Task<DocumentResponse> UploadOkAsync(
        HttpClient client, Guid workspaceId, string fileName = "note.md", byte[]? content = null, string? title = null)
    {
        var response = await UploadAsync(client, workspaceId, fileName, content, title);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }

    /// <summary>Polls until the document reaches <paramref name="status"/>; fails the test on timeout.</summary>
    public static async Task<DocumentResponse> WaitForStatusAsync(
        HttpClient client, Guid workspaceId, Guid documentId, string status, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        DocumentResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await client.GetFromJsonAsync<DocumentResponse>($"/api/v1/workspaces/{workspaceId}/documents/{documentId}");
            if (last!.Status == status)
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(
            $"Document did not reach '{status}' within {timeoutSeconds}s (last: {last?.Status}, reason: {last?.FailureReason}).");
    }
}
