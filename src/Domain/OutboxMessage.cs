namespace Domain;

/// <summary>
/// A unit of background work, written in the same transaction as the change that requires it
/// (transactional outbox), then claimed and retried by workers. Not tenant-filtered: workers are
/// system actors that restore the tenant from <see cref="TenantId"/> before touching tenant data.
/// </summary>
public sealed class OutboxMessage : Entity
{
    public const string DocumentUploaded = "document.uploaded";

    // Required by EF Core.
    private OutboxMessage() { }

    public OutboxMessage(Guid tenantId, string type, string payload)
    {
        TenantId = tenantId;
        Type = type;
        Payload = payload;
        NextAttemptAt = DateTimeOffset.UtcNow;
    }

    public Guid TenantId { get; private set; }
    public string Type { get; private set; } = string.Empty;

    /// <summary>JSON describing the work (for documents: the document ID).</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>How many times a worker has claimed this message.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>While in the future, a worker holds the message. A crashed worker's lease simply expires.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>Set when retries are exhausted: the dead-letter marker. Dead messages are never claimed again.</summary>
    public DateTimeOffset? DeadAt { get; private set; }

    public string? LastError { get; private set; }
}
