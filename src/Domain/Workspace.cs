namespace Domain;

public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedAt { get; }
}

public sealed class Workspace : Entity, ISoftDeletable
{
    // Required by EF Core.
    private Workspace() { }

    public Workspace(string name, string? description)
    {
        Name = name;
        Description = description;
    }

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
