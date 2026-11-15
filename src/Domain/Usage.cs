namespace Domain;

/// <summary>How a chat request ended. Stored as text so new outcomes never need a migration.</summary>
public static class ChatOutcome
{
    public const string Answered = "answered";
    public const string Cached = "cached";
    public const string NoSources = "no_sources";
    public const string Refused = "refused";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string QuotaExceeded = "quota_exceeded";
}

/// <summary>
/// One billable or measurable event: a chat request (tokens, cost, latency) or an embedding call. Append-only.
/// The cost is an <i>estimate</i> from configured per-model prices, not an invoice.
/// </summary>
public sealed class UsageLedgerEntry : Entity, ITenantOwned
{
    public const string KindChat = "chat";
    public const string KindEmbedding = "embedding";

    // Required by EF Core.
    private UsageLedgerEntry() { }

    public UsageLedgerEntry(
        Guid tenantId,
        string? userId,
        string kind,
        string model,
        string outcome,
        long inputTokens,
        long outputTokens,
        decimal costUsd)
    {
        TenantId = tenantId;
        UserId = userId;
        Kind = kind;
        Model = model;
        Outcome = outcome;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
    }

    public Guid TenantId { get; private set; }
    public string? UserId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = string.Empty;
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public decimal CostUsd { get; private set; }

    public Guid? ConversationId { get; private set; }
    public Guid? MessageId { get; private set; }

    /// <summary>Wall-clock time from receiving the question to finishing the answer.</summary>
    public int? LatencyMs { get; private set; }

    /// <summary>Time until the first piece of the answer was available to show.</summary>
    public int? TimeToFirstTokenMs { get; private set; }

    public double? GroundingScore { get; private set; }
    public int? RetrievedPassages { get; private set; }
    public int Neutralized { get; private set; }
    public int PiiRedactions { get; private set; }

    public UsageLedgerEntry InConversation(Guid conversationId, Guid? messageId)
    {
        ConversationId = conversationId;
        MessageId = messageId;
        return this;
    }

    public UsageLedgerEntry WithChatDetails(
        Guid conversationId, Guid? messageId, int latencyMs, int? firstTokenMs, double? grounding, int retrieved, int neutralized, int piiRedactions)
    {
        ConversationId = conversationId;
        MessageId = messageId;
        LatencyMs = latencyMs;
        TimeToFirstTokenMs = firstTokenMs;
        GroundingScore = grounding;
        RetrievedPassages = retrieved;
        Neutralized = neutralized;
        PiiRedactions = piiRedactions;
        return this;
    }
}

/// <summary>A tenant's monthly spending caps. Absent row = the configured defaults apply; null limit = unlimited.</summary>
public sealed class TenantQuota : Entity, ITenantOwned
{
    // Required by EF Core.
    private TenantQuota() { }

    public TenantQuota(Guid tenantId, long? monthlyTokenLimit, decimal? monthlyCostLimitUsd)
    {
        TenantId = tenantId;
        MonthlyTokenLimit = monthlyTokenLimit;
        MonthlyCostLimitUsd = monthlyCostLimitUsd;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid TenantId { get; private set; }
    public long? MonthlyTokenLimit { get; private set; }
    public decimal? MonthlyCostLimitUsd { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Set(long? monthlyTokenLimit, decimal? monthlyCostLimitUsd)
    {
        MonthlyTokenLimit = monthlyTokenLimit;
        MonthlyCostLimitUsd = monthlyCostLimitUsd;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
