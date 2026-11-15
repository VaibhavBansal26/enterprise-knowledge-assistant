using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` tooling; never connects to a database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=kb_design_time", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, new TenantContext());
    }
}
