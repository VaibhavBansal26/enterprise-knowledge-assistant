namespace Application.Abstractions;

public readonly record struct RateLimitResult(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds);

/// <summary>Distributed rate limiter. Implementations must be safe across multiple API instances.</summary>
public interface IRateLimiter
{
    /// <param name="permitLimit">Overrides the configured limit for this call (e.g. a tighter cap for costly endpoints).</param>
    /// <param name="windowSeconds">Overrides the configured window for this call.</param>
    Task<RateLimitResult> TryAcquireAsync(
        string key, CancellationToken cancellationToken, int? permitLimit = null, int? windowSeconds = null);
}
