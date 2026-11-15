using System.Security.Claims;
using Api.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Tests;

public class ClaimsNormalizerTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static ClaimsNormalizer Normalizer(AuthOptions options) => new(Options.Create(options));

    [Fact]
    public async Task Namespaced_claims_are_copied_to_the_canonical_names()
    {
        var normalizer = Normalizer(new AuthOptions
        {
            RolesClaim = "https://kb.example/roles",
            TenantClaim = "https://kb.example/tenant_id",
            NameClaim = "https://kb.example/name",
        });
        var principal = Principal(
            new Claim("sub", "auth0|1"),
            new Claim("https://kb.example/roles", "admin"),
            new Claim("https://kb.example/roles", "editor"),
            new Claim("https://kb.example/tenant_id", "a0000000-0000-0000-0000-000000000001"),
            new Claim("https://kb.example/name", "Alice"));

        var result = await normalizer.TransformAsync(principal);

        Assert.Equal(["admin", "editor"], result.FindAll("roles").Select(c => c.Value).Order(StringComparer.Ordinal));
        Assert.Equal("a0000000-0000-0000-0000-000000000001", result.FindFirst("tenant_id")?.Value);
        Assert.Equal("Alice", result.FindFirst("preferred_username")?.Value);
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate_claims()
    {
        var normalizer = Normalizer(new AuthOptions { RolesClaim = "https://kb.example/roles" });
        var principal = Principal(new Claim("https://kb.example/roles", "viewer"));

        await normalizer.TransformAsync(principal);
        await normalizer.TransformAsync(principal);

        Assert.Single(principal.FindAll("roles"));
    }

    [Fact]
    public async Task Without_configuration_claims_are_left_alone()
    {
        var principal = Principal(new Claim("roles", "viewer"), new Claim("tenant_id", "t"));

        var result = await Normalizer(new AuthOptions()).TransformAsync(principal);

        Assert.Single(result.FindAll("roles"));
        Assert.Single(result.FindAll("tenant_id"));
    }

    [Fact]
    public async Task Anonymous_principals_are_untouched()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await Normalizer(new AuthOptions { RolesClaim = "x" }).TransformAsync(anonymous);

        Assert.False(result.Identity!.IsAuthenticated);
        Assert.Empty(result.Claims);
    }
}

/// <summary>A token shaped like Auth0's (namespaced custom claims) through the real JWT pipeline.</summary>
[Collection(ApiFixtureGroup.Name)]
public sealed class NamespacedClaimsEndToEndTests(ApiFactory factory)
{
    private const string Ns = "https://kb.example/";

    private static string Auth0StyleToken(string role)
    {
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "auth0|abc123"),
                new Claim(Ns + "roles", role),
                new Claim(Ns + "tenant_id", TestAuth.TenantA.ToString()),
            ]),
            Issuer = TestAuth.Issuer,
            Audience = TestAuth.Audience,
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                TestAuth.Key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        };
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);
    }

    private HttpClient ClientWithMapping(string role)
    {
        var client = factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Auth:RolesClaim", Ns + "roles");
                b.UseSetting("Auth:TenantClaim", Ns + "tenant_id");
            })
            .CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Auth0StyleToken(role));
        return client;
    }

    [Fact]
    public async Task Namespaced_admin_token_is_authorised_and_scoped_to_its_tenant()
    {
        var client = ClientWithMapping("admin");

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/audit")).StatusCode);   // admin-only
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/workspaces")).StatusCode); // needs a tenant
    }

    [Fact]
    public async Task Namespaced_viewer_token_is_not_an_admin()
    {
        var client = ClientWithMapping("viewer");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/workspaces")).StatusCode);
    }

    [Fact]
    public async Task Without_the_mapping_a_namespaced_token_has_no_roles()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Auth0StyleToken("admin"));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/workspaces")).StatusCode);
    }
}
