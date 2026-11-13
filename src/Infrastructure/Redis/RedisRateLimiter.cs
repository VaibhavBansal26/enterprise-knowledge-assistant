using System.ComponentModel.DataAnnotations;
using Application.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.Redis;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; init; } = true;

    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; } = 100;

    [Range(1, 86_400)]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>Namespaces keys when several environments share one Redis.</summary>
    [Required, MinLength(1)]
    public string KeyPrefix { get; init; } = "rl";
}

/// <summary>Fixed-window limiter backed by Redis so limits hold across API instances.</summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, IOptions<RateLimitOptions> options) : IRateLimiter
{
    // INCR + EXPIRE in one atomic step; returns { count, ttlSeconds }.
    private const string Script = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        local ttl = redis.call('TTL', KEYS[1])
        return { count, ttl }
        """;

    public async Task<RateLimitResult> TryAcquireAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var o = options.Value;
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [new RedisKey($"{o.KeyPrefix}:{key}")],
            [o.WindowSeconds]))!;

        var count = (long)result[0];
        var ttl = (long)result[1];
        var remaining = (int)Math.Max(0, o.PermitLimit - count);
        var retryAfter = ttl > 0 ? (int)ttl : o.WindowSeconds;

        return new RateLimitResult(count <= o.PermitLimit, o.PermitLimit, remaining, retryAfter);
    }
}
