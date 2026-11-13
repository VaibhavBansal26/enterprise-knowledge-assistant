using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Application.Abstractions;
using Microsoft.Net.Http.Headers;

namespace Api.Web;

/// <summary>
/// Makes POST requests safe to retry. When a client sends <c>Idempotency-Key</c>, the first response is
/// stored and replayed for later requests with the same key and payload. A different payload under the
/// same key is rejected (422); a duplicate while the first is still running gets 409. Server errors are not
/// stored, so the client may retry. If the store is down the request is refused (503): a client that asked
/// for idempotency must not silently get duplicate side effects.
/// </summary>
public sealed partial class IdempotencyMiddleware(
    RequestDelegate next,
    IIdempotencyStore store,
    IProblemDetailsService problemDetails,
    ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayHeader = "Idempotent-Replayed";

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex ValidKey();

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsPost(context.Request.Method)
            || !context.Request.Headers.TryGetValue(HeaderName, out var header))
        {
            await next(context);
            return;
        }

        var clientKey = header.ToString();
        if (!ValidKey().IsMatch(clientKey))
        {
            await WriteProblem(context, StatusCodes.Status400BadRequest, "Invalid Idempotency-Key",
                "Use 1-128 characters from A-Z, a-z, 0-9, '.', '_', ':' and '-'.");
            return;
        }

        // Scope the key to the caller and the endpoint: two users (or two routes) may reuse the same key
        // value, and one user must never be able to replay another user's stored response.
        var caller = context.User.FindFirst("sub")?.Value ?? "anonymous";
        var storeKey = $"{caller}:{context.Request.Method}:{context.Request.Path}:{clientKey}";
        var fingerprint = await ComputeFingerprintAsync(context.Request);

        IdempotencyBeginResult begin;
        try
        {
            begin = await store.BeginAsync(storeKey, fingerprint, context.RequestAborted);
        }
#pragma warning disable CA1031 // Store failures are mapped to 503 for the client.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogStoreUnavailable(logger, ex);
            await WriteProblem(context, StatusCodes.Status503ServiceUnavailable, "Service unavailable",
                "Idempotency cannot be guaranteed right now. Retry shortly.");
            return;
        }

        switch (begin.State)
        {
            case IdempotencyState.Completed:
                await Replay(context, begin.Response!);
                return;
            case IdempotencyState.InProgress:
                await WriteProblem(context, StatusCodes.Status409Conflict, "Request in progress",
                    "A request with this Idempotency-Key is still being processed.");
                return;
            case IdempotencyState.FingerprintMismatch:
                await WriteProblem(context, StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reused",
                    "This Idempotency-Key was already used with a different request.");
                return;
        }

        await ExecuteAndStore(context, storeKey, fingerprint);
    }

    private async Task ExecuteAndStore(HttpContext context, string storeKey, string fingerprint)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = original;
            await ReleaseQuietly(storeKey);
            throw;
        }

        context.Response.Body = original;

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await ReleaseQuietly(storeKey);
        }
        else
        {
            var stored = new StoredResponse(
                context.Response.StatusCode,
                context.Response.ContentType,
                context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
                buffer.ToArray());
            try
            {
                await store.CompleteAsync(storeKey, fingerprint, stored, CancellationToken.None);
            }
#pragma warning disable CA1031 // The response was already produced; losing the replay record must not fail it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStoreFailed(logger, ex);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }

    private async Task ReleaseQuietly(string storeKey)
    {
        try
        {
            await store.ReleaseAsync(storeKey, CancellationToken.None);
        }
#pragma warning disable CA1031 // Best effort; the in-flight lock also expires on its own.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReleaseFailed(logger, ex);
        }
    }

    private static async Task Replay(HttpContext context, StoredResponse stored)
    {
        context.Response.StatusCode = stored.StatusCode;
        context.Response.ContentType = stored.ContentType;
        if (stored.Location is not null)
        {
            context.Response.Headers[HeaderNames.Location] = stored.Location;
        }

        context.Response.Headers[ReplayHeader] = "true";
        await context.Response.Body.WriteAsync(stored.Body, context.RequestAborted);
    }

    private static async Task<string> ComputeFingerprintAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var sha = SHA256.Create();

        // A multipart body differs on every retry (the client picks a fresh random boundary each time), so
        // hashing raw bytes would reject every genuine retry of an upload. Fingerprint what the request
        // *means* instead: its form fields and each file's name and content.
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var parts = new List<string>();
            foreach (var field in form.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                parts.Add($"field:{field.Key}={field.Value}");
            }

            foreach (var file in form.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                await using var stream = file.OpenReadStream();
                var fileHash = Convert.ToHexString(await sha.ComputeHashAsync(stream));
                parts.Add($"file:{file.Name}:{file.FileName}:{file.Length}:{fileHash}");
            }

            request.Body.Position = 0;
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts))));
        }

        var hash = await sha.ComputeHashAsync(request.Body);
        request.Body.Position = 0;
        return Convert.ToHexString(hash);
    }

    private async Task WriteProblem(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency store unavailable")]
    private static partial void LogStoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to store idempotent response")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to release idempotency key")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);
}
