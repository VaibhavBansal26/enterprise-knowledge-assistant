using Api.Web;
using Application.Common;
using Application.Workspaces;
using Domain;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspaces").WithTags("Workspaces");

        group.MapPost("/", Create).AddEndpointFilter<ValidationFilter<CreateWorkspaceRequest>>();
        group.MapGet("/", List).AddEndpointFilter<ValidationFilter<ListQuery>>();
        group.MapGet("/{id:guid}", Get);
        group.MapPut("/{id:guid}", Update).AddEndpointFilter<ValidationFilter<UpdateWorkspaceRequest>>();
        group.MapDelete("/{id:guid}", Delete);

        return app;
    }

    private static async Task<IResult> Create(CreateWorkspaceRequest request, AppDbContext db, CancellationToken ct)
    {
        var workspace = new Workspace(request.Name.Trim(), request.Description?.Trim());
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/workspaces/{workspace.Id}", ToResponse(workspace));
    }

    private static async Task<IResult> List(
        [AsParameters] ListQuery list, AppDbContext db, CancellationToken ct)
    {
        IQueryable<Workspace> query = db.Workspaces.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(list.Q))
        {
            var pattern = $"%{list.Q.Trim()}%";
            query = query.Where(w => EF.Functions.ILike(w.Name, pattern));
        }

        var (field, desc) = Paging.ParseSort(list.Sort);
        query = (field.ToUpperInvariant(), desc) switch
        {
            ("NAME", false) => query.OrderBy(w => w.Name).ThenBy(w => w.Id),
            ("NAME", true) => query.OrderByDescending(w => w.Name).ThenBy(w => w.Id),
            ("CREATEDAT", false) => query.OrderBy(w => w.CreatedAt).ThenBy(w => w.Id),
            ("CREATEDAT", true) => query.OrderByDescending(w => w.CreatedAt).ThenBy(w => w.Id),
            _ => null!,
        };

        if (query is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sort"] = ["Supported sort fields: name, createdAt (prefix with '-' for descending)."],
            });
        }

        return Results.Ok(await query.ToPagedAsync(list, ToResponse, ct));
    }

    private static async Task<IResult> Get(Guid id, AppDbContext db, CancellationToken ct)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return workspace is null ? Results.Problem(statusCode: 404, title: "Workspace not found") : Results.Ok(ToResponse(workspace));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateWorkspaceRequest request, AppDbContext db, CancellationToken ct)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null)
        {
            return Results.Problem(statusCode: 404, title: "Workspace not found");
        }

        // Optimistic concurrency: the update only succeeds if the row is still at the client's version.
        db.Entry(workspace).Property(w => w.Version).OriginalValue = request.Version;
        workspace.Update(request.Name.Trim(), request.Description?.Trim());
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(workspace));
    }

    private static async Task<IResult> Delete(Guid id, AppDbContext db, CancellationToken ct)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null)
        {
            return Results.Problem(statusCode: 404, title: "Workspace not found");
        }

        workspace.SoftDelete();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static WorkspaceResponse ToResponse(Workspace w) =>
        new(w.Id, w.Name, w.Description, w.CreatedAt, w.Version);
}
