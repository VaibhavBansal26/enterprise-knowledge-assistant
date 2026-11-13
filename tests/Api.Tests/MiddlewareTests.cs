using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Workspaces;
using Microsoft.AspNetCore.Hosting;

namespace Api.Tests;

[Collection(ApiFixtureGroup.Name)]
public sealed partial class MiddlewareTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.AdminClient();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HexId();

    // ---- Correlation ID ----

    [Fact]
    public async Task Response_has_generated_correlation_id_when_none_supplied()
    {
        var response = await _client.GetAsync("/api/v1/ping");

        var id = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.Matches(HexId(), id);
    }

    [Fact]
    public async Task Valid_inbound_correlation_id_is_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        request.Headers.Add("X-Correlation-Id", "trace-abc_123.x");

        var response = await _client.SendAsync(request);

        Assert.Equal("trace-abc_123.x", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
    }

    [Fact]
    public async Task Malformed_inbound_correlation_id_is_replaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        request.Headers.Add("X-Correlation-Id", "bad/value with spaces");

        var response = await _client.SendAsync(request);

        Assert.Matches(HexId(), Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
    }

    [Fact]
    public async Task Problem_details_carry_the_correlation_id()
    {
        var created = await (await _client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest($"cid-{Guid.NewGuid():N}", null)))
            .Content.ReadFromJsonAsync<WorkspaceResponse>();
        await _client.PutAsJsonAsync(
            $"/api/v1/workspaces/{created!.Id}", new UpdateWorkspaceRequest("a", null, created.Version));

        // Stale version -> 409 produced by the exception handler pipeline.
        var stale = await _client.PutAsJsonAsync(
            $"/api/v1/workspaces/{created.Id}", new UpdateWorkspaceRequest("b", null, created.Version));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var header = Assert.Single(stale.Headers.GetValues("X-Correlation-Id"));
        using var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(header, body.RootElement.GetProperty("correlationId").GetString());
    }

    // ---- Security headers and CORS ----

    [Fact]
    public async Task Security_headers_are_present()
    {
        var response = await _client.GetAsync("/api/v1/ping");

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cors_preflight_allows_the_configured_origin_only()
    {
        using var allowed = Preflight(ApiFactory.AllowedOrigin);
        using var denied = Preflight("https://evil.example");

        var ok = await _client.SendAsync(allowed);
        var no = await _client.SendAsync(denied);

        Assert.Equal(ApiFactory.AllowedOrigin, Assert.Single(ok.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(no.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/workspaces");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key");
        return request;
    }

    // ---- Rate limiting ----

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after_once_exceeded()
    {
        using var limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:PermitLimit", "3");
            b.UseSetting("RateLimiting:KeyPrefix", $"rl-test-{Guid.NewGuid():N}");
        });
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 4; i++)
        {
            last = await client.GetAsync("/api/v1/ping");
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.True(last.Headers.Contains("Retry-After"));
        Assert.Equal("0", Assert.Single(last.Headers.GetValues("X-RateLimit-Remaining")));
        using var body = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Health_endpoints_are_exempt_from_rate_limiting()
    {
        using var limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:PermitLimit", "1");
            b.UseSetting("RateLimiting:KeyPrefix", $"rl-test-{Guid.NewGuid():N}");
        });
        var client = limited.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Rate_limiter_fails_open_when_redis_is_unreachable()
    {
        using var broken = factory.WithWebHostBuilder(b => b.UseSetting("Redis:ConnectionString", "localhost:1"));
        var client = broken.CreateClient();

        var response = await client.GetAsync("/api/v1/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Idempotency ----

    private static HttpRequestMessage CreateWorkspace(string name, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workspaces")
        {
            Content = JsonContent.Create(new CreateWorkspaceRequest(name, null)),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task Same_key_and_payload_replays_the_first_response_without_creating_again()
    {
        var name = $"idem-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();

        var first = await _client.SendAsync(CreateWorkspace(name, key));
        var second = await _client.SendAsync(CreateWorkspace(name, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", Assert.Single(second.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        var list = await _client.GetStringAsync($"/api/v1/workspaces?q={name}");
        using var json = JsonDocument.Parse(list);
        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Same_key_with_a_different_payload_is_rejected_with_422()
    {
        var key = Guid.NewGuid().ToString();
        await _client.SendAsync(CreateWorkspace($"idem-{Guid.NewGuid():N}", key));

        var second = await _client.SendAsync(CreateWorkspace($"idem-{Guid.NewGuid():N}", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_key_are_not_deduplicated()
    {
        var name = $"idem-{Guid.NewGuid():N}";

        await _client.SendAsync(CreateWorkspace(name, null));
        await _client.SendAsync(CreateWorkspace(name, null));

        using var json = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/workspaces?q={name}"));
        Assert.Equal(2, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Malformed_idempotency_key_is_rejected_with_400()
    {
        var response = await _client.SendAsync(CreateWorkspace("x", "not valid!!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Idempotent_get_requests_are_unaffected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        request.Headers.Add("Idempotency-Key", "ignored-on-get");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
    }
}
