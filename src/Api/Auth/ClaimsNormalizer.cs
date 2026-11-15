using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Api.Auth;

/// <summary>
/// The API reads <c>roles</c>, <c>tenant_id</c> and <c>preferred_username</c>. Some identity providers (Auth0, Entra ID)
/// only allow custom claims under a namespaced name such as <c>https://kb.example/roles</c>. When
/// <see cref="AuthOptions.RolesClaim"/> / <see cref="AuthOptions.TenantClaim"/> / <see cref="AuthOptions.NameClaim"/>
/// are set, the values are copied to the canonical names so the rest of the application stays provider-agnostic.
/// </summary>
public sealed class ClaimsNormalizer(IOptions<AuthOptions> options) : IClaimsTransformation
{
    private const string Marker = "kb_normalized";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var settings = options.Value;

        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(c => c.Type == Marker))
        {
            return Task.FromResult(principal); // anonymous, or already done (this method can run more than once per request)
        }

        Copy(identity, settings.RolesClaim, "roles");
        Copy(identity, settings.TenantClaim, "tenant_id");
        Copy(identity, settings.NameClaim, "preferred_username");
        identity.AddClaim(new Claim(Marker, "1"));
        return Task.FromResult(principal);
    }

    private static void Copy(ClaimsIdentity identity, string? from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || from == to)
        {
            return;
        }

        foreach (var claim in identity.FindAll(from).ToList())
        {
            identity.AddClaim(new Claim(to, claim.Value));
        }
    }
}
