using Application.Abstractions;
using Application.Ingestion;
using Microsoft.AspNetCore.SignalR;
using Application.Chat;
using Infrastructure.Auditing;
using Infrastructure.Chat;
using Infrastructure.Llm;
using Infrastructure.Ingestion;
using Infrastructure.Storage;
using Infrastructure.Persistence;
using Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
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
        AddChat(services, config);

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
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        services.AddSingleton<OutboxStore>();
        services.AddScoped<DocumentIngestionProcessor>();
        services.AddHostedService<OutboxWorker>();

        // Hosts that can push to clients (the API's SignalR hub) replace this no-op.
        services.TryAddSingleton<IIngestionNotifier, NullIngestionNotifier>();
    }

    private static void AddChat(IServiceCollection services, IConfiguration config)
    {
        services.AddOptionsWithValidateOnStart<ChatOptions>()
            .Bind(config.GetSection(ChatOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<LlmOptions>()
            .Bind(config.GetSection(LlmOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<EmbeddingsOptions>()
            .Bind(config.GetSection(EmbeddingsOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
        services.AddSingleton<IValidateOptions<EmbeddingsOptions>, EmbeddingsOptionsValidator>();

        // Language model: singleton, because the Anthropic client owns the connection pool and the circuit breaker's state.
        services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider == LlmOptions.AnthropicProvider
            ? new AnthropicLlmClient(sp.GetRequiredService<IOptions<LlmOptions>>())
            : new ExtractiveLlmClient());

        // Embeddings: chosen by configuration. The OpenAI-compatible client gets a Polly resilience pipeline.
        services.AddOpenAiCompatibleEmbeddings();
        services.AddScoped<IEmbeddingClient>(sp =>
            sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value.Provider == EmbeddingsOptions.OpenAiProvider
                ? sp.GetRequiredService<OpenAiCompatibleEmbeddingClient>()
                : new HashingEmbeddingClient());

        services.AddSingleton<IRetrievalService, PostgresRetrievalService>();
        services.AddSingleton<ISemanticCache, RedisSemanticCache>();
        services.AddScoped<ChatService>();
    }

    /// <summary>
    /// Registers the OpenAI-compatible embeddings client with its resilience pipeline: retry with exponential backoff and
    /// jitter (a 429 waits for <c>Retry-After</c>), a circuit breaker, and per-attempt plus total timeouts, all driven
    /// by <see cref="EmbeddingsOptions"/>.
    /// </summary>
    public static IHttpClientBuilder AddOpenAiCompatibleEmbeddings(this IServiceCollection services)
    {
        var builder = services.AddHttpClient<OpenAiCompatibleEmbeddingClient>((sp, http) =>
        {
            var settings = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value.OpenAi;
            http.BaseAddress = new Uri(settings.BaseUrl.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
        });
        builder.AddStandardResilienceHandler();
        services.AddOptions<HttpStandardResilienceOptions>("OpenAiCompatibleEmbeddingClient-standard")
            .Configure<IOptions<EmbeddingsOptions>>((resilience, embeddingOptions) =>
            {
                var settings = embeddingOptions.Value.OpenAi;
                var attempt = TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds);
                resilience.Retry.MaxRetryAttempts = settings.MaxRetryAttempts;
                resilience.Retry.Delay = TimeSpan.FromMilliseconds(settings.RetryDelayMs);
                resilience.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
                resilience.Retry.UseJitter = true;
                resilience.Retry.ShouldRetryAfterHeader = true; // a 429 with Retry-After waits as the provider asks
                resilience.AttemptTimeout.Timeout = attempt;
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(settings.TotalTimeoutSeconds);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, settings.AttemptTimeoutSeconds * 2));
                resilience.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(settings.BreakSeconds);
                resilience.CircuitBreaker.MinimumThroughput = settings.BreakerMinimumThroughput;
                resilience.CircuitBreaker.FailureRatio = settings.BreakerFailureRatio;
            });
        return builder;
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
