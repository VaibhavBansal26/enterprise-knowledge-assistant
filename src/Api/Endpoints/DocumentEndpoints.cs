using Api.Web;
using Application.Common;
using Application.Documents;
using Domain;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspaces/{workspaceId:guid}/documents").WithTags("Documents");

        group.MapPost("/", Create).AddEndpointFilter<ValidationFilter<CreateDocumentRequest>>();
        group.MapGet("/", List).AddEndpointFilter<ValidationFilter<ListQuery>>();
        group.MapGet("/{id:guid}", Get);
        group.MapPut("/{id:guid}", Update).AddEndpointFilter<ValidationFilter<UpdateDocumentRequest>>();
        group.MapDelete("/{id:guid}", Delete);

        return app;
    }

    private static IResult WorkspaceNotFound() => Results.Problem(statusCode: 404, title: "Workspace not found");

    private static IResult DocumentNotFound() => Results.Problem(statusCode: 404, title: "Document not found");

    private static async Task<IResult> Create(
        Guid workspaceId, CreateDocumentRequest request, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct))
        {
            return WorkspaceNotFound();
        }

        var document = new Document(
            workspaceId,
            request.Title.Trim(),
            request.FileName.Trim(),
            request.ContentType.Trim(),
            request.SizeBytes,
            request.Metadata);
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/workspaces/{workspaceId}/documents/{document.Id}", ToResponse(document));
    }

    private static async Task<IResult> List(
        Guid workspaceId, [AsParameters] ListQuery list, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct))
        {
            return WorkspaceNotFound();
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

    private static async Task<IResult> Get(Guid workspaceId, Guid id, AppDbContext db, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, ct);
        return document is null ? DocumentNotFound() : Results.Ok(ToResponse(document));
    }

    private static async Task<IResult> Update(
        Guid workspaceId, Guid id, UpdateDocumentRequest request, AppDbContext db, CancellationToken ct)
    {
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

    private static async Task<IResult> Delete(Guid workspaceId, Guid id, AppDbContext db, CancellationToken ct)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, ct);
        if (document is null)
        {
            return DocumentNotFound();
        }

        document.SoftDelete();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static DocumentResponse ToResponse(Document d) => new(
        d.Id,
        d.WorkspaceId,
        d.Title,
        d.FileName,
        d.ContentType,
        d.SizeBytes,
        d.Status.ToString(),
        d.Metadata,
        d.CreatedAt,
        d.Version);
}
