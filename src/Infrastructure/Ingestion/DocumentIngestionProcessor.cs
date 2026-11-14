using Application.Chat;
using Application.Ingestion;
using Domain;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ingestion;

public enum ProcessOutcome
{
    /// <summary>Done, or nothing to do (document gone). The message can be completed.</summary>
    Completed,

    /// <summary>Another worker holds the document right now. Try again later without counting an attempt.</summary>
    Busy,
}

/// <summary>
/// Ingests one document: parse → chunk → embed → store chunks, moving it Pending → Processing → Ready.
/// Idempotent (chunks are replaced), so the at-least-once delivery of the queue is safe.
/// Must run with the document's tenant set on <see cref="TenantContext"/>.
/// </summary>
public sealed partial class DocumentIngestionProcessor(
    AppDbContext db,
    IBlobStorage blobs,
    IDocumentTextExtractor extractor,
    IEmbeddingClient embeddings,
    IDistributedLock locks,
    IIngestionNotifier notifier,
    ISemanticCache semanticCache,
    IOptions<IngestionOptions> options,
    ILogger<DocumentIngestionProcessor> logger)
{
    public async Task<ProcessOutcome> ProcessAsync(Guid documentId, CancellationToken ct)
    {
        // A tenant filter and soft-delete filter apply: a deleted or foreign document simply is not found.
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null)
        {
            LogMissing(logger, documentId);
            return ProcessOutcome.Completed;
        }

        await using var guard = await locks.TryAcquireAsync(
            $"ingest:{documentId:N}", TimeSpan.FromSeconds(options.Value.LeaseSeconds), ct);
        if (guard is null)
        {
            return ProcessOutcome.Busy;
        }

        document.MarkProcessing();
        await db.SaveChangesAsync(ct);
        await Notify(document, ct);

        try
        {
            var chunks = await BuildChunksAsync(document, ct);

            // Replace, never append: re-running (retry, reprocess, duplicate delivery) must not duplicate chunks.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Chunks.Where(c => c.DocumentId == document.Id).ExecuteDeleteAsync(ct);
            db.Chunks.AddRange(chunks);
            document.MarkReady(chunks.Count);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            LogIngested(logger, documentId, chunks.Count);
            await InvalidateCacheAsync(document.TenantId, ct);
        }
        catch (IngestionException ex) when (ex.IsPermanent)
        {
            // Retrying cannot help (corrupt file, no text...). Fail the document now and complete the message.
            db.ChangeTracker.Clear();
            await FailAsync(document.TenantId, documentId, ex.Message, ct);
            LogPermanentFailure(logger, documentId, ex.Message);
            return ProcessOutcome.Completed;
        }

        await Notify(document, ct);
        return ProcessOutcome.Completed;
    }

    /// <summary>Marks a document failed after the queue gave up on it (retries exhausted).</summary>
    public async Task FailAsync(Guid tenantId, Guid documentId, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.TenantId == tenantId, ct);
        if (document is null)
        {
            return;
        }

        document.MarkFailed(reason);
        await db.SaveChangesAsync(ct);
        await Notify(document, ct);
    }

    private async Task<List<DocumentChunk>> BuildChunksAsync(Document document, CancellationToken ct)
    {
        var format = SupportedFormats.FromContentType(document.ContentType)
            ?? throw IngestionException.Permanent($"Unsupported content type '{document.ContentType}'.");

        string text;
        await using (var content = await blobs.OpenReadAsync(document.StorageKey, ct))
        {
            text = await extractor.ExtractAsync(format, content, ct);
        }

        var pieces = TextChunker.Chunk(text, options.Value.ChunkSizeChars, options.Value.ChunkOverlapChars);
        if (pieces.Count == 0)
        {
            throw IngestionException.Permanent("The document contains no extractable text.");
        }

        var chunks = new List<DocumentChunk>(pieces.Count);
        for (var offset = 0; offset < pieces.Count; offset += options.Value.EmbeddingBatchSize)
        {
            var batch = pieces.Skip(offset).Take(options.Value.EmbeddingBatchSize).ToList();
            var vectors = await embeddings.EmbedAsync(batch, ct);
            for (var i = 0; i < batch.Count; i++)
            {
                chunks.Add(new DocumentChunk(
                    document.TenantId, document.WorkspaceId, document.Id, offset + i, batch[i], vectors[i], embeddings.ModelId));
            }
        }

        return chunks;
    }

    /// <summary>The tenant's searchable content changed, so cached answers may be stale. Best-effort.</summary>
    private async Task InvalidateCacheAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await semanticCache.InvalidateAsync(tenantId, ct);
        }
#pragma warning disable CA1031 // A cache outage must not fail ingestion; entries also expire on their own.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogCacheInvalidationFailed(logger, ex, tenantId);
        }
    }

    private async Task Notify(Document document, CancellationToken ct)
    {
        try
        {
            await notifier.DocumentStatusChangedAsync(
                new DocumentStatusChanged(
                    document.TenantId,
                    document.WorkspaceId,
                    document.Id,
                    document.Status.ToString(),
                    document.ChunkCount,
                    document.FailureReason),
                ct);
        }
#pragma warning disable CA1031 // Progress notifications are best-effort and must never fail ingestion.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNotifyFailed(logger, ex, document.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} no longer exists; nothing to ingest")]
    private static partial void LogMissing(ILogger logger, Guid documentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingested document {DocumentId} into {ChunkCount} chunks")]
    private static partial void LogIngested(ILogger logger, Guid documentId, int chunkCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} failed permanently: {Reason}")]
    private static partial void LogPermanentFailure(ILogger logger, Guid documentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not invalidate the semantic cache for tenant {TenantId}")]
    private static partial void LogCacheInvalidationFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish status for document {DocumentId}")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid documentId);
}
