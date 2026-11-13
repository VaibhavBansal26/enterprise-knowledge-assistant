using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace Api.Web;

/// <summary>
/// Ensures every request has a correlation ID: reuses a well-formed inbound <c>X-Correlation-Id</c>
/// or generates one, echoes it on the response, and attaches it to log events and the current trace.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    // Inbound values are untrusted: restrict charset and length so they cannot inject into logs or headers.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var id = incoming is not null && ValidId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        Activity.Current?.SetTag("correlation_id", id);
        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
