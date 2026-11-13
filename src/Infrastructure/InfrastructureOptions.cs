using System.ComponentModel.DataAnnotations;

namespace Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required, MinLength(1)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Apply pending EF Core migrations when the API starts (dev and test only).</summary>
    public bool MigrateOnStartup { get; init; }
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required, MinLength(1)]
    public string ConnectionString { get; init; } = string.Empty;
}
