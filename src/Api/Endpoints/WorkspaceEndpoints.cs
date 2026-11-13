using System.Security.Claims;
using Api.Auth;
using Api.Web;
using Application.Abstractions;
using Application.Common;
using Application.Workspaces;
using Domain;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspaces").WithTags("Workspaces").RequireAuthorization(Policies.Read);

        group.MapPost("/", Create)
            .RequireAuthorization(Policies.Write)
            .AddEndpointFilter<ValidationFilter<CreateWorkspaceRequest>>();
        group.MapGet("/", List).AddEndpointFilter<ValidationFilter<ListQuery>>();
        group.MapGet("/{id:guid}", Get);
        group.MapPut("/{id:guid}", Update)
            .RequireAuthorization(Policies.Write)
            .AddEndpointFilter<ValidationFilter<UpdateWorkspaceRequest>>();
        group.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.Admin);

        return app;
    }

    private static async Task<IResult> Create(
        CreateWorkspaceRequest request, AppDbContext db, ITenantContext tenant, ICurrentUser me, CancellationToken ct)
    {
        var tenantId = tenant.TenantId!.Value; // guaranteed by TenantMiddleware for authenticated requests
        var workspace = new Workspace(tenantId, request.Name.Trim(), request.Description?.Trim());
        db.Workspaces.Add(workspace);

        // The creator can always reach what they created, even if they are not a tenant admin.
        db.WorkspaceMembers.Add(new WorkspaceMember(tenantId, workspace.Id, me.UserId!, WorkspaceRole.Editor));
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/workspaces/{workspace.Id}", ToResponse(workspace));
    }

    private static async Task<IResult> List(
        [AsParameters] ListQuery list, AppDbContext db, ICurrentUser me, CancellationToken ct)
    {
        IQueryable<Workspace> query = db.Workspaces.AsNoTracking();

        // Tenant admins see the whole tenant; everyone else only workspaces they are a member of.
        if (!me.IsAdmin)
        {
            var userId = me.UserId;
            query = query.Where(w => db.WorkspaceMembers.Any(m => m.WorkspaceId == w.Id && m.UserId == userId));
        }

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

    private static async Task<IResult> Get(
        Guid id, AppDbContext db, IAuthorizationService authorization, ClaimsPrincipal user, CancellationToken ct)
    {
        var (workspace, error) = await WorkspaceAccess.LoadAsync(id, write: false, track: false, db, authorization, user, ct);
        return error ?? Results.Ok(ToResponse(workspace!));
    }

    private static async Task<IResult> Update(
        Guid id,
        UpdateWorkspaceRequest request,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var (workspace, error) = await WorkspaceAccess.LoadAsync(id, write: true, track: true, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        // Optimistic concurrency: the update only succeeds if the row is still at the client's version.
        db.Entry(workspace!).Property(w => w.Version).OriginalValue = request.Version;
        workspace!.Update(request.Name.Trim(), request.Description?.Trim());
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(workspace));
    }

    private static async Task<IResult> Delete(
        Guid id, AppDbContext db, IAuthorizationService authorization, ClaimsPrincipal user, CancellationToken ct)
    {
        var (workspace, error) = await WorkspaceAccess.LoadAsync(id, write: true, track: true, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        workspace!.SoftDelete();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static WorkspaceResponse ToResponse(Workspace w) =>
        new(w.Id, w.Name, w.Description, w.CreatedAt, w.Version);
}
