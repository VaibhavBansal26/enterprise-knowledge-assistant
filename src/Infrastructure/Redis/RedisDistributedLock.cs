using Application.Ingestion;
using StackExchange.Redis;

namespace Infrastructure.Redis;

/// <summary>
/// Single-instance Redis lock: <c>SET key token NX PX ttl</c> to acquire, and a compare-and-delete script to
/// release so a lock that expired and was taken by someone else is never released by its previous owner.
/// </summary>
public sealed class RedisDistributedLock(IConnectionMultiplexer redis) : IDistributedLock
{
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end
        """;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();
        var token = Guid.NewGuid().ToString("N");
        var acquired = await db.StringSetAsync($"lock:{key}", token, ttl, When.NotExists);
        return acquired ? new Handle(db, $"lock:{key}", token) : null;
    }

    private sealed class Handle(IDatabase db, string key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await db.ScriptEvaluateAsync(ReleaseScript, [new RedisKey(key)], [new RedisValue(token)]);
    }
}
