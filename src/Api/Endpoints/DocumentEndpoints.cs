using System.Security.Claims;
using System.Text.Json;
using Api.Auth;
using Api.Web;
using Application.Common;
using Application.Documents;
using Application.Ingestion;
using Format = Application.Ingestion.DocumentFormat;
using Domain;
using Infrastructure.Ingestion;
using Infrastructure.Persistence;
using Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Endpoints;

public static partial class DocumentEndpoints
{
    private const long MultipartOverheadBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var maxUpload = app.ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.MaxUploadBytes;

        var group = app.MapGroup("/api/v1/workspaces/{workspaceId:guid}/documents")
            .WithTags("Documents")
            .RequireAuthorization(Policies.Read);

        group.MapPost("/", Upload)
            .RequireAuthorization(Policies.Write)
            // Bearer tokens (not cookies) authenticate this API, so there is nothing for CSRF to ride on.
            .DisableAntiforgery()
            // Uploads may exceed the API-wide body cap, up to the configured file limit (plus multipart framing).
            .WithMetadata(new RequestSizeLimitAttribute(maxUpload + MultipartOverheadBytes))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxUpload + MultipartOverheadBytes });
        group.MapGet("/", List).AddEndpointFilter<ValidationFilter<ListQuery>>();
        group.MapGet("/{id:guid}", Get);
        group.MapPut("/{id:guid}", Update)
            .RequireAuthorization(Policies.Write)
            .AddEndpointFilter<ValidationFilter<UpdateDocumentRequest>>();
        group.MapPost("/{id:guid}/reprocess", Reprocess).RequireAuthorization(Policies.Write);
        group.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.Write);

        return app;
    }

    private static IResult DocumentNotFound() => Results.Problem(statusCode: 404, title: "Document not found");

    private static IResult Reject(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    private static async Task<IResult> Upload(
        Guid workspaceId,
        IFormFile file,
        [FromForm] string? title,
        AppDbContext db,
        IBlobStorage blobs,
        IOptions<StorageOptions> storage,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var (workspace, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: true, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var validation = await ValidateUploadAsync(file, title, storage.Value.MaxUploadBytes, ct);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        var (fileName, documentTitle, format) = validation.Accepted!;

        // 1. Store the bytes (and hash them) before the database transaction.
        StoredBlob blob;
        await using (var stream = file.OpenReadStream())
        {
            blob = await blobs.SaveAsync(workspace!.TenantId, workspaceId, stream, ct);
        }

        // 2. Record the document AND the job to ingest it in one transaction (transactional outbox):
        //    either both exist or neither does, so an upload can never be saved yet never processed.
        var document = new Document(
            workspace.TenantId,
            workspaceId,
            documentTitle,
            fileName,
            SupportedFormats.ContentTypeOf(format),
            blob.Length,
            blob.Key,
            blob.Sha256Hex);
        db.Documents.Add(document);
        db.OutboxMessages.Add(new OutboxMessage(
            workspace.TenantId,
            OutboxMessage.DocumentUploaded,
            JsonSerializer.Serialize(new DocumentUploadedPayload(document.Id))));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // The row was not saved, so the stored file would be an orphan: remove it.
            await DeleteBlobQuietly(blobs, blob.Key, loggerFactory);
            throw;
        }

        return Results.Created($"/api/v1/workspaces/{workspaceId}/documents/{document.Id}", ToResponse(document));
    }

    private sealed record Accepted(string FileName, string Title, Format Format);

    private sealed record UploadValidation(Accepted? Accepted, IResult? Error);

    /// <summary>Checks size, name, type and content signature. Never trusts the client-declared content type.</summary>
    private static async Task<UploadValidation> ValidateUploadAsync(IFormFile file, string? title, long maxBytes, CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return new(null, Reject(StatusCodes.Status400BadRequest, "Empty file", "The uploaded file is empty."));
        }

        if (file.Length > maxBytes)
        {
            return new(null, Reject(StatusCodes.Status413PayloadTooLarge, "File too large",
                $"The maximum upload size is {maxBytes / (1024 * 1024)} MB."));
        }

        // Keep only the final path segment, and refuse control characters: names are shown in the UI and logs.
        var fileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 260 || fileName.Any(char.IsControl))
        {
            return new(null, Reject(StatusCodes.Status400BadRequest, "Invalid file name", "Provide a file name of up to 260 characters."));
        }

        if (SupportedFormats.FromFileName(fileName) is not { } format)
        {
            return new(null, Reject(StatusCodes.Status415UnsupportedMediaType, "Unsupported file type",
                "Supported types: .pdf, .docx, .md, .txt."));
        }

        // The file's own bytes must match its claimed type (an executable renamed to .pdf is rejected).
        var header = new byte[512];
        int read;
        await using (var stream = file.OpenReadStream())
        {
            read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        }

        if (!SupportedFormats.HeaderMatches(format, header.AsSpan(0, read)))
        {
            return new(null, Reject(StatusCodes.Status400BadRequest, "Content does not match file type",
                $"The file does not look like a valid {format} document."));
        }

        var documentTitle = (string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title).Trim();
        if (documentTitle.Length is 0 or > 300)
        {
            return new(null, Reject(StatusCodes.Status400BadRequest, "Invalid title", "The title must be 1-300 characters."));
        }

        return new(new Accepted(fileName, documentTitle, format), null);
    }

    private static async Task<IResult> List(
        Guid workspaceId,
        [AsParameters] ListQuery list,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: false, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        IQueryable<Document> query = db.Documents.AsNoTracking().Where(d => d.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(list.Q))
        {
            var pattern = $"%{list.Q.Trim()}%";
            query = query.Where(d => EF.Functions.ILike(d.Title, pattern));
        }

        var (field, desc) = Paging.ParseSort(list.Sort);
        query = (field.ToUpperInvariant(), desc) switch
        {
            ("TITLE", false) => query.OrderBy(d => d.Title).ThenBy(d => d.Id),
            ("TITLE", true) => query.OrderByDescending(d => d.Title).ThenBy(d => d.Id),
            ("CREATEDAT", false) => query.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id),
            ("CREATEDAT", true) => query.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id),
            _ => null!,
        };

        if (query is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sort"] = ["Supported sort fields: title, createdAt (prefix with '-' for descending)."],
            });
        }

        return Results.Ok(await query.ToPagedAsync(list, ToResponse, ct));
    }

    private static async Task<IResult> Get(
        Guid workspaceId,
        Guid id,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: false, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var document = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, ct);
        return document is null ? DocumentNotFound() : Results.Ok(ToResponse(document));
    }

    private static async Task<IResult> Update(
        Guid workspaceId,
        Guid id,
        UpdateDocumentRequest request,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: true, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, ct);
        if (document is null)
        {
            return DocumentNotFound();
        }

        db.Entry(document).Property(d => d.Version).OriginalValue = request.Version;
        document.Update(request.Title.Trim(), request.Metadata);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(document));
    }

    /// <summary>Queue a document for ingestion again: after a failure, or to rebuild its chunks.</summary>
    private static async Task<IResult> Reprocess(
        Guid workspaceId,
        Guid id,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var (workspace, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: true, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, ct);
        if (document is null)
        {
            return DocumentNotFound();
        }

        if (document.Status is DocumentStatus.Pending or DocumentStatus.Processing)
        {
            return Reject(StatusCodes.Status409Conflict, "Already queued", "This document is already waiting for or being processed.");
        }

        document.Requeue();
        db.OutboxMessages.Add(new OutboxMessage(
            workspace!.TenantId,
            OutboxMessage.DocumentUploaded,
            JsonSerializer.Serialize(new DocumentUploadedPayload(document.Id))));
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/v1/workspaces/{workspaceId}/documents/{document.Id}", ToResponse(document));
    }

    private static async Task<IResult> Delete(
        Guid workspaceId,
        Guid id,
        AppDbContext db,
        IBlobStorage blobs,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: true, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, ct);
        if (document is null)
        {
            return DocumentNotFound();
        }

        var storageKey = document.StorageKey;
        document.SoftDelete();
        await db.SaveChangesAsync(ct);

        // Deleted content must stop being searchable and stored: drop its chunks and the original file.
        await db.Chunks.Where(c => c.DocumentId == id).ExecuteDeleteAsync(ct);
        await DeleteBlobQuietly(blobs, storageKey, loggerFactory);

        return Results.NoContent();
    }

    private static async Task DeleteBlobQuietly(IBlobStorage blobs, string key, ILoggerFactory loggerFactory)
    {
        try
        {
            await blobs.DeleteAsync(key, CancellationToken.None);
        }
#pragma warning disable CA1031 // Cleanup is best-effort; the database state is already correct.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBlobDeleteFailed(loggerFactory.CreateLogger(typeof(DocumentEndpoints)), ex, key);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete stored file {Key}")]
    private static partial void LogBlobDeleteFailed(ILogger logger, Exception exception, string key);

    private static DocumentResponse ToResponse(Document d) => new(
        d.Id,
        d.WorkspaceId,
        d.Title,
        d.FileName,
        d.ContentType,
        d.SizeBytes,
        d.Status.ToString(),
        d.ChunkCount,
        d.FailureReason,
        d.Metadata,
        d.CreatedAt,
        d.Version);
}
