namespace Application.Chat;

public enum LlmRole
{
    User,
    Assistant,
}

public sealed record LlmMessage(LlmRole Role, string Content);

public sealed record LlmRequest(string System, IReadOnlyList<LlmMessage> Messages, int MaxTokens);

/// <summary>One step of a streamed model response.</summary>
public abstract record LlmEvent;

public sealed record LlmTextDelta(string Text) : LlmEvent;

/// <summary>Always the last event. <see cref="StopReason"/> is "end_turn", "max_tokens", "refusal", ...</summary>
public sealed record LlmCompleted(string Model, int InputTokens, int OutputTokens, string StopReason) : LlmEvent;

public interface ILlmClient
{
    /// <summary>Identifier recorded with each answer (a model name, or e.g. "extractive-fallback").</summary>
    string ModelId { get; }

    /// <summary>Streams the answer. Honours cancellation end to end, down to the network call.</summary>
    IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>The language model could not be reached right now (outage, timeout, circuit open, rate limited).</summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException()
    {
    }

    public LlmUnavailableException(string message)
        : base(message)
    {
    }

    public LlmUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record RetrievalQuery(
    Guid TenantId,
    string UserId,
    bool IsAdmin,
    Guid? WorkspaceId,
    string QueryText,
    float[] QueryEmbedding,
    string EmbeddingModel,
    int Limit,
    double MinSimilarity);

public sealed record RetrievedChunk(
    Guid ChunkId,
    Guid DocumentId,
    Guid WorkspaceId,
    string DocumentTitle,
    int ChunkIndex,
    string Content,
    double Score);

public interface IRetrievalService
{
    /// <summary>
    /// Hybrid (vector + full-text) search. Permissions are enforced inside the SQL itself: tenant, workspace
    /// membership, deleted documents and workspaces, and document readiness are all part of the query, so a
    /// caller cannot receive a chunk it may not read regardless of what it asks for.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken);

    /// <summary>True when the user may currently read every one of the documents.</summary>
    Task<bool> CanReadAllAsync(
        Guid tenantId, string userId, bool isAdmin, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}

public sealed record CachedAnswer(
    string Question,
    string Answer,
    IReadOnlyList<CitationDto> Citations,
    IReadOnlyList<Guid> SourceDocumentIds,
    string Model,
    string PromptVersion);

public interface ISemanticCache
{
    /// <summary>The cached answer to the most similar earlier question, if it is similar enough.</summary>
    Task<CachedAnswer?> FindAsync(Guid tenantId, string scope, float[] embedding, CancellationToken cancellationToken);

    Task StoreAsync(Guid tenantId, string scope, float[] embedding, CachedAnswer answer, CancellationToken cancellationToken);

    /// <summary>Drop everything cached for the tenant (its documents changed). Cheap: bumps a version number.</summary>
    Task InvalidateAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>A source passage shown next to an answer.</summary>
public sealed record CitationDto(
    int Number, Guid DocumentId, Guid WorkspaceId, string DocumentTitle, int ChunkIndex, string Snippet);
