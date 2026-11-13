using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Application.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.Redis;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a completed response can be replayed.</summary>
    [Range(1, 24 * 30)]
    public int ResultTtlHours { get; init; } = 24;

    /// <summary>How long an in-flight key blocks duplicates; guards against crashed requests.</summary>
    [Range(1, 3600)]
    public int LockSeconds { get; init; } = 30;
}

public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis, IOptions<IdempotencyOptions> options)
    : IIdempotencyStore
{
    private sealed record Entry(bool Done, string Fingerprint, StoredResponse? Response);

    private static string RedisKey(string key) => $"idem:{key}";

    public async Task<IdempotencyBeginResult> BeginAsync(
        string key, string fingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();
        var lockTtl = TimeSpan.FromSeconds(options.Value.LockSeconds);
        var pending = JsonSerializer.Serialize(new Entry(false, fingerprint, null));

        // Two attempts: the key can expire between the failed SET NX and the read.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await db.StringSetAsync(RedisKey(key), pending, lockTtl, When.NotExists))
            {
                return new IdempotencyBeginResult(IdempotencyState.Started);
            }

            var existing = await db.StringGetAsync(RedisKey(key));
            if (existing.IsNullOrEmpty)
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<Entry>(existing.ToString())!;
            if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new IdempotencyBeginResult(IdempotencyState.FingerprintMismatch);
            }

            return entry.Done
                ? new IdempotencyBeginResult(IdempotencyState.Completed, entry.Response)
                : new IdempotencyBeginResult(IdempotencyState.InProgress);
        }

        return new IdempotencyBeginResult(IdempotencyState.InProgress);
    }

    public async Task CompleteAsync(
        string key, string fingerprint, StoredResponse response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var done = JsonSerializer.Serialize(new Entry(true, fingerprint, response));
        await redis.GetDatabase().StringSetAsync(
            RedisKey(key), done, TimeSpan.FromHours(options.Value.ResultTtlHours));
    }

    public async Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().KeyDeleteAsync(RedisKey(key));
    }
}
