using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Api.Tests;

public sealed class HealthTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
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

        Assert.Contains("pong", body);
    }
}
