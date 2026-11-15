using System.Text.Json;
using Application.Chat;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.Chat;

/// <summary>
/// Remembers answers to earlier questions and reuses one when a new question is nearly identical in meaning
/// (cosine similarity at or above a threshold), saving a model call and its latency and cost.
///
/// Layout: one Redis hash per (tenant, scope, corpus version); each field is an entry holding the question's
/// embedding and the answer. Similarity is computed in the app over a bounded set (default 100 entries), which
/// needs no Redis modules. Safety properties:
/// - The <b>corpus version</b> is part of the key and is bumped whenever the tenant's documents change, so stale
///   answers are unreachable immediately (and expire on their own).
/// - Entries are tenant-scoped, and a hit is only <i>offered</i>; the caller must still verify the asking user may
///   read every source behind the answer (see <see cref="CachedAnswer.SourceDocumentIds"/>).
/// </summary>
public sealed class RedisSemanticCache(IConnectionMultiplexer redis, IOptions<ChatOptions> options) : ISemanticCache
{
    private sealed record Entry(
        Guid Id,
        string EmbeddingBase64,
        CachedAnswer Answer,
        DateTimeOffset CreatedAt);

    private static string VersionKey(Guid tenant) => $"kb:corpus:{tenant:N}";

    private static string BucketKey(Guid tenant, string scope, long version) => $"kb:semcache:{tenant:N}:{scope}:v{version}";

    public async Task<CachedAnswer?> FindAsync(Guid tenantId, string scope, float[] embedding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var version = await CurrentVersionAsync(db, tenantId);
        var entries = await db.HashGetAllAsync(BucketKey(tenantId, scope, version));

        CachedAnswer? best = null;
        var bestScore = options.Value.CacheSimilarityThreshold;
        foreach (var field in entries)
        {
            var entry = JsonSerializer.Deserialize<Entry>(field.Value.ToString());
            if (entry is null)
            {
                continue;
            }

            var score = Cosine(embedding, Decode(entry.EmbeddingBase64));
            if (score >= bestScore)
            {
                bestScore = score;
                best = entry.Answer;
            }
        }

        return best;
    }

    public async Task StoreAsync(Guid tenantId, string scope, float[] embedding, CachedAnswer answer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var key = BucketKey(tenantId, scope, await CurrentVersionAsync(db, tenantId));
        var entry = new Entry(Guid.NewGuid(), Encode(embedding), answer, DateTimeOffset.UtcNow);

        await db.HashSetAsync(key, entry.Id.ToString("N"), JsonSerializer.Serialize(entry));
        await db.KeyExpireAsync(key, TimeSpan.FromHours(options.Value.CacheTtlHours));

        // Keep the bucket bounded: drop the oldest entries beyond the cap.
        var max = options.Value.CacheMaxEntriesPerScope;
        if (await db.HashLengthAsync(key) > max)
        {
            var all = await db.HashGetAllAsync(key);
            var oldest = all
                .Select(f => (Field: f.Name, Entry: JsonSerializer.Deserialize<Entry>(f.Value.ToString())!))
                .OrderBy(x => x.Entry.CreatedAt)
                .Take(all.Length - max)
                .Select(x => x.Field)
                .ToArray();
            await db.HashDeleteAsync(key, oldest);
        }
    }

    public async Task InvalidateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().StringIncrementAsync(VersionKey(tenantId));
    }

    private static async Task<long> CurrentVersionAsync(IDatabase db, Guid tenantId)
    {
        var value = await db.StringGetAsync(VersionKey(tenantId));
        return value.TryParse(out long version) ? version : 0;
    }

    public static string Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[] Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>Cosine similarity; vectors of different length are never similar.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return -1;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? -1 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
