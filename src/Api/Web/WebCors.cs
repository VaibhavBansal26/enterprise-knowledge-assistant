using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Api.Web;

public sealed class WebCorsSettings
{
    public const string SectionName = "Cors";
    public const string PolicyName = "web";

    /// <summary>Browser origins allowed to call the API (the React app).</summary>
    [Required, MinLength(1)]
    public string[] AllowedOrigins { get; init; } = [];
}

public static class WebCorsExtensions
{
    public static IServiceCollection AddWebCors(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptionsWithValidateOnStart<WebCorsSettings>()
            .Bind(config.GetSection(WebCorsSettings.SectionName))
            .ValidateDataAnnotations();

        services.AddCors();

        // Configured lazily from options so configuration overrides (tests, env vars) always apply.
        services.AddOptions<CorsOptions>().Configure<IOptions<WebCorsSettings>>((cors, settings) =>
            cors.AddPolicy(WebCorsSettings.PolicyName, policy => policy
                .WithOrigins(settings.Value.AllowedOrigins)
                .WithMethods("GET", "POST", "PUT", "DELETE")
                .WithHeaders("Content-Type", "Idempotency-Key", CorrelationIdMiddleware.HeaderName)
                .WithExposedHeaders(
                    CorrelationIdMiddleware.HeaderName,
                    IdempotencyMiddleware.ReplayHeader,
                    "Retry-After",
                    "X-RateLimit-Limit",
                    "X-RateLimit-Remaining")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }
}
