using Infrastructure.Persistence;

namespace Api.Web;

/// <summary>
/// Resolves the tenant from the token's <c>tenant_id</c> claim and makes it available to the DbContext's
/// query filters. An authenticated token without a valid tenant is refused: it must never fall through
/// to an unfiltered view of the data.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next, IProblemDetailsService problemDetails)
{
    public const string ClaimName = "tenant_id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(context.User.FindFirst(ClaimName)?.Value, out var tenantId) || tenantId == Guid.Empty)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await problemDetails.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status403Forbidden,
                        Title = "Forbidden",
                        Detail = "The access token is not associated with a tenant.",
                    },
                });
                return;
            }

            context.RequestServices.GetRequiredService<TenantContext>().TenantId = tenantId;
        }

        await next(context);
    }
}
