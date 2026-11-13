namespace Domain;

public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedAt { get; }
}

/// <summary>Data that belongs to exactly one tenant; always filtered by the current tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>Changes to these entities are recorded in the audit log.</summary>
public interface IAuditable;

public sealed class Workspace : Entity, ISoftDeletable, ITenantOwned, IAuditable
{
    // Required by EF Core.
    private Workspace() { }

    public Workspace(Guid tenantId, string name, string? description)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; private set; }

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAt = DateTimeOffset.UtcNow;
    }
}
