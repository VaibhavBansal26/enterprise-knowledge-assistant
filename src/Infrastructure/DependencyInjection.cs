using Application.Abstractions;
using Application.Ingestion;
using Microsoft.AspNetCore.SignalR;
using Infrastructure.Auditing;
using Infrastructure.Ingestion;
using Infrastructure.Storage;
using Infrastructure.Persistence;
using Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
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
            builder.UseVector(); // pgvector type mapping
            return builder.Build();
        });
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var redis = ConfigurationOptions.Parse(
                sp.GetRequiredService<IOptions<RedisOptions>>().Value.ConnectionString);
            redis.AbortOnConnectFail = false; // start even if Redis is briefly down; /health/ready reports it
            redis.ConnectTimeout = 2000;
            redis.SyncTimeout = 1000;
            redis.AsyncTimeout = 1000; // fail fast so a Redis outage cannot stall every request
            return ConnectionMultiplexer.Connect(redis);
        });

        services.AddOptionsWithValidateOnStart<RateLimitOptions>()
            .Bind(config.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<IdempotencyOptions>()
            .Bind(config.GetSection(IdempotencyOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

        // One tenant holder per request; the middleware sets it and the DbContext's query filters read it.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

        AddIngestion(services, config);

        services.AddHealthChecks()
            .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgres", tags: [ReadyTag])
            .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), name: "redis", tags: [ReadyTag]);

        return services;
    }

    private static void AddIngestion(IServiceCollection services, IConfiguration config)
    {
        services.AddOptionsWithValidateOnStart<StorageOptions>()
            .Bind(config.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<IngestionOptions>()
            .Bind(config.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IBlobStorage, FileSystemBlobStorage>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddSingleton<IEmbeddingClient, HashingEmbeddingClient>();
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        services.AddSingleton<OutboxStore>();
        services.AddScoped<DocumentIngestionProcessor>();
        services.AddHostedService<OutboxWorker>();

        // Hosts that can push to clients (the API's SignalR hub) replace this no-op.
        services.TryAddSingleton<IIngestionNotifier, NullIngestionNotifier>();
    }

    /// <summary>Shares SignalR messages between API instances through Redis, so a worker on one instance can reach a client on another.</summary>
    public static ISignalRServerBuilder AddRedisBackplane(this ISignalRServerBuilder signalR)
    {
        ArgumentNullException.ThrowIfNull(signalR);

        signalR.AddStackExchangeRedis();
        signalR.Services.AddOptions<Microsoft.AspNetCore.SignalR.StackExchangeRedis.RedisOptions>()
            .Configure<IOptions<RedisOptions>>((backplane, redis) =>
            {
                backplane.Configuration = ConfigurationOptions.Parse(redis.Value.ConnectionString);
                backplane.Configuration.AbortOnConnectFail = false;
                backplane.Configuration.ChannelPrefix = RedisChannel.Literal("kb");
            });
        return signalR;
    }
}

internal sealed class NullIngestionNotifier : IIngestionNotifier
{
    public Task DocumentStatusChangedAsync(DocumentStatusChanged change, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
