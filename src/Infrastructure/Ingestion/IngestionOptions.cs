using System.ComponentModel.DataAnnotations;

namespace Infrastructure.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Run the background worker in this process. Turn off to run API-only instances.</summary>
    public bool WorkerEnabled { get; init; } = true;

    [Range(10, 60_000)]
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>Documents processed in parallel by this instance.</summary>
    [Range(1, 64)]
    public int Concurrency { get; init; } = 2;

    /// <summary>A transient failure is retried until the message has been claimed this many times, then dead-lettered.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 5;

    /// <summary>First retry delay in seconds; doubles each attempt (capped at 15 minutes). 0 retries immediately.</summary>
    [Range(0, 3600)]
    public double RetryBaseSeconds { get; init; } = 5;

    /// <summary>How long a claimed message stays reserved. A crashed worker's claim expires after this.</summary>
    [Range(10, 86_400)]
    public int LeaseSeconds { get; init; } = 600;

    [Range(200, 8000)]
    public int ChunkSizeChars { get; init; } = 1000;

    [Range(0, 1000)]
    public int ChunkOverlapChars { get; init; } = 150;

    /// <summary>Guards against decompression bombs and absurdly large extractions.</summary>
    [Range(1000, 100_000_000)]
    public int MaxExtractedChars { get; init; } = 5_000_000;

    [Range(1, 512)]
    public int EmbeddingBatchSize { get; init; } = 64;
}
