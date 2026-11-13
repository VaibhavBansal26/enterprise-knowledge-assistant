using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Api.Tests;

/// <summary>Boots the API against real, throwaway PostgreSQL (pgvector) and Redis containers.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AllowedOrigin = "http://localhost:5173";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>A client that sends a valid access token for <paramref name="user"/>.</summary>
    public HttpClient CreateClientAs(TestUser user) => CreateClientWithToken(TestAuth.Token(user));

    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Tenant A administrator: the default identity for tests that are not about authorization.</summary>
    public HttpClient AdminClient() => CreateClientAs(TestAuth.AdminA);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());
        builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin);
        builder.UseSetting("Auth:Authority", TestAuth.Issuer);
        builder.UseSetting("Auth:Audience", TestAuth.Audience);
        // High enough that ordinary tests never trip it; the rate-limit tests lower it on their own host.
        builder.UseSetting("RateLimiting:PermitLimit", "100000");

        // Trust our test signing key instead of fetching keys from an identity provider.
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = TestAuth.Issuer });
                options.TokenValidationParameters.IssuerSigningKey = TestAuth.Key;
            }));
    }
}

[CollectionDefinition(Name)]
public sealed class ApiFixtureGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";
}
