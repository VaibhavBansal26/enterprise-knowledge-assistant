using System.ComponentModel.DataAnnotations;

namespace Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required, MinLength(1)]
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required, MinLength(1)]
    public string ConnectionString { get; init; } = string.Empty;
}
