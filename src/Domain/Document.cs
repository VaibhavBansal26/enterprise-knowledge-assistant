namespace Domain;

public enum DocumentStatus
{
    Pending,
    Processing,
    Ready,
    Failed,
}

public sealed class Document : Entity, ISoftDeletable
{
    // Required by EF Core.
    private Document() { }

    public Document(
        Guid workspaceId,
        string title,
        string fileName,
        string contentType,
        long sizeBytes,
        IReadOnlyDictionary<string, string>? metadata)
    {
        WorkspaceId = workspaceId;
        Title = title;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Metadata = metadata is null ? [] : new Dictionary<string, string>(metadata);
    }

    public Guid WorkspaceId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public DocumentStatus Status { get; private set; } = DocumentStatus.Pending;
    public Dictionary<string, string> Metadata { get; private set; } = [];
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; private set; }

    public void Update(string title, IReadOnlyDictionary<string, string>? metadata)
    {
        Title = title;
        Metadata = metadata is null ? [] : new Dictionary<string, string>(metadata);
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAt = DateTimeOffset.UtcNow;
    }
}
