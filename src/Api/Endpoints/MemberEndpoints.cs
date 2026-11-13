using System.Security.Claims;
using Api.Auth;
using Api.Web;
using Application.Workspaces;
using Domain;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>Workspace membership management. Tenant admins only.</summary>
public static class MemberEndpoints
{
    private const int MaxUserIdLength = 128;

    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspaces/{workspaceId:guid}/members")
            .WithTags("Members")
            .RequireAuthorization(Policies.Admin);

        group.MapGet("/", List);
        group.MapPut("/{userId}", Upsert).AddEndpointFilter<ValidationFilter<UpsertMemberRequest>>();
        group.MapDelete("/{userId}", Remove);

        return app;
    }

    private static IResult InvalidUserId() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["userId"] = [$"userId must be 1-{MaxUserIdLength} characters."],
    });

    private static bool IsValid(string userId) => userId.Length is > 0 and <= MaxUserIdLength;

    private static async Task<IResult> List(
        Guid workspaceId, AppDbContext db, IAuthorizationService authorization, ClaimsPrincipal user, CancellationToken ct)
    {
        var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: false, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var members = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.UserId)
            .ToListAsync(ct);

        return Results.Ok(members.Select(ToResponse));
    }

    private static async Task<IResult> Upsert(
        Guid workspaceId,
        string userId,
        UpsertMemberRequest request,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (!IsValid(userId))
        {
            return InvalidUserId();
        }

        var (workspace, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: true, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var role = Enum.Parse<WorkspaceRole>(request.Role, ignoreCase: true);
        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);

        if (member is null)
        {
            member = new WorkspaceMember(workspace!.TenantId, workspaceId, userId, role);
            db.WorkspaceMembers.Add(member);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/workspaces/{workspaceId}/members/{Uri.EscapeDataString(userId)}", ToResponse(member));
        }

        member.ChangeRole(role);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(member));
    }

    private static async Task<IResult> Remove(
        Guid workspaceId,
        string userId,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (!IsValid(userId))
        {
            return InvalidUserId();
        }

        var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: true, track: false, db, authorization, user, ct);
        if (error is not null)
        {
            return error;
        }

        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);
        if (member is null)
        {
            return Results.Problem(statusCode: 404, title: "Member not found");
        }

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static MemberResponse ToResponse(WorkspaceMember m) => new(m.UserId, m.Role.ToString(), m.CreatedAt);
}
