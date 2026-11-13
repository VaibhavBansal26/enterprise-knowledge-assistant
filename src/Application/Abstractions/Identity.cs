namespace Application.Abstractions;

/// <summary>Role names carried in the access token's <c>roles</c> claim.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Editor = "editor";
    public const string Viewer = "viewer";
}

/// <summary>The tenant of the current request. Null when the request is unauthenticated or outside HTTP.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>Identity of the caller, as established by the access token.</summary>
public interface ICurrentUser
{
    /// <summary>Stable subject ID from the identity provider; null when unauthenticated.</summary>
    string? UserId { get; }

    string? CorrelationId { get; }

    bool IsAdmin { get; }
}
