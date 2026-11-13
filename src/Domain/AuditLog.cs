namespace Domain;

/// <summary>Append-only record of who changed what. Stores property names, never values, to avoid copying data.</summary>
public sealed class AuditLog : Entity, ITenantOwned
{
    // Required by EF Core.
    private AuditLog() { }

    public AuditLog(
        Guid tenantId,
        string? userId,
        string action,
        string entityType,
        Guid entityId,
        string? changedFields,
        string? correlationId)
    {
        TenantId = tenantId;
        UserId = userId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        ChangedFields = changedFields;
        CorrelationId = correlationId;
    }

    public Guid TenantId { get; private set; }
    public string? UserId { get; private set; }

    /// <summary>Created, Updated or Deleted.</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }

    /// <summary>Comma-separated names of the properties that changed (updates only).</summary>
    public string? ChangedFields { get; private set; }

    public string? CorrelationId { get; private set; }
}
