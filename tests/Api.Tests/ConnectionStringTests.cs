using Infrastructure;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace Api.Tests;

public class ConnectionStringTests
{
    [Fact]
    public void Postgres_uri_becomes_npgsql_keywords()
    {
        var converted = ConnectionStrings.Postgres("postgres://kbuser:s3cret@dpg-abc.oregon-postgres.render.com:5432/kb_db");
        var b = new NpgsqlConnectionStringBuilder(converted);

        Assert.Equal("dpg-abc.oregon-postgres.render.com", b.Host);
        Assert.Equal("kb_db", b.Database);
        Assert.Equal("kbuser", b.Username);
        Assert.Equal("s3cret", b.Password);
    }

    [Fact]
    public void Postgres_password_with_special_characters_cannot_inject_keywords()
    {
        var converted = ConnectionStrings.Postgres("postgresql://u:p%3BHost%3Devil%27x@db:5432/kb");
        var b = new NpgsqlConnectionStringBuilder(converted);

        Assert.Equal("db", b.Host);
        Assert.Equal("p;Host=evil'x", b.Password);
    }

    [Theory]
    [InlineData("postgresql://u:p@ep-1.neon.tech/neondb?sslmode=require&channel_binding=require", Npgsql.SslMode.Require)]
    [InlineData("postgresql://u:p@db/kb?sslmode=disable", Npgsql.SslMode.Disable)]
    [InlineData("postgresql://u:p@db/kb", Npgsql.SslMode.Prefer)]
    public void Postgres_uri_honours_sslmode(string uri, Npgsql.SslMode expected)
    {
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(ConnectionStrings.Postgres(uri)).SslMode);
    }

    [Theory]
    [InlineData("Host=localhost;Port=5433;Database=kb;Username=kb;Password=x")]
    [InlineData("localhost:6380")]
    [InlineData("redis:6379")] // host:port where the host happens to be called "redis"
    [InlineData("postgres:5432")]
    public void Existing_formats_pass_through(string value)
    {
        Assert.Equal(value, ConnectionStrings.Postgres(value));
        Assert.Equal(value, ConnectionStrings.Redis(value));
    }

    [Fact]
    public void Redis_uri_becomes_stackexchange_options()
    {
        var parsed = ConfigurationOptions.Parse(ConnectionStrings.Redis("rediss://default:tok%2Fen@red-1.render.com:6380"));

        Assert.True(parsed.Ssl);
        Assert.Equal("tok/en", parsed.Password);
        Assert.Contains(parsed.EndPoints, e => e.ToString()!.EndsWith("red-1.render.com:6380", StringComparison.Ordinal));
    }

    [Fact]
    public void Redis_uri_without_port_uses_default()
    {
        Assert.Equal("red-1:6379", ConnectionStrings.Redis("redis://red-1"));
    }
}
