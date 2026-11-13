namespace Application.Abstractions;

public readonly record struct RateLimitResult(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds);

/// <summary>Distributed rate limiter. Implementations must be safe across multiple API instances.</summary>
public interface IRateLimiter
{
    Task<RateLimitResult> TryAcquireAsync(string key, CancellationToken cancellationToken);
}
