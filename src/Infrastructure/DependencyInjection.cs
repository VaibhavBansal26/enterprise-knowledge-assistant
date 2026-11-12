using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

        var db = config.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new();
        var redis = config.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new();

        services.AddNpgsqlDataSource(db.ConnectionString);
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redis.ConnectionString + ",abortConnect=false"));

        services.AddHealthChecks()
            .AddNpgSql(db.ConnectionString, name: "postgres", tags: [ReadyTag])
            .AddRedis(redis.ConnectionString, name: "redis", tags: [ReadyTag]);

        return services;
    }
}
