namespace Domain;

public enum DocumentStatus
{
    /// <summary>Uploaded and stored; waiting for the ingestion worker.</summary>
    Pending,

    /// <summary>A worker is parsing, chunking and embedding it.</summary>
    Processing,

    /// <summary>Chunked and embedded; searchable.</summary>
    Ready,

    /// <summary>Could not be ingested. See <see cref="Document.FailureReason"/>; can be reprocessed.</summary>
    Failed,
}

public sealed class Document : Entity, ISoftDeletable, ITenantOwned, IAuditable
{
    // Required by EF Core.
    private Document() { }

    public Document(
        Guid tenantId,
        Guid workspaceId,
        string title,
        string fileName,
        string contentType,
        long sizeBytes,
        string storageKey,
        string contentHash)
    {
        TenantId = tenantId;
        WorkspaceId = workspaceId;
        Title = title;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        StorageKey = storageKey;
        ContentHash = contentHash;
    }

    public Guid TenantId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }

    /// <summary>Opaque key of the stored file in blob storage. Never derived from user input.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the uploaded file.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    public DocumentStatus Status { get; private set; } = DocumentStatus.Pending;
    public int ChunkCount { get; private set; }
    public string? FailureReason { get; private set; }
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

    public void MarkProcessing()
    {
        Status = DocumentStatus.Processing;
        FailureReason = null;
    }

    public void MarkReady(int chunkCount)
    {
        Status = DocumentStatus.Ready;
        ChunkCount = chunkCount;
        FailureReason = null;
    }

    public void MarkFailed(string reason)
    {
        Status = DocumentStatus.Failed;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }

    /// <summary>Send the document back through ingestion (after a failure, or to re-embed).</summary>
    public void Requeue()
    {
        Status = DocumentStatus.Pending;
        FailureReason = null;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAt = DateTimeOffset.UtcNow;
    }
}
