using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Api.Tests;

/// <summary>Boots the API against a real, throwaway PostgreSQL (pgvector) container.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        // Redis is not exercised by these tests; the connection is created lazily and never resolved.
        builder.UseSetting("Redis:ConnectionString", "localhost:6380");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiFixtureGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";
}
