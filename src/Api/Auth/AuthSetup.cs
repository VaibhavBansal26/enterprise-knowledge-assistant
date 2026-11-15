using System.ComponentModel.DataAnnotations;
using Application.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The OIDC issuer (Keycloak realm URL). Tokens must carry exactly this <c>iss</c>.</summary>
    [Required, Url]
    public string Authority { get; init; } = string.Empty;

    /// <summary>
    /// Optional discovery document URL when the API reaches the identity provider on a different
    /// address than browsers do (e.g. inside Docker). Defaults to <c>{Authority}/.well-known/openid-configuration</c>.
    /// </summary>
    public string? MetadataAddress { get; init; }

    /// <summary>The <c>aud</c> the API accepts.</summary>
    [Required, MinLength(1)]
    public string Audience { get; init; } = string.Empty;

    public bool RequireHttpsMetadata { get; init; } = true;
}

public static class Policies
{
    public const string Read = "Read";
    public const string Write = "Write";
    public const string Admin = "Admin";
}

public static class AuthSetup
{
    public static IServiceCollection AddAppAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptionsWithValidateOnStart<AuthOptions>()
            .Bind(config.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured lazily from options so configuration overrides (tests, env vars) always apply.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                var settings = auth.Value;
                jwt.Authority = settings.Authority;
                if (!string.IsNullOrWhiteSpace(settings.MetadataAddress))
                {
                    jwt.MetadataAddress = settings.MetadataAddress;
                }

                jwt.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                jwt.MapInboundClaims = false; // keep claim names as issued: sub, roles, tenant_id

                // Browsers cannot set headers on a WebSocket, so SignalR passes the token in the query string.
                // Only honoured for hub paths; the logging redactor masks `access_token` everywhere else.
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Authority,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = "roles",
                    NameClaimType = "preferred_username",
                };
            });

        services.AddAuthorizationBuilder()
            // Secure by default: anything not explicitly [AllowAnonymous] needs a valid token.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Read, p => p.RequireRole(Roles.Admin, Roles.Editor, Roles.Viewer))
            .AddPolicy(Policies.Write, p => p.RequireRole(Roles.Admin, Roles.Editor))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));

        services.AddScoped<WorkspaceAccessEvaluator>();
        services.AddScoped<IAuthorizationHandler, WorkspaceAuthorizationHandler>();
        return services;
    }
}
