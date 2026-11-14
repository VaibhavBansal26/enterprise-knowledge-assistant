using System.ComponentModel.DataAnnotations;

namespace Infrastructure.Chat;

public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>How many passages to retrieve before fitting them to the context budget.</summary>
    [Range(1, 30)]
    public int TopK { get; init; } = 8;

    /// <summary>Approximate tokens of retrieved passages placed in the prompt.</summary>
    [Range(200, 100_000)]
    public int ContextTokenBudget { get; init; } = 3000;

    /// <summary>Approximate tokens of earlier conversation turns kept in the prompt.</summary>
    [Range(0, 100_000)]
    public int HistoryTokenBudget { get; init; } = 2000;

    [Range(64, 16_000)]
    public int MaxAnswerTokens { get; init; } = 1024;

    /// <summary>
    /// Vector matches below this cosine similarity are dropped (full-text matches always qualify). Tune per
    /// embedding model: unrelated text scores near 0 for the offline hashing model, roughly 0.1-0.3 for neural ones.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinSimilarity { get; init; } = 0.2;

    public bool CacheEnabled { get; init; } = true;

    /// <summary>A cached answer is reused only when the new question is at least this similar to the cached one.</summary>
    [Range(0.5, 1.0)]
    public double CacheSimilarityThreshold { get; init; } = 0.97;

    [Range(1, 24 * 30)]
    public int CacheTtlHours { get; init; } = 24;

    [Range(1, 10_000)]
    public int CacheMaxEntriesPerScope { get; init; } = 100;

    /// <summary>Each message calls a paid model, so it has its own, tighter per-user limit (per minute).</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; init; } = 20;
}
