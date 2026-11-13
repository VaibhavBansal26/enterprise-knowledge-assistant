using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Options are validated at startup so misconfiguration fails fast.
        services.AddOptionsWithValidateOnStart<DatabaseOptions>()
            .Bind(config.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<RedisOptions>()
            .Bind(config.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations();

        // Connection details are resolved lazily from options, so configuration
        // overrides (tests, environment variables) always take effect.
        services.AddSingleton(sp =>
        {
            var builder = new NpgsqlDataSourceBuilder(
                sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString);
            // Required to map Dictionary<string,string> (document metadata) to jsonb.
            builder.EnableDynamicJson();
            return builder.Build();
        });
        services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(
                sp.GetRequiredService<IOptions<RedisOptions>>().Value.ConnectionString + ",abortConnect=false"));

        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .UseSnakeCaseNamingConvention());

        services.AddHealthChecks()
            .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgres", tags: [ReadyTag])
            .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), name: "redis", tags: [ReadyTag]);

        return services;
    }
}
