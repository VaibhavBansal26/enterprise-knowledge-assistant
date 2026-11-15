using Api.Auth;
using Application.Abstractions;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Api.Hubs;

/// <summary>
/// Live ingestion progress. Clients subscribe to a workspace and receive <c>documentStatus</c> events for
/// its documents. Subscribing requires read access to the workspace, checked with the same rules as the REST API.
/// </summary>
[Authorize(Policy = Policies.Read)]
public sealed class IngestionHub(AppDbContext db, TenantContext tenant, WorkspaceAccessEvaluator access) : Hub
{
    public const string Path = "/hubs/ingestion";
    public const string StatusEvent = "documentStatus";

    public static string GroupName(Guid tenantId, Guid workspaceId) => $"t:{tenantId:N}:w:{workspaceId:N}";

    public async Task Subscribe(Guid workspaceId)
    {
        var user = Context.User!;

        // Hub invocations run outside the HTTP pipeline, so the tenant middleware has not set the tenant here.
        if (!Guid.TryParse(user.FindFirst("tenant_id")?.Value, out var tenantId) || tenantId == Guid.Empty)
        {
            throw new HubException("No tenant.");
        }

        tenant.TenantId = tenantId;

        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId);
        var userId = user.FindFirst("sub")?.Value;

        // Same answer for "does not exist" and "not allowed": existence is not revealed.
        if (workspace is null || userId is null
            || !await access.CanAsync(workspace, userId, user.IsInRole(Roles.Admin), write: false))
        {
            throw new HubException("Workspace not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(tenantId, workspaceId));
    }

    public Task Unsubscribe(Guid workspaceId)
    {
        _ = Guid.TryParse(Context.User?.FindFirst("tenant_id")?.Value, out var tenantId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(tenantId, workspaceId));
    }
}
