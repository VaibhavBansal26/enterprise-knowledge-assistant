using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Api.Tests;

public sealed record TestUser(string Sub, Guid? TenantId, params string[] Roles);

/// <summary>Issues signed access tokens that the API accepts in tests (it trusts <see cref="Key"/>).</summary>
public static class TestAuth
{
    public const string Issuer = "https://idp.test.invalid/realms/kb";
    public const string Audience = "kb-api";

    public static readonly SymmetricSecurityKey Key =
        new(Encoding.UTF8.GetBytes("unit-test-signing-key-0123456789-abcdefghijklmnop"));

    public static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    public static readonly TestUser AdminA = new("user-admin-a", TenantA, "admin");
    public static readonly TestUser EditorA1 = new("user-editor-a1", TenantA, "editor");
    public static readonly TestUser EditorA2 = new("user-editor-a2", TenantA, "editor");
    public static readonly TestUser ViewerA = new("user-viewer-a", TenantA, "viewer");
    public static readonly TestUser AdminB = new("user-admin-b", TenantB, "admin");

    public static string Token(
        TestUser user,
        string audience = Audience,
        string issuer = Issuer,
        DateTime? expires = null,
        SecurityKey? key = null,
        bool includeTenant = true)
    {
        var claims = new List<Claim> { new("sub", user.Sub), new("preferred_username", user.Sub) };
        if (includeTenant && user.TenantId is { } tenant)
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        claims.AddRange(user.Roles.Select(r => new Claim("roles", r)));

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = audience,
            NotBefore = now.AddMinutes(-5),
            Expires = expires ?? now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
