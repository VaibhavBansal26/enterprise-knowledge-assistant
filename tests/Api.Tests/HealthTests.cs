using System.Net;

namespace Api.Tests;

[Collection(ApiFixtureGroup.Name)]
public sealed class HealthTests(ApiFactory factory)
{
    [Fact]
    public async Task Liveness_returns_200_without_touching_dependencies()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ping_returns_pong()
    {
        var client = factory.CreateClient();

        var body = await client.GetStringAsync("/api/v1/ping");

        Assert.Contains("pong", body, StringComparison.Ordinal);
    }
}
