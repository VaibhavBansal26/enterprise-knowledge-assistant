using Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>Contract tests for the Redis-backed idempotency store and rate limiter.</summary>
[Collection(ApiFixtureGroup.Name)]
public sealed class RedisStoreTests(ApiFactory factory)
{
    private IIdempotencyStore Store => factory.Services.GetRequiredService<IIdempotencyStore>();

    private static string NewKey() => $"test:{Guid.NewGuid():N}";

    [Fact]
    public async Task Idempotency_lifecycle_started_inprogress_completed_released()
    {
        var key = NewKey();
        var ct = CancellationToken.None;

        Assert.Equal(IdempotencyState.Started, (await Store.BeginAsync(key, "fp", ct)).State);
        Assert.Equal(IdempotencyState.InProgress, (await Store.BeginAsync(key, "fp", ct)).State);
        Assert.Equal(IdempotencyState.FingerprintMismatch, (await Store.BeginAsync(key, "other", ct)).State);

        var response = new StoredResponse(201, "application/json", "/x/1", [1, 2, 3]);
        await Store.CompleteAsync(key, "fp", response, ct);

        var replay = await Store.BeginAsync(key, "fp", ct);
        Assert.Equal(IdempotencyState.Completed, replay.State);
        Assert.Equal(201, replay.Response!.StatusCode);
        Assert.Equal("/x/1", replay.Response.Location);
        Assert.Equal([1, 2, 3], replay.Response.Body);

        await Store.ReleaseAsync(key, ct);
        Assert.Equal(IdempotencyState.Started, (await Store.BeginAsync(key, "fp", ct)).State);
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_requests_gets_to_execute()
    {
        var key = NewKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Store.BeginAsync(key, "fp", CancellationToken.None)));

        Assert.Equal(1, results.Count(r => r.State == IdempotencyState.Started));
        Assert.Equal(19, results.Count(r => r.State == IdempotencyState.InProgress));
    }

    [Fact]
    public async Task Rate_limiter_counts_per_key_and_reports_remaining()
    {
        var limiter = factory.Services.GetRequiredService<IRateLimiter>();
        var keyA = NewKey();
        var keyB = NewKey();

        var a1 = await limiter.TryAcquireAsync(keyA, CancellationToken.None);
        var a2 = await limiter.TryAcquireAsync(keyA, CancellationToken.None);
        var b1 = await limiter.TryAcquireAsync(keyB, CancellationToken.None);

        Assert.True(a1.Allowed);
        Assert.Equal(a1.Remaining - 1, a2.Remaining);
        Assert.Equal(a1.Remaining, b1.Remaining); // independent counters per client
        Assert.InRange(a1.RetryAfterSeconds, 1, 60);
    }
}
