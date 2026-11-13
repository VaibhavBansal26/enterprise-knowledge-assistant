namespace Application.Abstractions;

/// <summary>A response captured for replay when a client retries with the same Idempotency-Key.</summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, string? Location, byte[] Body);

public enum IdempotencyState
{
    /// <summary>First time this key is seen; the caller must execute the request.</summary>
    Started,

    /// <summary>Another request with this key is still executing.</summary>
    InProgress,

    /// <summary>A previous request with this key finished; replay <see cref="IdempotencyBeginResult.Response"/>.</summary>
    Completed,

    /// <summary>The key was already used with a different request payload.</summary>
    FingerprintMismatch,
}

public sealed record IdempotencyBeginResult(IdempotencyState State, StoredResponse? Response = null);

public interface IIdempotencyStore
{
    Task<IdempotencyBeginResult> BeginAsync(string key, string fingerprint, CancellationToken cancellationToken);

    Task CompleteAsync(string key, string fingerprint, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Forget an in-progress key (request failed), so the client can retry.</summary>
    Task ReleaseAsync(string key, CancellationToken cancellationToken);
}
