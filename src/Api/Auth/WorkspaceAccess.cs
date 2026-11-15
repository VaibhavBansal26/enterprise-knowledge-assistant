using System.Security.Claims;
using Application.Abstractions;
using Domain;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Auth;

public static class WorkspaceOperations
{
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement Write = new() { Name = nameof(Write) };
}

/// <summary>The membership rules, usable from HTTP handlers and from SignalR hubs alike.</summary>
public sealed class WorkspaceAccessEvaluator(AppDbContext db)
{
    public async Task<bool> CanAsync(Workspace workspace, string userId, bool isAdmin, bool write)
    {
        if (isAdmin)
        {
            return true;
        }

        var role = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspace.Id && m.UserId == userId)
            .Select(m => (WorkspaceRole?)m.Role)
            .FirstOrDefaultAsync();

        return role is not null && (!write || role == WorkspaceRole.Editor);
    }
}

/// <summary>
/// Resource-based authorization: tenant admins may access every workspace in their tenant; everyone else
/// needs a membership (any role to read, Editor to write). The tenant boundary itself is enforced earlier
/// by the DbContext query filter, and re-checked here as defence in depth.
/// </summary>
public sealed class WorkspaceAuthorizationHandler(WorkspaceAccessEvaluator access, ICurrentUser user, ITenantContext tenant)
    : AuthorizationHandler<OperationAuthorizationRequirement, Workspace>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, Workspace resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(resource);

        if (user.UserId is null || tenant.TenantId != resource.TenantId)
        {
            return;
        }

        var write = requirement == WorkspaceOperations.Write;
        if (await access.CanAsync(resource, user.UserId, user.IsAdmin, write))
        {
            context.Succeed(requirement);
        }
    }
}

internal static class WorkspaceAccess
{
    /// <summary>
    /// Loads a workspace the caller may access. A workspace the caller cannot read is reported as 404, never 403,
    /// so its existence is not revealed. A readable workspace without write access yields 403 for write operations.
    /// </summary>
    public static async Task<(Workspace? Workspace, IResult? Error)> LoadAsync(
        Guid id,
        bool write,
        bool track,
        AppDbContext db,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        IQueryable<Workspace> query = track ? db.Workspaces : db.Workspaces.AsNoTracking();
        var workspace = await query.FirstOrDefaultAsync(w => w.Id == id, ct);

        if (workspace is null || !(await authorization.AuthorizeAsync(user, workspace, WorkspaceOperations.Read)).Succeeded)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Workspace not found"));
        }

        if (write && !(await authorization.AuthorizeAsync(user, workspace, WorkspaceOperations.Write)).Succeeded)
        {
            return (null, Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "You do not have write access to this workspace."));
        }

        return (workspace, null);
    }
}
