namespace Domain;

/// <summary>A passage of a document with its embedding, the unit that retrieval searches over.</summary>
public sealed class DocumentChunk : Entity, ITenantOwned
{
    /// <summary>Length of every embedding vector; must match the database column.</summary>
    public const int EmbeddingDimensions = 384;

    // Required by EF Core.
    private DocumentChunk() { }

    public DocumentChunk(Guid tenantId, Guid workspaceId, Guid documentId, int index, string content, float[] embedding)
    {
        if (embedding.Length != EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Embedding must have {EmbeddingDimensions} dimensions, got {embedding.Length}.", nameof(embedding));
        }

        TenantId = tenantId;
        WorkspaceId = workspaceId;
        DocumentId = documentId;
        Index = index;
        Content = content;
        Embedding = embedding;
    }

    public Guid TenantId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid DocumentId { get; private set; }

    /// <summary>Position of the chunk within the document, starting at 0.</summary>
    public int Index { get; private set; }

    public string Content { get; private set; } = string.Empty;

#pragma warning disable CA1819 // Embeddings are fixed-size numeric vectors; an array is the natural, mapped representation.
    public float[] Embedding { get; private set; } = [];
#pragma warning restore CA1819
}
