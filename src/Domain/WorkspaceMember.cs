namespace Domain;

/// <summary>What a member may do inside one workspace (on top of their tenant-wide role).</summary>
public enum WorkspaceRole
{
    Viewer,
    Editor,
}

/// <summary>Grants a user access to a workspace. Non-admin users only see workspaces they are members of.</summary>
public sealed class WorkspaceMember : Entity, ITenantOwned, IAuditable
{
    // Required by EF Core.
    private WorkspaceMember() { }

    public WorkspaceMember(Guid tenantId, Guid workspaceId, string userId, WorkspaceRole role)
    {
        TenantId = tenantId;
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
    }

    public Guid TenantId { get; private set; }
    public Guid WorkspaceId { get; private set; }

    /// <summary>The identity provider's stable subject ID (JWT <c>sub</c>).</summary>
    public string UserId { get; private set; } = string.Empty;

    public WorkspaceRole Role { get; private set; }

    public void ChangeRole(WorkspaceRole role) => Role = role;
}
