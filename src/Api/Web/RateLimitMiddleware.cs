using System.Globalization;
using Application.Abstractions;
using Infrastructure.Redis;
using Microsoft.Extensions.Options;

namespace Api.Web;

/// <summary>
/// Per-client request limiting backed by Redis. Health probes are exempt. If Redis is unavailable the
/// limiter fails open (and logs), because an outage of the limiter must not take the API down.
/// </summary>
public sealed partial class RateLimitMiddleware(
    RequestDelegate next,
    IRateLimiter limiter,
    IOptions<RateLimitOptions> options,
    IProblemDetailsService problemDetails,
    ILogger<RateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!options.Value.Enabled || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        RateLimitResult result;
        try
        {
            // Authenticated callers are limited per user (so one NAT'd office cannot starve itself, and a
            // user cannot dodge the limit by changing address); anonymous callers per client address.
            var clientKey = context.User.FindFirst("sub")?.Value is { Length: > 0 } sub
                ? $"u:{sub}"
                : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
            result = await limiter.TryAcquireAsync(clientKey, context.RequestAborted);
        }
#pragma warning disable CA1031 // Any limiter failure must fail open, never fail the request.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogLimiterUnavailable(logger, ex);
            await next(context);
            return;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-RateLimit-Limit"] = result.Limit.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });

        if (result.Allowed)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = $"Rate limit exceeded. Retry after {result.RetryAfterSeconds} seconds.",
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limiter unavailable; allowing request")]
    private static partial void LogLimiterUnavailable(ILogger logger, Exception exception);
}
