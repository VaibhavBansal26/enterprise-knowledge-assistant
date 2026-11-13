using Application.Abstractions;

namespace Api.Web;

/// <summary>Reads the caller's identity from the validated access token on the current request.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private HttpContext? Context => accessor.HttpContext;

    public string? UserId => Context?.User.FindFirst("sub")?.Value;

    public string? CorrelationId =>
        Context?.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id) == true ? id as string : null;

    public bool IsAdmin => Context?.User.IsInRole(Roles.Admin) == true;
}
